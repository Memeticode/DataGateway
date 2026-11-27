using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Failures;
using DataGateway.Operations;
using DataGateway.Mcp;

namespace DataGateway.Routes;

internal sealed record GatewayOperation(string Name);

internal static class GatewayRoutes
{
    internal static void Map(RouteGroupBuilder group, GatewayRegistration registration, ILoggerFactory loggers)
    {
        var failures = new FailureLog(loggers, registration.Limits);
        MapPost<SourceListResponse>(group, "/sources/list", SourcesListRoute.Operation, registration, failures,
            (body, catalog, context) => ListSources.RunAsync(body, catalog.ListAsync, context.RequestAborted));
        MapPost<SourceDescribeResponse>(group, "/sources/describe", SourcesDescribeRoute.Operation, registration, failures,
            (body, catalog, context) => DescribeSource.RunAsync(body, catalog.SourceAsync, registration.Limits,
                RequestTrail.Of(context), context.RequestAborted));
        MapPost<QueryResponse>(group, "/query", QueryRoute.Operation, registration, failures,
            (body, catalog, context) => QuerySource.RunAsync(body, catalog.TargetAsync, registration.Limits,
                RequestTrail.Of(context), failures.StoreTypes.Report, context.RequestAborted));
        McpEndpoint.Map(group, registration, failures);
    }

    private static RouteHandlerBuilder MapPost<T>(RouteGroupBuilder group, string path, string operation,
        GatewayRegistration registration, FailureLog failures,
        Func<byte[], GatewayCatalog, HttpContext, Task<CatalogResult<T>>> run) where T : class
    {
        Delegate handler = async (HttpContext context) =>
        {
            using var scope = failures.BeginGatewayScope(registration.Name);
            try
            {
                _ = RequestTrail.Of(context);
                var catalog = registration.CreateCatalog(context.RequestServices);
                await RouteRequest.HandleAsync(context, operation, failures, body => run(body, catalog, context));
            }
            catch (Exception error)
            {
                await CatchAll.Answer(context, error, failures);
            }
        };
        return group.MapPost(path, handler)
            .WithName($"DataGateway:{registration.Name}:{operation}")
            .WithMetadata(new GatewayOperation(operation))
            .Produces<T>(StatusCodes.Status200OK, "application/json");
    }
}
