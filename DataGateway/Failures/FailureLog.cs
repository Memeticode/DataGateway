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

internal sealed class FailureLog(ILoggerFactory loggers, JsonQueryLimits limits)
{
    internal const string Category = DataGatewayExtensions.FailureLogCategory;
    internal const string Template = "Failure {IncidentId}: {Operation} for {Caller}: {Code}: {Message} ({Step}, {ExceptionType}, {SqlNumber})";
    private readonly ILogger logger = loggers.CreateLogger(Category);
    internal int RequestCharacters { get; } = (int)Math.Min(limits.MaxJsonLength + 1024L, int.MaxValue);

    /// <summary>Writes the failure's one line and returns its incident ID for the error body.</summary>
    internal string Write(HttpContext context, string? operation, byte[]? body, CatalogFailure failure)
        => Write(context.User, context.RequestAborted.IsCancellationRequested, operation, body, failure, RequestTrail.Of(context));

    /// <remarks>A logging provider that throws never changes the answer: the refusal is still sent with its incident ID.</remarks>
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

    internal static string NewIncident() => Guid.NewGuid().ToString("N");

    /// <summary>Writes checked exception text only; a failed log write still returns the incident ID.</summary>
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

    internal string WriteUnexpected(ClaimsPrincipal user, bool callerGone, string? operation, RequestTrail trail, Exception exception)
    {
        var incident = NewIncident();
        try { WriteUnexpected(incident, user, callerGone, operation, trail, exception, step: null); }
        catch (Exception unwritten) { Report(incident, "the failure line could not be written", unwritten, exception); }
        return incident;
    }

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

    private sealed class GuardedScope(IDisposable scope, string name) : IDisposable
    {
        public void Dispose()
        {
            try { scope.Dispose(); }
            catch (Exception unended) { ReportScope(name, "could not end", unended); }
        }
    }

    private static void ReportScope(string name, string problem, Exception cause) =>
        LastResort($"DataGateway.Failure: the logging scope for gateway '{name}' {problem} ({cause.GetType().FullName}); the request continues without it.");

    internal static string? OperationOf(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<GatewayOperation>()?.Name ??
        context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;

    internal static void Report(string incidentId, string problem, Exception cause, Exception failure) =>
        Report(incidentId, problem, cause, failure.GetType().FullName);

    internal static void Report(string incidentId, string problem, Exception cause, string? failure) =>
        LastResort($"DataGateway.Failure: incident {incidentId}: {problem} ({cause.GetType().FullName}); the failure was {failure}.");

    // Standard error is the last place a failure can be told; if it throws too, the answer still goes out.
    private static void LastResort(string line)
    {
        try { Console.Error.WriteLine(line); }
        catch (Exception) { }
    }

    internal static string? CallerOf(ClaimsPrincipal user) =>
        user.Identity is { IsAuthenticated: true } identity ? identity.Name : "anonymous";
    internal static LogLevel LevelOf(bool callerGone) => callerGone ? LogLevel.Warning : LogLevel.Error;
}

internal sealed record RequestText(string Text, int Bytes, bool Cut)
{
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
internal sealed class FailureLine : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly KeyValuePair<string, object?>[] fields;
    internal string IncidentId { get; }

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

    internal string Summary() => $"Failure {Value(0)}: {Value(2)} for {Value(1)}: {Value(8)}: {Value(10)} ({Value(13)}, {Value(11)}, {Value(12)})";
    private string Value(int index) => fields[index].Value is { } value ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null" : "null";
    public int Count => fields.Length;
    public KeyValuePair<string, object?> this[int index] => fields[index];
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)fields).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
