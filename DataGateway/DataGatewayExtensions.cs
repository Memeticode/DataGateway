using DataGateway.Core.Catalog;
using DataGateway.Routes;
using DataGateway.Mcp;
using DataGateway.Page;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DataGateway;

/// <summary>Registers application catalogs and engines and exposes named JSON POST, MCP and API-page endpoint groups.</summary>
/// <remarks>The host passes in everything the gateway needs and owns everything around it: the library reads no
/// setting by itself, logs only through <see cref="ILoggerFactory"/>, and returns each endpoint group so the host
/// attaches its own access policy. Registering a gateway maps nothing, and mapping it adds no access
/// policy.</remarks>
public static class DataGatewayExtensions
{
    /// <summary>The category used by the gateway's existing structured failure logger.</summary>
    public const string FailureLogCategory = "DataGateway.Failure";

    /// <summary>Covers downstream middleware on selected library gateway endpoints with gateway error handling.</summary>
    /// <remarks>Install after routing and before the middleware to cover. API, MCP, document, page, redirect and
    /// asset operations are included. Earlier middleware, routing, unmatched requests, method rejections and
    /// host-owned endpoints remain with the host. Repeated installations share one request boundary.</remarks>
    /// <param name="app">The application pipeline builder.</param>
    /// <returns>The same builder.</returns>
    public static IApplicationBuilder UseDataGatewayErrors(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(HostErrors.Invoke);
        return app;
    }

    /// <summary>Covers the downstream host pipeline with a named gateway fallback error boundary.</summary>
    /// <remarks>Install before routing to cover routing, authentication, method rejections, unmatched requests
    /// and host-owned endpoints. After routing, selected gateway metadata supplies policy, limits and Gateway
    /// scope; otherwise the named registration supplies them. The name must already be registered. Repeated
    /// installations share the outer active boundary and complete only once.</remarks>
    /// <param name="app">The required application pipeline builder.</param>
    /// <param name="name">The required ordinal gateway name, containing a non-whitespace character.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="InvalidOperationException">The name is blank or no gateway is registered under it.</exception>
    public static IApplicationBuilder UseDataGatewayErrors(this IApplicationBuilder app, string name)
    {
        ArgumentNullException.ThrowIfNull(app);
        CheckName(name);
        var registration = app.ApplicationServices.GetService<GatewayRegistry>()?.Find(name)
            ?? throw Refuse(name, "Register this gateway with AddDataGateway before installing its host error boundary.");
        app.Use((context, next) => HostErrors.InvokeNamed(context, next, registration));
        return app;
    }

