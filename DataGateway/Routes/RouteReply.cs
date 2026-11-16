using DataGateway.Failures;
using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;

namespace DataGateway.Routes;

/// <summary>The one way a route answers: once the caller has gone, nothing is written.</summary>
internal static class RouteReply
{
    internal static Task Send<T>(HttpContext context, FailureLog log, RouteRequest request, CatalogResult<T> result) where T : class =>
        result.Failure is { } failure
            ? Fail(context, log, request, failure)
            : Answer(context, result.Value);

    internal static Task Answer<T>(HttpContext context, T body) => Write(context, StatusCodes.Status200OK, body);

    internal static Task Fail(HttpContext context, FailureLog log, RouteRequest request, CatalogFailure failure)
    {
        var incident = log.Write(context, request.Operation, request.Body, failure);
        return Fail(context, ErrorEnvelope.From(failure, incident));
    }

    internal static Task Fail(HttpContext context, ErrorReply reply) => Write(context, reply.Status, reply.Body);

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
