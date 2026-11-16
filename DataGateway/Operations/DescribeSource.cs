using System.Text.Json;
using DataGateway.Failures;
using DataGateway.Core.Catalog;
using SqlKata.JsonQuery;

namespace DataGateway.Operations;

internal static class DescribeSource
{
    internal static async Task<CatalogResult<SourceDescribeResponse>> RunAsync(byte[] body,
        Func<string, CancellationToken, Task<CatalogResult<SourceContract>>> readSource,
        JsonQueryLimits limits, RequestTrail trail, CancellationToken token)
    {
        CatalogResult<string> request;
        try
        {
            using var document = JsonDocument.Parse(new MemoryStream(body, writable: false), default);
            request = SourceDescription.ParseRequest(document.RootElement);
        }
        catch (JsonException)
        {
            request = CatalogResult<string>.Reject(SourceDescription.InvalidJson with { ExceptionType = "JsonException" });
        }
        if (request.Failure is { } requestFailure)
        {
            return CatalogResult<SourceDescribeResponse>.Reject(requestFailure);
        }

        var result = await readSource(request.Value, token);
        if (result.Failure is { } failure)
        {
            return CatalogResult<SourceDescribeResponse>.Reject(failure);
        }
        trail.Source = result.Value;
        return CatalogResult<SourceDescribeResponse>.Success(SourceDescription.Map(result.Value, limits with { MaxLimit = result.Value.Source.RowCap }));
    }
}
