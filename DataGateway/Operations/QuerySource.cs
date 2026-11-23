using System.Text.Json;
using DataGateway.Failures;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using SqlKata.JsonQuery;

namespace DataGateway.Operations;

/// <summary>The query operation: parses <c>{"source", "query"}</c>, validates the query against the source,
/// compiles it for the source's engine, runs it and answers with the rows.</summary>
/// <remarks>Shared by the HTTP route and the MCP tool. Each step records what it reached in the trail, so a later
/// failure's log line shows the source and the compiled SQL.</remarks>
internal static class QuerySource
{
    /// <summary>The body parser's options: one level deeper than the query library's own.</summary>
    /// <remarks>The body nests the query one level down. The library parses the query text at System.Text.Json's default
    /// depth of 64, so the body allows 65 and never refuses a query the library would judge.</remarks>
    private static readonly JsonDocumentOptions BodyOptions = new() { MaxDepth = 65 };

    /// <summary>Runs one query request.</summary>
    /// <param name="body">The request body as UTF-8 JSON.</param>
    /// <param name="readTarget">Loads a source's contract and its engine's runner from the catalog.</param>
    /// <param name="limits">The gateway's query limits; the source's row cap is added here.</param>
    /// <param name="trail">Records the source and the compiled query for the failure log.</param>
    /// <param name="token">Cancels the catalog read and the query.</param>
    /// <returns>The rows with their columns and hint, or the failure.</returns>
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
