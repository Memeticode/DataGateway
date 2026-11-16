using System.Security.Claims;
using System.Text.Json;
using DataGateway.Failures;
using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DataGateway.Mcp;

internal sealed class DataGatewayTool<T>(Tool definition,
    Func<IServiceProvider, byte[], RequestTrail, CancellationToken, Task<CatalogResult<T>>> run,
    Func<IServiceProvider, FailureLog> getFailures) : McpServerTool where T : class
{
    public override Tool ProtocolTool => definition;
    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var services = request.Services!;
        var failures = getFailures(services);
        var caller = request.User ?? new ClaimsPrincipal();
        var trail = new RequestTrail();
        try
        {
            var body = ToolWire.Body(request.Params?.Arguments);
            trail.Body = body;
            var json = GatewayJson.Of(services);
            var result = await run(services, body, trail, cancellationToken);
            if (result.Failure is not { } failure) return ToolWire.Answer(JsonSerializer.Serialize(result.Value, json));
            var incident = failures.Write(caller, cancellationToken.IsCancellationRequested, definition.Name, body, failure, trail);
            return ToolWire.Failed(JsonSerializer.Serialize(ErrorEnvelope.From(failure, incident).Body, json));
        }
        catch (Exception error)
        {
            // A caller's cancel never reaches this catch: the data layer returns it as QueryCancelled or CatalogCancelled.
            // Anything caught here, OperationCanceledException included, is an unexpected fault.
            var incident = failures.WriteUnexpected(caller, cancellationToken.IsCancellationRequested, definition.Name, trail, error);
            return ToolWire.Unexpected(incident);
        }
    }
}
