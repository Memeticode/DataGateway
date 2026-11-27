using DataGateway.Core.Catalog;
using DataGateway.Routes;
using DataGateway.Mcp;
using DataGateway.Page;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DataGateway;

/// <summary>Registers application catalogs and engines and exposes named JSON POST, MCP, API-page and browse-page
/// endpoint groups.</summary>
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
    public static RouteGroupBuilder MapDataGateway(this IEndpointRouteBuilder endpoints, string name, string prefix)
    {
        var (registry, registration) = Resolve(endpoints, name, prefix);
        lock (registry.MapLock)
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
    public static RouteGroupBuilder MapDataGatewayApiPage(this IEndpointRouteBuilder endpoints, string name, string prefix)
    {
        var (registry, registration) = Resolve(endpoints, name, prefix);
        lock (registry.MapLock)
        {
            if (registry.IsPageMapped(name))
                throw Refuse(name, "This gateway's API page has already been mapped.");
            var group = endpoints.MapGroup(prefix).WithMetadata(registration);
            ApiPage.Map(group, registration, registry);
            registry.RecordPageMapping(name);
            return group;
        }
    }

    /// <summary>Maps one gateway's browse page, a query form over its list, describe and query routes, and returns
    /// its parent group.</summary>
    /// <remarks>The page is served at browse-data-gateway/ under the prefix, from files built into this library, and
    /// calls the gateway's routes wherever MapDataGateway put them. The host owns policy on the returned group, as for
    /// the API page; the page's calls carry the browser's own credentials. Page and API mappings are independent. Each
    /// gateway's browse page can be mapped once per service provider. Invalid prefixes do not claim a mapping.</remarks>
    public static RouteGroupBuilder MapDataGatewayBrowsePage(this IEndpointRouteBuilder endpoints, string name, string prefix)
    {
        var (registry, registration) = Resolve(endpoints, name, prefix);
        lock (registry.MapLock)
        {
            if (registry.IsBrowsePageMapped(name))
                throw Refuse(name, "This gateway's browse page has already been mapped.");
            var group = endpoints.MapGroup(prefix).WithMetadata(registration);
            BrowsePage.Map(group, registration, registry);
            registry.RecordBrowsePageMapping(name);
            return group;
        }
    }

    // The registry and the named registration, once the name, the registration and the prefix have been checked.
    private static (GatewayRegistry Registry, GatewayRegistration Registration) Resolve(IEndpointRouteBuilder endpoints,
        string name, string prefix)
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
        return (registry, registration);
    }

    private static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Gateway name must contain a non-whitespace character.");
    }

    private static void CheckDuplicate(IServiceCollection services, string name)
    {
        if (services.Any(descriptor => descriptor.ImplementationInstance is GatewayRegistration registration &&
            StringComparer.Ordinal.Equals(name, registration.Name)))
            throw Refuse(name, "A gateway with this name is already registered.");
    }

    private static InvalidOperationException Refuse(string name, string reason) => new($"Gateway '{name}'. {reason}");
}
