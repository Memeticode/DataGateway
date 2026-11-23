using DataGateway.Core.Errors;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;

namespace DataGateway.Data.Queries;

/// <summary>Compiles with its engine and runs on the source database, on a context created per call.</summary>
/// <remarks>
/// <para>The runner owns the meaning of failures. Engines report exceptions and provider facts; the runner maps them
/// onto the gateway's codes (<see cref="Classify"/>): a bad connection string is a catalog fault, a failure before
/// the connection opens is an unavailable source, any failure once the deadline has fired is a timeout, and a
/// cancelled caller is <c>QueryCancelled</c>.</para>
/// <para>It prefers the engine's internal bounded path when the engine derives from
/// <see cref="DatabaseEngine{TConnection}"/>; any other engine gets the same row bound through
/// <see cref="KeptRows"/>.</para>
/// </remarks>
public sealed class QueryRunner
{
    // CancellationTokenSource.CancelAfter accepts at most uint.MaxValue - 1 milliseconds (about 49.7 days); a longer
    // timeout gets that longest deadline rather than none.
    /// <summary>The longest deadline the runner can set.</summary>
    internal static readonly TimeSpan LongestDeadline = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    /// <summary>The engine that compiles and runs every query this runner handles.</summary>
    private readonly IDatabaseEngine engine;

    /// <summary>Creates a runner for one engine.</summary>
    /// <param name="engine">The engine for the sources this runner serves.</param>
    public QueryRunner(IDatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        this.engine = engine;
    }

    /// <summary>Compiles a validated query with the runner's engine.</summary>
    /// <param name="query">The validated query.</param>
    /// <returns>The compiled query.</returns>
    public CompiledQuery Compile(ValidatedQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return engine is ICappedQueryEngine capped ? capped.Compile(query) : engine.Compile(query);
    }

    /// <summary>Runs the query within one deadline of the source's QueryTimeoutSeconds, from the open connection to the last row
    /// kept; the driver's per-command timeout alone would restart for every row read. Connecting is governed by the
    /// source's connection string.</summary>
    /// <param name="source">The source contract: connection string, timeout and name.</param>
    /// <param name="query">The compiled query.</param>
    /// <param name="cancellationToken">The caller's token; cancelling it ends the query as
    /// <c>QueryCancelled</c>.</param>
    /// <returns>The kept rows, or a failure carrying the source and log diagnostics.</returns>
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

    /// <summary>The deadline for a timeout, no longer than <see cref="LongestDeadline"/>.</summary>
    /// <param name="commandTimeoutSeconds">The source's query timeout, in seconds.</param>
    /// <returns>The deadline.</returns>
    internal static TimeSpan Deadline(int commandTimeoutSeconds)
    {
        var seconds = TimeSpan.FromSeconds(commandTimeoutSeconds);
        return seconds < LongestDeadline ? seconds : LongestDeadline;
    }

    /// <summary>Whether the deadline fired while the caller was still there.</summary>
    /// <param name="deadline">The query's deadline source, linked to the caller's token.</param>
    /// <param name="caller">The caller's token.</param>
    /// <returns><see langword="true"/> when the query timed out.</returns>
    private static bool TimedOut(CancellationTokenSource deadline, CancellationToken caller) =>
        deadline.IsCancellationRequested && !caller.IsCancellationRequested;

    // One snapshot for both answers. The deadline is read first: a caller's cancel also cancels the deadline, so a deadline
    // seen cancelled while the caller is still not cancelled afterwards can only have timed out.
    /// <summary>Reads, once, whether the caller cancelled and whether the deadline timed out.</summary>
    /// <param name="deadline">The query's deadline source.</param>
    /// <param name="caller">The caller's token.</param>
    /// <param name="timedOut">Whether the deadline fired while the caller was still there.</param>
    /// <returns>Whether the caller cancelled.</returns>
    private static bool Snapshot(CancellationTokenSource deadline, CancellationToken caller, out bool timedOut)
    {
        var deadlineHit = deadline.IsCancellationRequested;
        var callerGone = caller.IsCancellationRequested;
        timedOut = deadlineHit && !callerGone;
        return callerGone;
    }

    /// <summary>Maps a failed run onto the gateway's codes, or null for an exception the gateway did not
    /// expect.</summary>
    /// <remarks>An unexpected exception is left to propagate, so the edge answers it as an unexpected error with an
    /// incident id instead of disguising a bug as an outage. An <see cref="OverflowException"/> counts as expected
    /// only after the connection opened, and an argument or format error only before.</remarks>
    /// <param name="error">The exception.</param>
    /// <param name="connected">Whether the connection had opened.</param>
    /// <param name="cancelled">Whether the caller cancelled.</param>
    /// <param name="timedOut">Whether the deadline fired while the caller was still there.</param>
    /// <param name="commandTimeoutSeconds">The source's timeout, for the message.</param>
    /// <param name="provider">The engine's facts about the exception; null when not the driver's.</param>
    /// <returns>The failure, or null.</returns>
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

    /// <summary>A failed result, tagged with the source's id and name for the log.</summary>
    /// <param name="source">The source contract.</param>
    /// <param name="failure">The failure.</param>
    /// <returns>The failed result.</returns>
    private static CatalogResult<QueryRows> Reject(SourceContract source, CatalogFailure failure) =>
        CatalogResult<QueryRows>.Reject(failure with
        { SourceId = source.Id, SourceName = source.Source.Name });

    /// <summary>The <c>QueryTimeout</c> failure, naming the source's timeout.</summary>
    /// <param name="commandTimeoutSeconds">The source's timeout, in seconds.</param>
    /// <returns>The failure at <c>$.query</c>.</returns>
    private static CatalogFailure QueryTimeout(int commandTimeoutSeconds) => new(DataGatewayError.QueryTimeout.Code, "$.query",
        $"The query ran longer than the {commandTimeoutSeconds}-second command timeout and was stopped. Narrow the query once (filters, an aggregate or fewer rows) and say what you narrowed; if it times out again, tell the user, give the incidentId and stop.");

    /// <summary>The <c>QueryCancelled</c> failure; log only.</summary>
    /// <returns>The failure.</returns>
    private static CatalogFailure Cancelled() => new(DataGatewayError.QueryCancelled.Code, "$.query",
        "The query was cancelled because the caller ended the request. Submit a new request if still needed.");

    /// <summary>The <c>InvalidCatalog</c> failure for a source connection string the engine cannot use.</summary>
    /// <returns>The failure at <c>$.source</c>.</returns>
    private static CatalogFailure InvalidCatalog() => new("InvalidCatalog", "$.source",
        "The source's catalog connection string cannot be used. Ask the data steward to correct the source's catalog row.");

    /// <summary>The <c>SourceUnavailable</c> failure.</summary>
    /// <returns>The failure at <c>$.source</c>.</returns>
    private static CatalogFailure Unavailable() => new(DataGatewayError.SourceUnavailable.Code, "$.source",
        "The source's database could not run the query. The DataGateway administrator should check the source. Tell the user, give the incidentId and stop; do not retry.");
}
