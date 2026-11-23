using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using DataGateway.Failures;

namespace DataGateway.Routes;

/// <summary>The gateway's error boundary over the host's own middleware: turns an exception into the gateway's
/// error reply, and an empty 401 the host leaves into the gateway's JSON refusal.</summary>
/// <remarks>
/// <para>TERM: boundary, a span of the request pipeline whose failures the gateway answers. One request has at most
/// one active boundary; an inner one passes through, so a failure is logged and answered once.</para>
/// <para>The host keeps access control: it refuses a caller with an empty 401, and may first record what the
/// refusal was. The boundary writes the body afterwards, so the host never has to build the gateway's error
/// format.</para>
/// </remarks>
internal static class HostErrors
{
    /// <summary>The request's boundary, kept as a request feature.</summary>
    private sealed class State
    {
        /// <summary>Whether refusals can still be recorded; false once the downstream pipeline has returned.</summary>
        internal bool Active { get; set; } = true;
        /// <summary>The host's recorded refusal, or null.</summary>
        internal CatalogFailure? Refusal { get; set; }
    }

    /// <summary>Records the host's refusal for the boundary to answer; see
    /// <see cref="DataGatewayExtensions.TryRecordDataGatewayRefusal"/>.</summary>
    /// <param name="context">The request.</param>
    /// <param name="failure">The refusal; its driver exception is dropped.</param>
    /// <returns>True when accepted.</returns>
    internal static bool TryRecord(HttpContext context, CatalogFailure failure)
    {
        var state = context.Features.Get<State>();
        if (state is null || !state.Active || state.Refusal is not null ||
            context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
            return false;
        state.Refusal = failure with { DriverException = null };
        return true;
    }

    /// <summary>The unnamed boundary: covers the rest of the pipeline only for a request routed to a gateway
    /// endpoint.</summary>
    /// <param name="context">The request.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <returns>A task that completes with the request.</returns>
    internal static async Task Invoke(HttpContext context, RequestDelegate next)
    {
        var endpoint = context.GetEndpoint();
        var registration = endpoint?.Metadata.GetMetadata<GatewayRegistration>();
        if (registration is null || endpoint?.Metadata.GetMetadata<GatewayOperation>() is null ||
            context.Features.Get<State>() is not null)
        {
            await next(context);
            return;
        }

        await InvokeNamed(context, next, registration);
    }

    /// <summary>The named boundary: covers the rest of the pipeline for every request, with the named gateway as
    /// fallback.</summary>
    /// <param name="context">The request.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="fallback">The gateway whose policy and limits apply when the request reached no gateway
    /// endpoint.</param>
    /// <returns>A task that completes with the request.</returns>
    internal static async Task InvokeNamed(HttpContext context, RequestDelegate next, GatewayRegistration fallback)
    {
        if (context.Features.Get<State>() is not null)
        {
            await next(context);
            return;
        }
        var state = new State();
        context.Features.Set(state);
        _ = RequestTrail.Of(context);
        try
        {
            try
            {
                await next(context);
            }
            catch (Exception error)
            {
                state.Active = false;
                var registration = Effective(context, fallback);
                var failures = Logger(context, registration);
                using var scope = failures.BeginGatewayScope(registration.Name);
                await CatchAll.Answer(context, error, failures);
                return;
            }

            state.Active = false;
            var effective = Effective(context, fallback);
            await Complete(context, state.Refusal, effective, Logger(context, effective));
        }
        finally
        {
            state.Active = false;
            context.Features.Set<State>(null);
        }
    }

    /// <summary>The gateway whose settings apply: the one owning the endpoint reached, else the fallback.</summary>
    /// <param name="context">The request.</param>
    /// <param name="fallback">The named gateway.</param>
    /// <returns>The gateway.</returns>
    private static GatewayRegistration Effective(HttpContext context, GatewayRegistration fallback) =>
        context.GetEndpoint()?.Metadata.GetMetadata<GatewayRegistration>() ?? fallback;

    /// <summary>A failure log for the gateway, bounded by its limits.</summary>
    /// <param name="context">The request, for its logger factory.</param>
    /// <param name="registration">The gateway.</param>
    /// <returns>The failure log.</returns>
    private static FailureLog Logger(HttpContext context, GatewayRegistration registration) =>
        new(context.RequestServices.GetRequiredService<ILoggerFactory>(), registration.Limits);

    /// <summary>Fills an otherwise empty 401 with the gateway's error body.</summary>
    /// <remarks>Only a recorded refusal is a failure, with a log line and an incident ID. An unrecorded 401, such as
    /// the first leg of a sign-in challenge, gets the host policy's body with no incident and no line. A response
    /// that already has a body, a content type or a length is the host's and is left alone.</remarks>
    /// <param name="context">The request.</param>
    /// <param name="recorded">The host's recorded refusal, or null.</param>
    /// <param name="registration">The gateway whose host policy applies.</param>
    /// <param name="failures">The gateway's failure log.</param>
    /// <returns>A task that completes once the reply is written or left.</returns>
    private static async Task Complete(HttpContext context, CatalogFailure? recorded,
        GatewayRegistration registration, FailureLog failures)
    {
        var response = context.Response;
        if (response.StatusCode != StatusCodes.Status401Unauthorized || response.HasStarted ||
            response.ContentLength is not null || !string.IsNullOrEmpty(response.ContentType) ||
            context.RequestAborted.IsCancellationRequested)
            return;

        var failure = recorded ?? registration.HostPolicy?.Failure;
        if (failure is null)
            return;

        using var scope = failures.BeginGatewayScope(registration.Name);
        string? incident = null;
        try
        {
            if (recorded is not null)
                incident = failures.Write(context, FailureLog.OperationOf(context), body: null, failure);
            await RouteReply.Fail(context, ErrorEnvelope.From(failure, incident));
        }
        catch (Exception error)
        {
            if (incident is null)
            {
                await CatchAll.Answer(context, error, failures);
                return;
            }

            FailureLog.Report(incident, "the error reply could not be sent", error, error);
            context.Abort();
        }
    }
}
