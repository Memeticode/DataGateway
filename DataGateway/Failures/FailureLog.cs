using System.Collections;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using DataGateway.Core.Catalog;
using DataGateway.Routes;
using DataGateway.Core.Errors;
using DataGateway.Core.Queries;
using SqlKata.JsonQuery;

namespace DataGateway.Failures;

/// <summary>Writes each failure as one structured log line and returns its incident ID, the value the error body
/// carries so a report leads straight to the line.</summary>
/// <remarks>
/// <para>TERM: incident ID, a new 32-character lowercase hex value per failure, written in both the reply and the
/// line. It reveals nothing about the backend.</para>
/// <para>Logging never changes the answer. A provider that throws is reported on standard error and the reply still
/// goes out with its incident ID. No exception object is passed to <see cref="ILogger"/>: the line holds the
/// exception's type name, and its full text only for unexpected failures.</para>
/// </remarks>
/// <param name="loggers">The host's logger factory.</param>
/// <param name="limits">The gateway's limits, which bound how much of a request body is logged.</param>
internal sealed class FailureLog(ILoggerFactory loggers, JsonQueryLimits limits)
{
    /// <summary>The logger category; public as <see cref="DataGatewayExtensions.FailureLogCategory"/>.</summary>
    internal const string Category = DataGatewayExtensions.FailureLogCategory;
    /// <summary>The message template providers receive as <c>{OriginalFormat}</c>; <see cref="FailureLine.Summary"/>
    /// renders the same text.</summary>
    internal const string Template = "Failure {IncidentId}: {Operation} for {Caller}: {Code}: {Message} ({Step}, {ExceptionType}, {SqlNumber})";
    /// <summary>The logger for <see cref="Category"/>.</summary>
    private readonly ILogger logger = loggers.CreateLogger(Category);
    /// <summary>How many characters of a request body a line keeps: the query length limit plus 1024, capped at
    /// <see cref="int.MaxValue"/>.</summary>
    /// <remarks>The bound is the longest query the gateway accepts, plus room for the source name, the JSON envelope
    /// and whitespace. Any request that could have been accepted is therefore logged whole and can be reproduced;
    /// only a body that is refused anyway gets cut.</remarks>
    internal int RequestCharacters { get; } = (int)Math.Min(limits.MaxJsonLength + 1024L, int.MaxValue);

    /// <summary>Writes the failure's one line and returns its incident ID for the error body.</summary>
    /// <param name="context">The request; supplies the caller, whether the caller has gone, and the trail.</param>
    /// <param name="operation">The operation name, or null when none is known.</param>
    /// <param name="body">The request body as received, or null when none was read in full.</param>
    /// <param name="failure">The expected failure.</param>
    /// <returns>The new incident ID.</returns>
    internal string Write(HttpContext context, string? operation, byte[]? body, CatalogFailure failure)
        => Write(context.User, context.RequestAborted.IsCancellationRequested, operation, body, failure, RequestTrail.Of(context));

    /// <summary>Writes an expected failure's one line, for callers without an <see cref="HttpContext"/> such as an MCP
    /// tool call.</summary>
    /// <remarks>A logging provider that throws never changes the answer: the refusal is still sent with its incident ID.</remarks>
    /// <param name="user">The caller.</param>
    /// <param name="callerGone">Whether the caller has left; the line is then a warning, not an error.</param>
    /// <param name="operation">The operation or tool name, or null.</param>
    /// <param name="body">The request body, or null.</param>
    /// <param name="failure">The expected failure; its driver message is logged when present.</param>
    /// <param name="trail">The source and compiled query reached so far.</param>
    /// <returns>The new incident ID.</returns>
    internal string Write(ClaimsPrincipal user, bool callerGone, string? operation, byte[]? body, CatalogFailure failure, RequestTrail trail)
    {
        var incident = NewIncident();
        try
        {
            var line = new FailureLine(incident, CallerOf(user), operation,
                body is null ? null : RequestText.From(body, RequestCharacters), failure, trail.Query, trail.Source, driverMessage: failure.DriverException?.Message);
            logger.Log(LevelOf(callerGone), default, line, null, static (l, _) => l.Summary());
        }
        catch (Exception unwritten) { Report(incident, "the failure line could not be written", unwritten, failure.Code); }
        return incident;
    }

