using System.Text.Json;
using DataGateway.Failures;
using DataGateway.Core.Catalog;
using SqlKata.JsonQuery;

namespace DataGateway.Operations;

/// <summary>The describe operation: parses <c>{"source": name}</c>, loads that source and answers with its
/// metadata and query schema.</summary>
/// <remarks>Shared by the HTTP route and the MCP tool, which differ only in how the body arrives and the answer
/// leaves.</remarks>
internal static class DescribeSource
{
    /// <summary>Runs one describe request.</summary>
    /// <remarks>The query schema states the limits the query operation will apply, with the source's own row cap as the
    /// limit's maximum.</remarks>
    /// <param name="body">The request body as UTF-8 JSON.</param>
    /// <param name="readSource">Loads a source's contract from the catalog.</param>
    /// <param name="limits">The gateway's query limits, without a row cap.</param>
    /// <param name="trail">Records the source for the failure log.</param>
    /// <param name="token">Cancels the catalog read.</param>
    /// <returns>The description, or the failure.</returns>
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
