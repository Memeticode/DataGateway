using System.Security.Claims;
using System.Text.Json;
using DataGateway.Failures;
using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DataGateway.Mcp;

/// <summary>One gateway MCP tool: runs its operation on the call's arguments and answers with the operation's JSON,
/// or with the error body as a tool error.</summary>
/// <remarks>
/// <para>A hand-built <see cref="McpServerTool"/>, not an attribute-declared one: the definition (name, title,
/// description, schema) is built at run time from the catalog's MCP text and the host's prefixes.</para>
/// <para>Every failure is a tool result, never a protocol error: the calling model reads the code, path and message
/// and can act on them. Each failure writes one log line, as on the HTTP routes.</para>
/// </remarks>
/// <typeparam name="T">The operation's answer type.</typeparam>
/// <param name="definition">The tool definition clients list.</param>
/// <param name="operation">The canonical tool name, logged as the operation.</param>
/// <param name="run">Runs the operation on the JSON body built from the arguments.</param>
/// <param name="getFailures">Finds the gateway's failure log in the call's services.</param>
internal sealed class DataGatewayTool<T>(Tool definition, string operation,
    Func<IServiceProvider, byte[], RequestTrail, CancellationToken, Task<CatalogResult<T>>> run,
    Func<IServiceProvider, FailureLog> getFailures) : McpServerTool where T : class
{
    /// <summary>The tool definition clients list.</summary>
    public override Tool ProtocolTool => definition;
    /// <summary>None: the tool declares no attributes.</summary>
    public override IReadOnlyList<object> Metadata => [];

    /// <summary>Runs one tool call.</summary>
    /// <remarks>The arguments are rewritten as a JSON body, so the call goes through the same parsing and checks as the
    /// matching HTTP route.</remarks>
    /// <param name="request">The call, with its arguments, services and caller.</param>
    /// <param name="cancellationToken">The call's cancellation.</param>
    /// <returns>The answer, a tool error with the error body, or the unexpected-error result.</returns>
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
            var incident = failures.Write(caller, cancellationToken.IsCancellationRequested, operation, body, failure, trail);
            return ToolWire.Failed(JsonSerializer.Serialize(ErrorEnvelope.From(failure, incident).Body, json));
        }
        catch (Exception error)
        {
            // A caller's cancel never reaches this catch: the data layer returns it as QueryCancelled or CatalogCancelled.
            // Anything caught here, OperationCanceledException included, is an unexpected fault.
            var incident = failures.WriteUnexpected(caller, cancellationToken.IsCancellationRequested, operation, trail, error);
            return ToolWire.Unexpected(incident);
        }
    }
}