    /// <summary>A new incident ID: a GUID as 32 lowercase hex digits.</summary>
    /// <returns>The incident ID.</returns>
    internal static string NewIncident() => Guid.NewGuid().ToString("N");

    /// <summary>Writes checked exception text only; a failed log write still returns the incident ID.</summary>
    /// <remarks>For an exception no known failure covers. The line carries the full exception text, which never
    /// reaches the caller; step <c>response</c> marks a failure after the reply had started.</remarks>
    /// <param name="context">The request.</param>
    /// <param name="exception">The unexpected exception.</param>
    /// <returns>The new incident ID.</returns>
    internal string WriteUnexpected(HttpContext context, Exception exception)
    {
        var incident = NewIncident();
        try
        {
            var trail = RequestTrail.Of(context);
            WriteUnexpected(incident, context.User, context.RequestAborted.IsCancellationRequested,
                OperationOf(context), trail, exception, context.Response.HasStarted ? "response" : null);
        }
        catch (Exception unwritten) { Report(incident, "the failure line could not be written", unwritten, exception); }
        return incident;
    }

    /// <summary>Writes an unexpected failure's one line for an MCP tool call.</summary>
    /// <param name="user">The caller.</param>
    /// <param name="callerGone">Whether the caller has left.</param>
    /// <param name="operation">The tool name.</param>
    /// <param name="trail">The call's body, source and compiled query reached so far.</param>
    /// <param name="exception">The unexpected exception.</param>
    /// <returns>The new incident ID.</returns>
    internal string WriteUnexpected(ClaimsPrincipal user, bool callerGone, string? operation, RequestTrail trail, Exception exception)
    {
        var incident = NewIncident();
        try { WriteUnexpected(incident, user, callerGone, operation, trail, exception, step: null); }
        catch (Exception unwritten) { Report(incident, "the failure line could not be written", unwritten, exception); }
        return incident;
    }

    /// <summary>Builds and writes the unexpected failure's line; the callers guard it.</summary>
    /// <param name="incident">The incident ID already chosen.</param>
    /// <param name="user">The caller.</param>
    /// <param name="callerGone">Whether the caller has left.</param>
    /// <param name="operation">The operation, or null.</param>
    /// <param name="trail">What the request reached.</param>
    /// <param name="exception">The unexpected exception.</param>
    /// <param name="step">The phase, or null when unknown.</param>
    private void WriteUnexpected(string incident, ClaimsPrincipal user, bool callerGone, string? operation,
        RequestTrail trail, Exception exception, string? step)
    {
        var failure = ErrorEnvelope.Unexpected with
        {
            ExceptionType = exception.GetType().Name,
            Step = step
        };
        var line = new FailureLine(incident, CallerOf(user), operation,
            trail.Body is null ? null : RequestText.From(trail.Body, RequestCharacters),
            failure, trail.Query, trail.Source, exceptionText: exception.ToString());
        logger.Log(LevelOf(callerGone), default, line, null, static (l, _) => l.Summary());
    }

    /// <summary>Begins the gateway's logging scope; a provider that throws on begin or end never changes the answer.</summary>
    /// <remarks>When a later provider throws, Microsoft.Extensions.Logging does not dispose a scope an earlier provider
    /// already began; that scope is lost with the request.</remarks>
    /// <param name="name">The gateway name, logged as the scope's <c>Gateway</c> value.</param>
    /// <returns>The scope to dispose at the end of the request, or null when none began.</returns>
    internal IDisposable? BeginGatewayScope(string name)
    {
        try
        {
            return logger.BeginScope(new Dictionary<string, object?> { ["Gateway"] = name }) is { } scope
                ? new GuardedScope(scope, name)
                : null;
        }
        catch (Exception unscoped)
        {
            ReportScope(name, "could not begin", unscoped);
            return null;
        }
    }

    /// <summary>A logging scope whose end never throws into the request.</summary>
    /// <param name="scope">The provider's scope.</param>
    /// <param name="name">The gateway name, for the report.</param>
    private sealed class GuardedScope(IDisposable scope, string name) : IDisposable
    {
        /// <summary>Ends the scope; a provider fault is reported on standard error.</summary>
        public void Dispose()
        {
            try { scope.Dispose(); }
            catch (Exception unended) { ReportScope(name, "could not end", unended); }
        }
    }

