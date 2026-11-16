using DataGateway.Failures;
using DataGateway.Routes;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DataGateway.Mcp;

internal static class McpEndpoint
{
    private sealed class RegistrationMarker
    {
    }

    private sealed record GatewayBinding(GatewayRegistration Registration, FailureLog Failures);

    internal static void EnsureConfigured(IServiceCollection services)
    {
        lock (services)
        {
            if (services.Any(descriptor => descriptor.ServiceType == typeof(RegistrationMarker))) return;
            services.AddMcpServer().WithHttpTransport(ConfigureTransport);
            services.AddSingleton(new RegistrationMarker());
        }
    }

    private static void ConfigureTransport(HttpServerTransportOptions options)
    {
        options.Stateless = true;
        options.ConfigureSessionOptions = ConfigureSessionOptions;
    }

    /// <summary>Refuses an app that also configures the MCP SDK's HTTP transport.</summary>
    /// <remarks>The SDK keeps one set of transport options for every MCP endpoint in the process. DataGateway's
    /// stateless mode would silently remove the app's own server-to-client features, or the app's session setup would
    /// silently replace DataGateway's tools, depending on registration order. Failing at startup says so plainly.
    /// Every IConfigureOptions or IPostConfigureOptions registration for the transport is detected (Configure,
    /// PostConfigure, WithHttpTransport or the app's own class); only the SDK's own setup and DataGateway's are not conflicts.</remarks>
    // MapMcp reads the default (unnamed) options; a registration for another name never reaches it. The options library's
    // own configure and post-configure classes (with or without dependencies) carry that name; a null name means all
    // names. An app's own class, whose target cannot be read, is counted.
    private static bool ReachesDefault(object setup)
    {
        var type = setup.GetType();
        if (type.Namespace != typeof(ConfigureNamedOptions<>).Namespace || type.GetProperty("Name") is not { } property)
            return true;
        var name = (string?)property.GetValue(setup);
        return name is null || name == Options.DefaultName;
    }

    // The SDK's own defaults, registered by every WithHttpTransport call (ModelContextProtocol.AspNetCore 2.1.0); not a conflict.
    private const string SdkTransportSetup = "ModelContextProtocol.AspNetCore.HttpServerTransportOptionsSetup";

    internal static void RefuseSharedTransport(IServiceProvider services, string gateway)
    {
        var ours = ((Action<HttpServerTransportOptions>)ConfigureTransport).Method;
        var foreign = services.GetServices<IConfigureOptions<HttpServerTransportOptions>>()
            .Where(setup => setup.GetType().FullName != SdkTransportSetup
                && !(setup is ConfigureNamedOptions<HttpServerTransportOptions> named && named.Action?.Method == ours)
                && ReachesDefault(setup))
            .Cast<object>()
            .Concat(services.GetServices<IPostConfigureOptions<HttpServerTransportOptions>>().Where(ReachesDefault));
        if (foreign.Any())
            throw new InvalidOperationException($"Gateway '{gateway}'. This app also configures the MCP SDK's HTTP transport " +
                "(HttpServerTransportOptions), which the SDK shares with every MCP endpoint in the process, so DataGateway's " +
                "stateless MCP endpoint and the app's own MCP server would change each other's behaviour. Serve DataGateway " +
                "from its own app, or remove the app's MCP HTTP transport configuration.");
    }

    private static Task ConfigureSessionOptions(HttpContext context, McpServerOptions options,
        CancellationToken token)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<GatewayBinding>() is not { } binding)
            return Task.CompletedTask;

        var registration = binding.Registration;
        options.ServerInfo = new Implementation
        {
            Name = registration.Name,
            Title = registration.McpTitle,
            Version = typeof(DataGatewayBuilder).Assembly.GetName().Version!.ToString(3)
        };
        options.ServerInstructions = registration.McpAbout is null
            ? DataGatewayTools.Instructions
            : registration.McpAbout + " " + DataGatewayTools.Instructions;
        options.ToolCollection = [.. DataGatewayTools.Create(registration, binding.Failures)];
        return Task.CompletedTask;
    }

    internal static void Map(RouteGroupBuilder group, GatewayRegistration registration, FailureLog failures)
    {
        RefuseSharedTransport(((IEndpointRouteBuilder)group).ServiceProvider, registration.Name);
        var mapped = group.MapMcp("/mcp")
            .WithName($"DataGateway:{registration.Name}:mcp")
            .WithMetadata(new GatewayOperation("mcp"), new GatewayBinding(registration, failures))
            .ExcludeFromDescription();

        mapped.Finally(endpoint =>
        {
            var next = endpoint.RequestDelegate!;
            endpoint.RequestDelegate = async context =>
            {
                using var scope = failures.BeginGatewayScope(registration.Name);
                try
                {
                    _ = RequestTrail.Of(context);
                    await next(context);
                }
                catch (Exception error)
                {
                    await CatchAll.Answer(context, error, failures);
                }
            };
        });
    }

    /// <summary>True when the exception is the MCP SDK ending a stateless call whose caller has gone.</summary>
    /// <remarks>The SDK serves each stateless POST with its own McpServer, a request feature. When the caller ends the
    /// request, the SDK cancels the call, waits for it to return, then rethrows the abort out of the endpoint. The call
    /// has already written its one line, or none if nothing failed. Before the server exists (sign-in, body read), an
    /// abort keeps the catch-all's line.</remarks>
    internal static bool EndedByCaller(HttpContext context, Exception error) =>
        error is OperationCanceledException or IOException
        && context.RequestAborted.IsCancellationRequested
        && context.Features.Get<McpServer>() is not null;
}
