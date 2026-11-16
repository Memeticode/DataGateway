using DataGateway.Failures;
using DataGateway.Mcp;
using DataGateway.Core.Errors;

namespace DataGateway.Routes;

/// <summary>DataGateway's outermost layer: an exception that escapes routing, sign-in or a route is logged once
/// and answered once. Nothing escapes it, so the framework never logs or shows a raw exception.
/// The one exception it drops is the MCP SDK ending a stateless call whose caller has gone (McpEndpoint.EndedByCaller):
/// that call has already written its own line, or none if nothing failed.</summary>
internal static class CatchAll
{
    internal static async Task Answer(HttpContext context, Exception error, FailureLog failures)
    {
        if (McpEndpoint.EndedByCaller(context, error)) return;
        if (error is BadHttpRequestException refused && !context.Response.HasStarted)
        {
            try
            {
                var failure = RouteRequest.Refusal(refused);
                var bodyIncident = failures.Write(context, FailureLog.OperationOf(context), body: null, failure);
                await RouteReply.Fail(context, ErrorEnvelope.From(failure, bodyIncident));
                return;
            }
            catch (Exception unsent) { error = unsent; }
        }
        var incident = failures.WriteUnexpected(context, error);
        if (context.RequestAborted.IsCancellationRequested) return;
        if (context.Response.HasStarted) { context.Abort(); return; }
        try
        {
            context.Response.Clear();
            await RouteReply.Fail(context, ErrorEnvelope.From(ErrorEnvelope.Unexpected, incident));
        }
        catch (Exception unsent)
        {
            FailureLog.Report(incident, "the error reply could not be sent", unsent, error);
            context.Abort();
        }
    }
}