    /// <summary>Reports a logging scope fault on standard error.</summary>
    /// <param name="name">The gateway name.</param>
    /// <param name="problem">What went wrong.</param>
    /// <param name="cause">The provider's exception; only its type name is written.</param>
    private static void ReportScope(string name, string problem, Exception cause) =>
        LastResort($"DataGateway.Failure: the logging scope for gateway '{name}' {problem} ({cause.GetType().FullName}); the request continues without it.");

    /// <summary>The operation of the endpoint a request reached: the gateway's own name for it, else the endpoint
    /// name.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The operation, or null before routing or for an endpoint without a name.</returns>
    internal static string? OperationOf(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<GatewayOperation>()?.Name ??
        context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;

    /// <summary>Reports, on standard error, that an unexpected failure's line or reply could not be written.</summary>
    /// <param name="incidentId">The incident the reply carries.</param>
    /// <param name="problem">What could not be done.</param>
    /// <param name="cause">The exception that stopped it; only its type name is written.</param>
    /// <param name="failure">The original failure; only its type name is written.</param>
    internal static void Report(string incidentId, string problem, Exception cause, Exception failure) =>
        Report(incidentId, problem, cause, failure.GetType().FullName);

    /// <summary>Reports, on standard error, that a failure's line or reply could not be written.</summary>
    /// <param name="incidentId">The incident the reply carries.</param>
    /// <param name="problem">What could not be done.</param>
    /// <param name="cause">The exception that stopped it; only its type name is written.</param>
    /// <param name="failure">The original failure's code or type name.</param>
    internal static void Report(string incidentId, string problem, Exception cause, string? failure) =>
        LastResort($"DataGateway.Failure: incident {incidentId}: {problem} ({cause.GetType().FullName}); the failure was {failure}.");

    /// <summary>Writes one line to standard error.</summary>
    /// <remarks>Standard error is the last place a failure can be told; if it throws too, the answer still goes out.</remarks>
    /// <param name="line">The text, which holds type names and IDs only.</param>
    private static void LastResort(string line)
    {
        try { Console.Error.WriteLine(line); }
        catch (Exception) { }
    }

    /// <summary>The caller as logged: the signed-in name, or <c>anonymous</c>.</summary>
    /// <param name="user">The caller.</param>
    /// <returns>The name; null for a signed-in identity without one.</returns>
    internal static string? CallerOf(ClaimsPrincipal user) =>
        user.Identity is { IsAuthenticated: true } identity ? identity.Name : "anonymous";
    /// <summary>The line's level: Error while the caller is connected, Warning once it has gone.</summary>
    /// <remarks>A caller who leaves has withdrawn the request, so it is not a failure; it still gets its one line, so
    /// aborts such as slow-query cancellations stay visible. That line is Warning, not Information, so a log filtered
    /// at Warning still keeps it.</remarks>
    /// <param name="callerGone">Whether the caller has left.</param>
    /// <returns>The log level.</returns>
    internal static LogLevel LevelOf(bool callerGone) => callerGone ? LogLevel.Warning : LogLevel.Error;
}

/// <summary>The part of a request body a log line keeps, with its full size and whether it was cut.</summary>
/// <param name="Text">The body as UTF-8 text, at most the bound in characters; invalid bytes become replacement
/// characters.</param>
/// <param name="Bytes">The full body's byte count, even when the text was cut.</param>
/// <param name="Cut">Whether the text stops short of the body.</param>
internal sealed record RequestText(string Text, int Bytes, bool Cut)
{
    /// <summary>Decodes at most <paramref name="maxCharacters"/> characters of a body, never splitting a surrogate
    /// pair.</summary>
    /// <remarks>Only the first <c>3 * maxCharacters + 3</c> bytes are decoded: enough for the bound in any UTF-8 text,
    /// so a large body is never decoded in full.</remarks>
    /// <param name="body">The request body.</param>
    /// <param name="maxCharacters">The bound in UTF-16 characters; at least 1.</param>
    /// <returns>The kept text.</returns>
    internal static RequestText From(ReadOnlySpan<byte> body, int maxCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCharacters, 1);
        var count = (int)Math.Min(body.Length, 3L * maxCharacters + 3);
        var text = Encoding.UTF8.GetString(body[..count]);
        if (text.Length <= maxCharacters) return new(text, body.Length, false);
        var keep = char.IsHighSurrogate(text[maxCharacters - 1]) ? maxCharacters - 1 : maxCharacters;
        return new(text[..keep], body.Length, true);
    }
}

