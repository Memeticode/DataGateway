using DataGateway.Failures;
using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;

namespace DataGateway.Routes;

/// <summary>The one way a route answers: once the caller has gone, nothing is written.</summary>
internal static class RouteReply
{
    /// <summary>Answers an operation's result: the answer with 200, or the failure logged and answered.</summary>
    /// <typeparam name="T">The answer type.</typeparam>
    /// <param name="context">The request.</param>
    /// <param name="log">The gateway's failure log.</param>
    /// <param name="request">The request as read, for the log line.</param>
    /// <param name="result">The operation's result.</param>
    /// <returns>A task that completes once the reply is written.</returns>
    internal static Task Send<T>(HttpContext context, FailureLog log, RouteRequest request, CatalogResult<T> result) where T : class =>
        result.Failure is { } failure
            ? Fail(context, log, request, failure)
            : Answer(context, result.Value);

    /// <summary>Writes an answer with status 200.</summary>
    /// <typeparam name="T">The answer type.</typeparam>
    /// <param name="context">The request.</param>
    /// <param name="body">The answer.</param>
    /// <returns>A task that completes once the reply is written.</returns>
    internal static Task Answer<T>(HttpContext context, T body) => Write(context, StatusCodes.Status200OK, body);

    /// <summary>Logs an expected failure and writes its error reply with the incident ID.</summary>
    /// <param name="context">The request.</param>
    /// <param name="log">The gateway's failure log.</param>
    /// <param name="request">The request as read.</param>
    /// <param name="failure">The failure.</param>
    /// <returns>A task that completes once the reply is written.</returns>
    internal static Task Fail(HttpContext context, FailureLog log, RouteRequest request, CatalogFailure failure)
    {
        var incident = log.Write(context, request.Operation, request.Body, failure);
        return Fail(context, ErrorEnvelope.From(failure, incident));
    }

    /// <summary>Writes an error reply already built.</summary>
    /// <param name="context">The request.</param>
    /// <param name="reply">The status and error body.</param>
    /// <returns>A task that completes once the reply is written.</returns>
    internal static Task Fail(HttpContext context, ErrorReply reply) => Write(context, reply.Status, reply.Body);

    /// <summary>Writes a status and JSON body with the gateway's own serializer options.</summary>
    /// <remarks>A caller that leaves mid-write is not a failure: the cancellation and broken-pipe exceptions it causes
    /// are swallowed.</remarks>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="context">The request.</param>
    /// <param name="status">The HTTP status.</param>
    /// <param name="body">The body.</param>
    /// <returns>A task that completes once the body is written or the caller is gone.</returns>
    private static async Task Write<T>(HttpContext context, int status, T body)
    {
        var token = context.RequestAborted;
        if (token.IsCancellationRequested) return;
        context.Response.StatusCode = status;
        try { await context.Response.WriteAsJsonAsync(body, GatewayJson.Of(context.RequestServices), token); }
        catch (Exception error) when (token.IsCancellationRequested &&
            error is OperationCanceledException or IOException) { }
    }
}