    /// <summary>Records the first provisional host refusal in an active gateway request boundary.</summary>
    /// <remarks>The host must end the refused request with an otherwise empty 401. Recording creates no incident,
    /// log or output; normal completion owns them. Supply caller-safe Message, Path and Step values and an exception
    /// type name for ExceptionType. The raw driver exception is discarded before the failure is retained.</remarks>
    /// <param name="context">The current request.</param>
    /// <param name="failure">The failure to use if the host leaves an empty 401.</param>
    /// <returns>True when accepted; false without an active boundary, after a record, after the response starts
    /// or when the caller has left.</returns>
    public static bool TryRecordDataGatewayRefusal(this HttpContext context, CatalogFailure failure)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(failure);
        return HostErrors.TryRecord(context, failure);
    }

    /// <summary>Registers one ordinal, case-sensitive gateway identity, preserving accepted spelling and whitespace.</summary>
    /// <remarks>Configuration must supply a catalog factory and at least one application-owned engine. Factories
    /// run lazily once per request adapter, with request services, and once in a startup scope. Awaited host startup
    /// validates all registered catalogs, including unmapped gateways. Catalogs and supplied engines remain owned
    /// by the application. A failed configuration is not committed. The original service collection is returned.</remarks>
    /// <param name="services">The host's service collection.</param>
    /// <param name="name">The gateway name, containing a non-whitespace character; used again to map the gateway and
    /// to install its host error boundary.</param>
    /// <param name="configure">Sets the catalog, engines and optional settings on the gateway's builder.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is
    /// null.</exception>
    /// <exception cref="InvalidOperationException">The name is blank or already registered, or the configuration is
    /// incomplete or invalid.</exception>
    public static IServiceCollection AddDataGateway(this IServiceCollection services, string name,
        Action<DataGatewayBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        CheckName(name);
        ArgumentNullException.ThrowIfNull(configure);
        lock (services) CheckDuplicate(services, name);
        var builder = new DataGatewayBuilder(name);
        try { configure(builder); }
        finally { builder.Freeze(); }
        var registration = builder.Complete();
        lock (services)
        {
            CheckDuplicate(services, name);
            McpEndpoint.EnsureConfigured(services);
            ApiPage.AddDocument(services, registration);
            services.AddSingleton(registration);
            services.AddLogging();
            services.TryAddSingleton<GatewayRegistry>();
            services.TryAddSingleton(GatewayJson.Default);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, GatewayStartup>());
        }
        return services;
    }

    /// <summary>Maps list, describe, query and stateless MCP POST endpoints under a prefix and returns their parent group.</summary>
    /// <remarks>The host controls authentication, authorization and other policy on the returned group. Policy
    /// executes before gateway handlers. By default only handlers have gateway exception handling.
    /// UseDataGatewayErrors also covers following host middleware on selected gateway endpoints. Each ordinal
    /// gateway identity can be mapped once per service provider, including when mapping through parent groups.
    /// Empty and slash prefixes map at the root. Invalid prefixes do not claim a mapping.</remarks>
    /// <param name="endpoints">The host's endpoint route builder, or a parent group.</param>
    /// <param name="name">The registered gateway name.</param>
    /// <param name="prefix">The route pattern the endpoints are mapped under.</param>
    /// <returns>The group holding the gateway's endpoints, for the host's policy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The name is not registered, the prefix is not a valid route
    /// pattern, or the gateway is already mapped.</exception>
    public static RouteGroupBuilder MapDataGateway(this IEndpointRouteBuilder endpoints, string name, string prefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        CheckName(name);
        var registry = endpoints.ServiceProvider.GetService<GatewayRegistry>();
        var registration = registry?.Find(name) ?? throw Refuse(name, "Register this gateway with AddDataGateway before mapping it.");
        try
        {
            ArgumentNullException.ThrowIfNull(prefix);
            _ = RoutePatternFactory.Parse(prefix);
        }
        catch (Exception error) when (error is ArgumentException or RoutePatternException)
        {
            throw Refuse(name, "Prefix must be a valid route pattern.");
        }
        lock (registry!.MapLock)
        {
            if (registry.IsMapped(name)) throw Refuse(name, "This gateway has already been mapped.");
            var group = endpoints.MapGroup(prefix).WithMetadata(registration);
            GatewayRoutes.Map(group, registration, endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>());
            registry.RecordMapping(name);
            return group;
        }
    }

    /// <summary>Maps one gateway's API document and Scalar page and returns their parent group.</summary>
    /// <remarks>The host owns policy on the returned group. API and page mappings are independent.
    /// Each gateway page can be mapped once per service provider. Invalid prefixes do not claim a mapping.</remarks>
    /// <param name="endpoints">The host's endpoint route builder, or a parent group.</param>
    /// <param name="name">The registered gateway name.</param>
    /// <param name="prefix">The route pattern the page and document are mapped under; usually the API's own
    /// prefix.</param>
    /// <returns>The group holding the page, its assets and the document, for the host's policy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The name is not registered, the prefix is not a valid route
    /// pattern, or the gateway's page is already mapped. A document route another gateway already uses is refused
    /// when the endpoints are built.</exception>
    public static RouteGroupBuilder MapDataGatewayApiPage(this IEndpointRouteBuilder endpoints, string name, string prefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        CheckName(name);
        var registry = endpoints.ServiceProvider.GetService<GatewayRegistry>();
        var registration = registry?.Find(name) ?? throw Refuse(name, "Register this gateway with AddDataGateway before mapping it.");
        try
        {
            ArgumentNullException.ThrowIfNull(prefix);
            _ = RoutePatternFactory.Parse(prefix);
        }
        catch (Exception error) when (error is ArgumentException or RoutePatternException)
        {
            throw Refuse(name, "Prefix must be a valid route pattern.");
        }
        lock (registry!.MapLock)
        {
            if (registry.IsPageMapped(name))
                throw Refuse(name, "This gateway's API page has already been mapped.");
            var group = endpoints.MapGroup(prefix).WithMetadata(registration);
            ApiPage.Map(group, registration, registry);
            registry.RecordPageMapping(name);
            return group;
        }
    }

    /// <summary>Refuses a null or blank gateway name.</summary>
    /// <param name="name">The supplied name.</param>
    private static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Gateway name must contain a non-whitespace character.");
    }

    /// <summary>Refuses a name already registered in this service collection.</summary>
    /// <remarks>Run before configuration, to fail fast, and again under the lock just before committing, so two
    /// concurrent registrations of one name cannot both succeed.</remarks>
    /// <param name="services">The host's service collection.</param>
    /// <param name="name">The gateway name, compared ordinally.</param>
    private static void CheckDuplicate(IServiceCollection services, string name)
    {
        if (services.Any(descriptor => descriptor.ImplementationInstance is GatewayRegistration registration &&
            StringComparer.Ordinal.Equals(name, registration.Name)))
            throw Refuse(name, "A gateway with this name is already registered.");
    }

    /// <summary>A registration or mapping error that names the gateway.</summary>
    /// <param name="name">The gateway name.</param>
    /// <param name="reason">What is wrong and how to fix it.</param>
    /// <returns>The exception to throw.</returns>
    private static InvalidOperationException Refuse(string name, string reason) => new($"Gateway '{name}'. {reason}");
}
