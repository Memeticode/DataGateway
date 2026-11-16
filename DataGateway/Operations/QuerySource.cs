using System.Text.Json;
using DataGateway.Failures;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using SqlKata.JsonQuery;

namespace DataGateway.Operations;

internal static class QuerySource
{
    // The body nests the query one level down. The library parses the query text at System.Text.Json's default
    // depth of 64, so the body allows 65 and never refuses a query the library would judge.
    private static readonly JsonDocumentOptions BodyOptions = new() { MaxDepth = 65 };

    internal static async Task<CatalogResult<QueryResponse>> RunAsync(byte[] body,
        Func<string, CancellationToken, Task<CatalogResult<QuerySourceContext>>> readTarget,
        JsonQueryLimits limits, RequestTrail trail, CancellationToken token)
    {
        CatalogResult<QueryRequest> request;
        try
        {
            using var document = JsonDocument.Parse(new MemoryStream(body, writable: false), BodyOptions);
            request = QueryAnswer.ParseRequest(document.RootElement);
        }
        catch (JsonException)
        {
            request = CatalogResult<QueryRequest>.Reject(QueryAnswer.InvalidJson with { ExceptionType = "JsonException" });
        }
        if (request.Failure is { } invalid) { return CatalogResult<QueryResponse>.Reject(invalid); }

        var loaded = await readTarget(request.Value.Source, token);
        if (loaded.Failure is { } unread) { return CatalogResult<QueryResponse>.Reject(unread); }

        var source = loaded.Value.Source;
        var runner = loaded.Value.Runner;
        trail.Source = source;
        var validated = QueryBuilding.Validate(source, request.Value.Query,
            limits with { MaxLimit = source.Source.RowCap });
        if (validated.Failure is { } refused) { return CatalogResult<QueryResponse>.Reject(QueryAnswer.RootPath(refused)); }

        var compiled = runner.Compile(validated.Value);
        trail.Query = compiled;
        var fetched = await runner.RunAsync(source, compiled, token);
        if (fetched.Failure is { } failed) { return CatalogResult<QueryResponse>.Reject(failed); }

        return CatalogResult<QueryResponse>.Success(QueryAnswer.Map(source, validated.Value, fetched.Value));
    }
}
