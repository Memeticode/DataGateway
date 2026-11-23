using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Failures;
using DataGateway.Operations;
using DataGateway.Mcp;

namespace DataGateway.Routes;

/// <summary>Endpoint metadata naming a gateway operation: list, describe, query, mcp, document or page.</summary>
/// <remarks>It marks the endpoint as the gateway's, for the error boundary and the API document, and names the
/// operation in each log line.</remarks>
/// <param name="Name">The operation name.</param>
internal sealed record GatewayOperation(string Name);

/// <summary>Maps a gateway's three JSON POST routes and its MCP endpoint.</summary>
internal static class GatewayRoutes
{
    /// <summary>Maps <c>/sources/list</c>, <c>/sources/describe</c>, <c>/query</c> and <c>/mcp</c> under the group.</summary>
    /// <param name="group">The gateway's group, already carrying its registration as metadata.</param>
    /// <param name="registration">The gateway.</param>
    /// <param name="loggers">The host's logger factory, for the gateway's failure log.</param>
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
                RequestTrail.Of(context), context.RequestAborted));
        McpEndpoint.Map(group, registration, failures);
    }

    /// <summary>Maps one POST route that reads the body, runs the operation with a fresh catalog adapter and answers
    /// once.</summary>
    /// <remarks>The handler takes the raw body, not a bound parameter, so a body that is not JSON reaches the
    /// operation and is refused with the gateway's own code and message.</remarks>
    /// <typeparam name="T">The operation's answer type, declared for the API document.</typeparam>
    /// <param name="group">The gateway's group.</param>
    /// <param name="path">The route.</param>
    /// <param name="operation">The operation name.</param>
    /// <param name="registration">The gateway.</param>
    /// <param name="failures">The gateway's failure log.</param>
    /// <param name="run">Runs the operation on the body.</param>
    /// <returns>The route's builder.</returns>
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
