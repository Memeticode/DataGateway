using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using DataGateway.Data.Queries;
using DataGateway.Operations;
using DataGateway.Mcp;

namespace DataGateway;

// One adapter per invocation; the registration supplies lazily bound application delegates.
internal sealed class GatewayCatalog(
    Func<CancellationToken, Task<IReadOnlyList<SourceListRow>>> list,
    Func<string, CancellationToken, Task<CatalogDetail?>> detail,
    IReadOnlyDictionary<string, QueryRunner> runners,
    Func<CancellationToken, Task<McpMetadata>> metadata)
{
    internal const string UnavailableMessage = "Cannot read the catalog. Check catalog availability and the configured catalog connection. Tell the user, give the incidentId and stop; do not retry.";
    private CatalogFailure? metadataFailure;

    internal async Task<CatalogResult<McpMetadata>> MetadataAsync(string gateway, McpToolSettings settings, CancellationToken token)
    {
        var read = await ReadAsync(() => metadata(token), "$", null, token);
        if (read.Failure is { } unavailable)
        {
            metadataFailure = unavailable.Code == DataGatewayError.CatalogUnavailable.Code
                ? unavailable with { Message = UnavailableMessage }
                : unavailable;
            return CatalogResult<McpMetadata>.Reject(unavailable);
        }
        try
        {
            McpMetadataValidation.Validate(read.Value, settings, reason => new InvalidOperationException($"Gateway '{gateway}'. {reason}"));
        }
        catch (InvalidOperationException error)
        {
            metadataFailure = new(DataGatewayError.CatalogUnavailable.Code, "$", UnavailableMessage) { Step = "catalog" };
            return CatalogResult<McpMetadata>.Reject(metadataFailure with { Message = error.Message });
        }
        ArgumentNullException.ThrowIfNull(read.Value);
        return CatalogResult<McpMetadata>.Success(read.Value);
    }

    internal async Task<CatalogResult<IReadOnlyList<SourceListRow>>> ListAsync(CancellationToken token)
    {
        var sources = await ReadSourcesAsync(token);
        if (sources.IsSuccess && metadataFailure is { } invalidMetadata)
            return CatalogResult<IReadOnlyList<SourceListRow>>.Reject(invalidMetadata);
        return sources;
    }

    private async Task<CatalogResult<IReadOnlyList<SourceListRow>>> ReadSourcesAsync(CancellationToken token)
    {
        var read = await ReadAsync(() => list(token), "$", null, token);
        if (read.Failure is { } unavailable) return CatalogResult<IReadOnlyList<SourceListRow>>.Reject(unavailable);
        var rows = read.Value;
        var failures = CatalogValidator.ValidateList(rows);
        if (failures.Count > 0)
            return CatalogResult<IReadOnlyList<SourceListRow>>.Reject(failures[0] with { Step = "catalog" });
        ArgumentNullException.ThrowIfNull(rows);
        return CatalogResult<IReadOnlyList<SourceListRow>>.Success(rows);
    }

    internal async Task<CatalogResult<SourceContract>> SourceAsync(string sourceName, CancellationToken token)
    {
        var result = await TargetAsync(sourceName, token);
        return result.Failure is { } failure
            ? CatalogResult<SourceContract>.Reject(failure)
            : CatalogResult<SourceContract>.Success(result.Value.Source);
    }

    internal Task<CatalogResult<QuerySourceContext>> TargetAsync(string sourceName, CancellationToken token) =>
        LoadAsync(sourceName, requiredActive: false, token);

    private async Task<CatalogResult<QuerySourceContext>> LoadAsync(string sourceName, bool requiredActive, CancellationToken token)
    {
        var read = await ReadAsync(() => detail(sourceName, token), "$.source", sourceName, token);
        if (read.Failure is { } unread) return CatalogResult<QuerySourceContext>.Reject(unread);
        var checkedDetail = read.Value;
        var failures = CatalogValidator.ValidateDetail(sourceName, checkedDetail);
        if (failures.Count > 0)
            return CatalogResult<QuerySourceContext>.Reject(failures[0] with
            {
                Step = "catalog", SourceName = failures[0].SourceName ?? sourceName,
                SourceId = checkedDetail?.Rows?.Source?.Id
            });
        if (checkedDetail is null) return await MissingAsync(sourceName, requiredActive, token);
        var mapped = CatalogMapping.Map(checkedDetail.Rows);
        if (mapped.Failure is { } invalid)
        {
            if (invalid.Code == DataGatewayError.UnknownSource.Code)
                return await MissingAsync(sourceName, requiredActive, token, checkedDetail.Rows.Source.Id);
            return CatalogResult<QuerySourceContext>.Reject(invalid with
            {
                SourceName = sourceName, SourceId = checkedDetail.Rows.Source.Id, Step = "catalog"
            });
        }
        if (!runners.TryGetValue(checkedDetail.Provider, out var runner))
            return CatalogResult<QuerySourceContext>.Reject(new("InvalidCatalog", "$.detail.provider",
                $"Source '{sourceName}'. Register an engine named '{checkedDetail.Provider}' for this gateway.")
            {
                SourceName = sourceName, SourceId = mapped.Value.Id, Step = "catalog"
            });
        if (metadataFailure is { } invalidMetadata)
            return CatalogResult<QuerySourceContext>.Reject(invalidMetadata with { Path = "$.source", SourceName = sourceName });
        return CatalogResult<QuerySourceContext>.Success(new(mapped.Value, runner));
    }

    private async Task<CatalogResult<QuerySourceContext>> MissingAsync(string sourceName, bool requiredActive, CancellationToken token, int? sourceId = null)
    {
        if (requiredActive)
            return CatalogResult<QuerySourceContext>.Reject(new("InvalidCatalog", "$.detail",
                $"Source '{sourceName}'. Return an active detail for every active source in the list.")
            { SourceName = sourceName, SourceId = sourceId, Step = "catalog" });
        var sources = await ReadSourcesAsync(token);
        return CatalogResult<QuerySourceContext>.Reject(sources.Failure ??
            SourceListing.UnknownSource(sources.Value.Where(row => row.IsActive).Select(row => row.Name)) with
            { SourceName = sourceName, SourceId = sourceId, Step = "catalog" });
    }

    internal async Task ValidateAsync(string gatewayName, McpToolSettings settings, CancellationToken token)
    {
        var presentation = await MetadataAsync(gatewayName, settings, token);
        if (presentation.Failure is { } unavailable) throw StartupFailure(gatewayName, unavailable);
        var sources = await ListAsync(token);
        if (sources.Failure is { } unread) throw StartupFailure(gatewayName, unread);
        foreach (var source in sources.Value.Where(row => row.IsActive))
        {
            var result = await LoadAsync(source.Name, requiredActive: true, token);
            if (result.Failure is { } failure) throw StartupFailure(gatewayName, failure);
        }
    }

    private static InvalidOperationException StartupFailure(string name, CatalogFailure failure)
    {
        var source = failure.SourceName is null ? "" : $"Source '{failure.SourceName}'. ";
        var exceptionType = failure.ExceptionType is null ? "" : $" ({failure.ExceptionType})";
        return new InvalidOperationException($"Gateway '{name}'. {source}{failure.Path} {failure.Message}{exceptionType}", innerException: null);
    }

    // Only application invocation and await are inside this boundary. No exception object survives it.
    private static async Task<(T? Value, CatalogFailure? Failure)> ReadAsync<T>(
        Func<Task<T>> invoke, string path, string? sourceName, CancellationToken token) where T : class?
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var value = await invoke();
            token.ThrowIfCancellationRequested();
            return (value, null);
        }
        catch (Exception error)
        {
            var cancelled = error is OperationCanceledException || token.IsCancellationRequested;
            if (!cancelled && error is DataGateway.Data.Catalog.InvalidMcpMetadataException cell)
                return (default, new CatalogFailure(DataGatewayError.CatalogUnavailable.Code, "$", cell.Field + ": return the required MCP catalog projection.")
                { Step = "catalog", ExceptionType = error.GetType().Name });
            if (!cancelled && sourceName is not null && error is InvalidQueryTimeoutException)
                return (default, InvalidQueryTimeoutException.Failure(sourceName) with { Step = "catalog" });
            return (default, new CatalogFailure(
                cancelled ? DataGatewayError.CatalogCancelled.Code : DataGatewayError.CatalogUnavailable.Code, path,
                cancelled ? "The catalog read was cancelled. Submit a new request if still needed." :
                    UnavailableMessage)
            { Step = "catalog", SourceName = sourceName, ExceptionType = error.GetType().Name });
        }
    }
}
