using DataGateway.Core.Errors;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;

namespace DataGateway.Data.Queries;

/// <summary>Compiles with its engine and runs on the source database, on a context created per call.</summary>
public sealed class QueryRunner
{
    // CancellationTokenSource.CancelAfter accepts at most uint.MaxValue - 1 milliseconds (about 49.7 days); a longer
    // timeout gets that longest deadline rather than none.
    internal static readonly TimeSpan LongestDeadline = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private readonly IDatabaseEngine engine;

    public QueryRunner(IDatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        this.engine = engine;
    }

    public CompiledQuery Compile(ValidatedQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return engine is ICappedQueryEngine capped ? capped.Compile(query) : engine.Compile(query);
    }

    /// <summary>Runs the query within one deadline of the source's QueryTimeoutSeconds, from the open connection to the last row
    /// kept; the driver's per-command timeout alone would restart for every row read. Connecting is governed by the
    /// source's connection string.</summary>
    public async Task<CatalogResult<QueryRows>> RunAsync(
        SourceContract source, CompiledQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        if (cancellationToken.IsCancellationRequested) return Reject(source, Cancelled());

        try { engine.ValidateSourceConnection(source.ConnectionString); }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return Reject(source, InvalidCatalog() with { DriverException = e, ExceptionType = e.GetType().Name, Step = "connect" });
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = deadline.Token;
        var connected = false;
        void Opened()
        {
            connected = true;
            deadline.CancelAfter(Deadline(source.QueryTimeoutSeconds));
        }
        bool Expired() => TimedOut(deadline, cancellationToken);

        QueryRows rows;
        ProviderFailure? provider = null;
        try
        {
            if (engine is ICappedQueryEngine capped)
                rows = await capped.ReadQueryAsync(source.ConnectionString, query, source.QueryTimeoutSeconds,
                    Opened, Expired, token).ConfigureAwait(false);
            else
                rows = await engine.ReadSourceAsync(source.ConnectionString, query, source.QueryTimeoutSeconds,
                    Opened,
                    sourceRows => KeptRows.KeepAsync(sourceRows, query, token, Expired), token)
                    .ConfigureAwait(false);
        }
        catch (Exception e) when (Classify(e, connected, Snapshot(deadline, cancellationToken, out var timedOut), timedOut,
            source.QueryTimeoutSeconds, provider = engine.RecognizeFailure(e)) is { } f)
        {
            return Reject(source, f with
            {
                DriverException = e, ExceptionType = e.GetType().Name, SqlNumber = provider?.Number,
                Step = connected ? "command" : "connect"
            });
        }

        if (cancellationToken.IsCancellationRequested) return Reject(source, Cancelled() with { Step = "command" });
        return CatalogResult<QueryRows>.Success(rows);
    }

    internal static TimeSpan Deadline(int commandTimeoutSeconds)
    {
        var seconds = TimeSpan.FromSeconds(commandTimeoutSeconds);
        return seconds < LongestDeadline ? seconds : LongestDeadline;
    }

    private static bool TimedOut(CancellationTokenSource deadline, CancellationToken caller) =>
        deadline.IsCancellationRequested && !caller.IsCancellationRequested;

    // One snapshot for both answers. The deadline is read first: a caller's cancel also cancels the deadline, so a deadline
    // seen cancelled while the caller is still not cancelled afterwards can only have timed out.
    private static bool Snapshot(CancellationTokenSource deadline, CancellationToken caller, out bool timedOut)
    {
        var deadlineHit = deadline.IsCancellationRequested;
        var callerGone = caller.IsCancellationRequested;
        timedOut = deadlineHit && !callerGone;
        return callerGone;
    }

    internal static CatalogFailure? Classify(Exception error, bool connected, bool cancelled, bool timedOut,
        int commandTimeoutSeconds, ProviderFailure? provider)
    {
        // An exception DataGateway throws for its own broken assumption is never a source failure.
        if (error is GatewayInvariantException) return null;
        var expected = provider is not null || error is InvalidOperationException or IOException or TimeoutException
            or OperationCanceledException || (connected ? error is OverflowException : error is ArgumentException or FormatException);
        if (!expected) return null;
        if (cancelled) return Cancelled();
        // Once the deadline has fired, the failure is the deadline's: drivers and engines report a cancelled command in
        // many shapes (cancellation, a provider error, an I/O error). A failure that came first but whose cleanup outlasted
        // the deadline is also reported as QueryTimeout; that rarer case is accepted.
        if (timedOut) return QueryTimeout(commandTimeoutSeconds);
        if (error is OperationCanceledException) return null;
        if (!connected) return error is ArgumentException or FormatException ? InvalidCatalog() : Unavailable();
        return provider?.IsTimeout == true || error is TimeoutException ? QueryTimeout(commandTimeoutSeconds) : Unavailable();
    }

    private static CatalogResult<QueryRows> Reject(SourceContract source, CatalogFailure failure) =>
        CatalogResult<QueryRows>.Reject(failure with
        { SourceId = source.Id, SourceName = source.Source.Name });

    private static CatalogFailure QueryTimeout(int commandTimeoutSeconds) => new(DataGatewayError.QueryTimeout.Code, "$.query",
        $"The query ran longer than the {commandTimeoutSeconds}-second command timeout and was stopped. Narrow the filters, aggregate, or request fewer rows.");

    private static CatalogFailure Cancelled() => new(DataGatewayError.QueryCancelled.Code, "$.query",
        "The query was cancelled because the caller ended the request. Submit a new request if still needed.");

    private static CatalogFailure InvalidCatalog() => new("InvalidCatalog", "$.source",
        "The source's catalog connection string cannot be used. Ask the data steward to correct the source's catalog row.");

    private static CatalogFailure Unavailable() => new(DataGatewayError.SourceUnavailable.Code, "$.source",
        "The source's database could not run the query. If it keeps failing, the DataGateway administrator should check the source. Tell the user. Do not retry.");
}
