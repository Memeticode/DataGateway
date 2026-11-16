using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using DataGateway.Failures;

namespace DataGateway.Routes;

internal static class HostErrors
{
    private sealed class State
    {
        internal bool Active { get; set; } = true;
        internal CatalogFailure? Refusal { get; set; }
    }

    internal static bool TryRecord(HttpContext context, CatalogFailure failure)
    {
        var state = context.Features.Get<State>();
        if (state is null || !state.Active || state.Refusal is not null ||
            context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
            return false;
        state.Refusal = failure with { DriverException = null };
        return true;
    }

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

    private static GatewayRegistration Effective(HttpContext context, GatewayRegistration fallback) =>
        context.GetEndpoint()?.Metadata.GetMetadata<GatewayRegistration>() ?? fallback;

    private static FailureLog Logger(HttpContext context, GatewayRegistration registration) =>
        new(context.RequestServices.GetRequiredService<ILoggerFactory>(), registration.Limits);

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