/// <summary>Explicit failure projection, shared by Serilog and in-memory providers. Never holds an exception object or a connection.</summary>
/// <remarks>The line is its own logger state: a list of named fields that structured providers store field by field,
/// ending with <c>{OriginalFormat}</c>, and a formatter (<see cref="Summary"/>) for text providers. The field set
/// and order are listed in the library's configuration guide.</remarks>
internal sealed class FailureLine : IReadOnlyList<KeyValuePair<string, object?>>
{
    /// <summary>The fields in their fixed order; <see cref="Summary"/> reads them by position.</summary>
    private readonly KeyValuePair<string, object?>[] fields;
    /// <summary>The incident ID this line records.</summary>
    internal string IncidentId { get; }

    /// <summary>Projects one failure and what the request reached into the line's fields.</summary>
    /// <remarks>The query parameters are copied, so the line holds values, not the compiled query.</remarks>
    /// <param name="incidentId">The incident ID.</param>
    /// <param name="caller">The caller's name, <c>anonymous</c>, or null.</param>
    /// <param name="operation">The operation, or null.</param>
    /// <param name="request">The kept body, or null.</param>
    /// <param name="failure">The failure, in its internal form; the code the caller saw is added as
    /// <c>ReturnedCode</c>.</param>
    /// <param name="query">The compiled query, when reached.</param>
    /// <param name="source">The source, when reached; fills the source fields the failure leaves empty.</param>
    /// <param name="driverMessage">The driver's message, on expected failures.</param>
    /// <param name="exceptionText">The full exception text, on unexpected failures only.</param>
    internal FailureLine(string incidentId, string? caller, string? operation, RequestText? request,
        CatalogFailure failure, CompiledQuery? query, SourceContract? source = null, string? driverMessage = null, string? exceptionText = null)
    {
        IncidentId = incidentId;
        Dictionary<string, object?>? parameters = null;
        if (query is not null)
        {
            parameters = new(StringComparer.Ordinal);
            foreach (var pair in query.Parameters) parameters.Add(pair.Key, pair.Value);
        }
        fields = [new("IncidentId", incidentId), new("Caller", caller), new("Operation", operation),
            new("Request", request?.Text), new("RequestBytes", request?.Bytes), new("RequestCut", request?.Cut),
            new("Sql", query?.Sql), new("SqlParameters", parameters), new("Code", failure.Code),
            new("Path", failure.Path), new("Message", failure.Message), new("ExceptionType", failure.ExceptionType),
            new("SqlNumber", failure.SqlNumber), new("Step", failure.Step),
            new("ReturnedCode", ErrorEnvelope.From(failure, incidentId).Body.Code),
            new("SourceId", failure.SourceId ?? source?.Id), new("SourceName", failure.SourceName ?? source?.Source.Name),
            new("DriverMessage", driverMessage), new("ExceptionText", exceptionText), new("{OriginalFormat}", FailureLog.Template)];
    }

    /// <summary>The line as text, in <see cref="FailureLog.Template"/>'s shape.</summary>
    /// <returns>The rendered message.</returns>
    internal string Summary() => $"Failure {Value(0)}: {Value(2)} for {Value(1)}: {Value(8)}: {Value(10)} ({Value(13)}, {Value(11)}, {Value(12)})";
    /// <summary>One field as invariant text, or <c>null</c>.</summary>
    /// <param name="index">The field's position.</param>
    /// <returns>The text.</returns>
    private string Value(int index) => fields[index].Value is { } value ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null" : "null";
    /// <summary>The number of fields.</summary>
    public int Count => fields.Length;
    /// <summary>One field by position.</summary>
    /// <param name="index">The field's position.</param>
    public KeyValuePair<string, object?> this[int index] => fields[index];
    /// <summary>The fields in order.</summary>
    /// <returns>An enumerator over the fields.</returns>
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)fields).GetEnumerator();
    /// <summary>The fields in order.</summary>
    /// <returns>An enumerator over the fields.</returns>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
