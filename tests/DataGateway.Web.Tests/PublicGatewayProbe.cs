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

internal static class PublicGatewayProbe
{
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
