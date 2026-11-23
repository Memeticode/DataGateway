using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using DataGateway.Data.Queries;
using DataGateway.Operations;
using DataGateway.Mcp;

namespace DataGateway;

/// <summary>The gateway's view of the application's catalog for one invocation: reads the MCP metadata, the source
/// list and one source's detail, checks each against the catalog rules, and turns every fault into a
/// <see cref="CatalogFailure"/>.</summary>
/// <remarks>
/// <para>One adapter per invocation; the registration supplies lazily bound application delegates. Nothing is
/// cached between invocations, so a catalog edit applies to the next request.</para>
/// <para>The application's code runs only inside <see cref="ReadAsync{T}"/>, so no exception it throws reaches a route
/// or a tool: each becomes a coded failure with its type name.</para>
/// </remarks>
/// <param name="list">Reads the source list.</param>
/// <param name="detail">Reads one source's detail by the name the caller sent.</param>
/// <param name="runners">The query runner for each registered engine name.</param>
/// <param name="metadata">Reads the MCP server and tool text.</param>
internal sealed class GatewayCatalog(
    Func<CancellationToken, Task<IReadOnlyList<SourceListRow>>> list,
    Func<string, CancellationToken, Task<CatalogDetail?>> detail,
    IReadOnlyDictionary<string, QueryRunner> runners,
    Func<CancellationToken, Task<McpMetadata>> metadata)
{
    /// <summary>The caller-facing message for a catalog that cannot be read or holds invalid MCP metadata.</summary>
    /// <remarks>Diagnostic detail stays in the log. The caller is told to stop rather than retry: the same read
    /// fails the same way until an operator fixes the catalog.</remarks>
    internal const string UnavailableMessage = "Cannot read the catalog. Check catalog availability and the configured catalog connection. Tell the user, give the incidentId and stop; do not retry.";
    /// <summary>Set when this invocation's MCP metadata read failed; later list and source reads then refuse
    /// with it.</summary>
    /// <remarks>Only the MCP endpoint reads metadata, and it shares one adapter across a stateless POST. A
    /// <c>tools/call</c> made while the metadata is unreadable therefore answers with that failure as a tool error, not
    /// with data. HTTP routes never read metadata, so it stays null for them.</remarks>
    private CatalogFailure? metadataFailure;

    /// <summary>Reads and checks the MCP server and tool text.</summary>
    /// <remarks>The returned failure carries the validation detail, for the log and the startup message; the stored
    /// one carries <see cref="UnavailableMessage"/>, for callers.</remarks>
    /// <param name="gateway">The gateway name, for the refusal messages.</param>
    /// <param name="settings">The tool prefixes, applied before the final descriptions are measured.</param>
    /// <param name="token">Cancels the read.</param>
    /// <returns>The checked metadata, or the failure.</returns>
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

    /// <summary>Reads and checks the source list for the list operation.</summary>
    /// <param name="token">Cancels the read.</param>
    /// <returns>Every source row, active or not, or the failure.</returns>
    internal async Task<CatalogResult<IReadOnlyList<SourceListRow>>> ListAsync(CancellationToken token)
    {
        var sources = await ReadSourcesAsync(token);
        if (sources.IsSuccess && metadataFailure is { } invalidMetadata)
            return CatalogResult<IReadOnlyList<SourceListRow>>.Reject(invalidMetadata);
        return sources;
    }

    /// <summary>Reads the source list and refuses it on the first rule it breaks.</summary>
    /// <param name="token">Cancels the read.</param>
    /// <returns>The checked rows, or the failure.</returns>
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

    /// <summary>Loads one source's contract for the describe operation.</summary>
    /// <param name="sourceName">The source name the caller sent.</param>
    /// <param name="token">Cancels the read.</param>
    /// <returns>The source contract, or the failure.</returns>
    internal async Task<CatalogResult<SourceContract>> SourceAsync(string sourceName, CancellationToken token)
    {
        var result = await TargetAsync(sourceName, token);
        return result.Failure is { } failure
            ? CatalogResult<SourceContract>.Reject(failure)
            : CatalogResult<SourceContract>.Success(result.Value.Source);
    }

    /// <summary>Loads one source's contract and the runner of its engine, for the query operation.</summary>
    /// <param name="sourceName">The source name the caller sent.</param>
    /// <param name="token">Cancels the read.</param>
    /// <returns>The source and its runner, or the failure.</returns>
    internal Task<CatalogResult<QuerySourceContext>> TargetAsync(string sourceName, CancellationToken token) =>
        LoadAsync(sourceName, requiredActive: false, token);

    /// <summary>Reads one source's detail, checks and maps it, and finds its engine.</summary>
    /// <param name="sourceName">The source name as sent, or as listed during startup.</param>
    /// <param name="requiredActive">True at startup, where an active listed source without an active detail is a
    /// catalog error; false for callers, who get <c>UnknownSource</c>.</param>
    /// <param name="token">Cancels the reads.</param>
    /// <returns>The source and its runner, or the failure.</returns>
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

    /// <summary>The failure for a source with no active detail.</summary>
    /// <remarks>A caller gets <c>UnknownSource</c> naming the active sources, so the next call can use a real
    /// name; that is why the list is read again here.</remarks>
    /// <param name="sourceName">The source name.</param>
    /// <param name="requiredActive">True at startup; see <see cref="LoadAsync"/>.</param>
    /// <param name="token">Cancels the list read.</param>
    /// <param name="sourceId">The catalog id, when a detail row existed, for the log.</param>
    /// <returns>A rejected result.</returns>
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

    /// <summary>The startup check: MCP metadata, the source list and every active source's detail.</summary>
    /// <param name="gatewayName">The gateway name, for the message.</param>
    /// <param name="settings">The tool prefixes.</param>
    /// <param name="token">Cancels startup.</param>
    /// <returns>A task that fails with the first problem found.</returns>
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

    /// <summary>The exception that stops startup, naming the gateway, the source, the path and the fix.</summary>
    /// <remarks>Carries no inner exception, in keeping with <see cref="ReadAsync{T}"/>: a failure keeps only the
    /// exception's type name.</remarks>
    /// <param name="name">The gateway name.</param>
    /// <param name="failure">The first failure found.</param>
    /// <returns>The exception to throw.</returns>
    private static InvalidOperationException StartupFailure(string name, CatalogFailure failure)
    {
        var source = failure.SourceName is null ? "" : $"Source '{failure.SourceName}'. ";
        var exceptionType = failure.ExceptionType is null ? "" : $" ({failure.ExceptionType})";
        return new InvalidOperationException($"Gateway '{name}'. {source}{failure.Path} {failure.Message}{exceptionType}", innerException: null);
    }

    /// <summary>Invokes one application catalog read and turns any exception into a coded failure.</summary>
    /// <remarks>Only application invocation and await are inside this boundary. No exception object survives it.
    /// Cancellation, seen as an exception or on the token, becomes <c>CatalogCancelled</c>; invalid MCP metadata
    /// and a source's invalid query timeout keep their own messages; everything else is <c>CatalogUnavailable</c>.</remarks>
    /// <typeparam name="T">The read's result type.</typeparam>
    /// <param name="invoke">Starts the application's read.</param>
    /// <param name="path">The failure path for an unavailable or cancelled read.</param>
    /// <param name="sourceName">The source being read, or null for list and metadata reads.</param>
    /// <param name="token">The token the read observes.</param>
    /// <returns>The value, or the failure.</returns>
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
