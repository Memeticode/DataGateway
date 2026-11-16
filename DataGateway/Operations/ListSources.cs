using System.Text.Json;
using DataGateway.Failures;
using DataGateway.Core.Catalog;


namespace DataGateway.Operations;

internal static class ListSources
{
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
