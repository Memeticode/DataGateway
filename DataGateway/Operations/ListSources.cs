using System.Text.Json;
using DataGateway.Failures;
using DataGateway.Core.Catalog;


namespace DataGateway.Operations;

/// <summary>The list operation: checks that the body is an empty object and answers with every active source's
/// summary.</summary>
/// <remarks>Shared by the HTTP route and the MCP tool.</remarks>
internal static class ListSources
{
    /// <summary>Runs one list request.</summary>
    /// <param name="body">The request body as UTF-8 JSON.</param>
    /// <param name="readList">Reads the checked source list from the catalog.</param>
    /// <param name="token">Cancels the catalog read.</param>
    /// <returns>The source list, or the failure.</returns>
    internal static async Task<CatalogResult<SourceListResponse>> RunAsync(byte[] body,
        Func<CancellationToken, Task<CatalogResult<IReadOnlyList<SourceListRow>>>> readList, CancellationToken token)
    {
        CatalogFailure? invalid;
        try
        {
            using var document = JsonDocument.Parse(new MemoryStream(body, writable: false), default);
            invalid = SourceListing.CheckRequest(document.RootElement);
        }
        catch (JsonException) { invalid = SourceListing.InvalidJson with { ExceptionType = "JsonException" }; }
        if (invalid is not null)
        {
            return CatalogResult<SourceListResponse>.Reject(invalid);
        }
        var result = await readList(token);
        if (result.Failure is { } failure) { return CatalogResult<SourceListResponse>.Reject(failure); }
        return CatalogResult<SourceListResponse>.Success(SourceListing.Map(result.Value));
    }
}
