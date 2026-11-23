using DataGateway.Data;
using DataGateway.Data.SqlServer;
using DataGateway.Failures;
using DataGateway.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>Drives the gateway through its public registration API only, the way an app outside this repository
/// would use it.</summary>
internal static class PublicGatewayProbe
{
    /// <summary>The MCP tools a public registration produces, built without reading the catalog.</summary>
    /// <remarks>The catalog factory throws, so a test fails if building tool metadata ever starts reading the
    /// catalog.</remarks>
    /// <returns>The gateway's MCP tools, built with the shared test metadata.</returns>
    internal static McpServerTool[] Tools()
    {
        var services = new ServiceCollection();
        services.AddDataGateway("text", gateway => gateway
            .UseCatalog<SqlConnection>(_ =>
                throw new InvalidOperationException("Metadata inspection must not read the catalog."))
            .AddEngine(new SqlServerEngine()));
        var registration = Assert.Single(services.Select(item => item.ImplementationInstance)
            .OfType<GatewayRegistration>());
        var failures = new FailureLog(NullLoggerFactory.Instance, registration.Limits);
        return DataGatewayTools.Create(registration, failures, McpMetadataFixture.Seed).ToArray();
    }

    /// <summary>Runs one HTTP operation of a gateway named <c>probe</c> on <paramref name="context"/>, with the given
    /// catalog, log provider and engine.</summary>
    /// <remarks>The endpoint's delegate is called directly on the test's context, so the test reads the response and
    /// the logs without a network. The catalog is armed only after the host is built, so reads made while building see
    /// the startup list.</remarks>
    /// <param name="context">The request context; the caller sets the body and reads the response.</param>
    /// <param name="operation">The operation name: <c>list</c>, <c>describe</c> or <c>query</c>.</param>
    /// <param name="catalog">The test catalog the gateway reads.</param>
    /// <param name="logs">The only log provider, so the test sees every log line.</param>
    /// <param name="engine">The engine to register; SQL Server when null.</param>
    internal static async Task Invoke(DefaultHttpContext context, string operation,
        HostCatalogState catalog, ILoggerProvider logs, IDatabaseEngine? engine = null)
    {
        await using var host = new NamedGatewayHost(builder =>
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Services.AddDataGateway("probe", gateway => gateway
                .UseCatalog(_ => catalog.Create())
                .AddEngine(engine ?? new SqlServerEngine())
                );
        });
        host.App.MapDataGateway("probe", "");
        var endpoint = Assert.Single(host.Endpoints, candidate =>
            candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ==
                "DataGateway:probe:" + operation);
        catalog.Arm();
        await using var scope = host.App.Services.CreateAsyncScope();
        context.RequestServices = scope.ServiceProvider;
        context.SetEndpoint(endpoint);
        context.Request.Method = "POST";
        context.Request.Path = NamedGatewayHost.PathOf(operation);
        context.Request.ContentType = "application/json";
        await endpoint.RequestDelegate!(context);
    }
}
