using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;

namespace DataGateway.Proxy;

/// <summary>The stdio MCP server the proxy runs: it passes the tool list and tool calls to the gateway's
/// <c>/mcp</c>, and turns a failed call into an error result the client can read.</summary>
/// <remarks>The error codes here are the proxy's own, distinct from the gateway's: <see cref="UnreachableCode"/>
/// when the gateway cannot be reached, and <see cref="RefusedCode"/> when it answers with an HTTP error instead of a
/// tool result. A gateway that is up but cannot reach a database answers with its own codes, which pass through
/// unchanged.</remarks>
internal static class ProxyServer
{
    /// <summary>The code when the gateway did not answer.</summary>
    internal const string UnreachableCode = "ServiceUnreachable";
    /// <summary>The message for <see cref="UnreachableCode"/>: what to tell the user, and where the details are.</summary>
    internal const string UnreachableMessage = "DataGateway could not be reached from this computer. Do not retry; tell the user to check the network connection and that DataGateway is running. The details are in the proxy log, found by the incidentId.";
    /// <summary>The code when the gateway answered a tool call with an HTTP error.</summary>
    internal const string RefusedCode = "ServiceRefused";
    /// <summary>The message for a refused sign-in (401 or 403); <c>{0}</c> is the status.</summary>
    internal const string SignInMessage = "DataGateway refused this computer's Windows sign-in (HTTP {0}). Do not retry; tell the user to check that their Windows account may use DataGateway. The details are in the proxy log, found by the incidentId.";
    /// <summary>The message for any other HTTP error; <c>{0}</c> is the status.</summary>
    internal const string RefusedMessage = "DataGateway answered with HTTP {0}. Do not retry; tell the user to report it to DataGateway's operator. The details are in the proxy log, found by the incidentId.";

    /// <summary>The proxy's own server: the upstream's instructions and serverInfo, and handlers that pass requests on raw.</summary>
    /// <remarks>
    /// <para>The initialize answer copies the gateway's <c>serverInfo</c> and instructions. A proxy that only
    /// forwarded tools would answer initialize with its own defaults, and the gateway's name and instructions would
    /// never reach the client.</para>
    /// <para>A tool call that reaches the gateway comes back unchanged, its own errors included. Only a transport
    /// failure (nothing answered, or an HTTP error status) becomes the proxy's error result, logged under a fresh
    /// incident id. Nothing is retried: the next call tries again, so it works once the gateway is back.</para>
    /// </remarks>
    /// <param name="upstream">The client connected to the gateway's <c>/mcp</c>.</param>
    /// <param name="endpoint">The gateway's <c>/mcp</c> URL, for the log line.</param>
    internal static McpServerOptions Options(McpClient upstream, Uri endpoint) => new()
    {
        ServerInfo = upstream.ServerInfo,
        ServerInstructions = upstream.ServerInstructions,
        Handlers = new()
        {
            ListToolsHandler = async (request, token) => await upstream.ListToolsAsync(request.Params ?? new(), token),
            CallToolHandler = async (request, token) =>
            {
                try { return await upstream.CallToolAsync(request.Params!, token); }
                catch (HttpRequestException error)
                {
                    var incident = Guid.NewGuid().ToString("N");
                    Log.Error(error, "Incident {IncidentId}: {Tool}: {Endpoint} failed with {Status}", incident, request.Params!.Name, endpoint.ToString(),
                        error.StatusCode?.ToString() ?? "no answer");
                    return Failed(incident, error.StatusCode);
                }
            }
        }
    };

    /// <summary>The proxy's error body: unreachable when nothing answered, refused when the gateway answered with an HTTP error.</summary>
    /// <remarks>The body has the gateway's own error shape (code, path, message, incident id), so a client reads it
    /// like any other DataGateway error. 502, 503 and 504 count as unreachable: they are what a reverse proxy in
    /// front of a stopped gateway answers.</remarks>
    /// <param name="incidentId">The id of the proxy's own log entry for this failure.</param>
    /// <param name="status">The HTTP status, or null when nothing answered.</param>
    /// <returns>A tool result with <c>isError</c> set.</returns>
    internal static CallToolResult Failed(string incidentId, System.Net.HttpStatusCode? status)
    {
        var (code, message) = status switch
        {
            // No answer, or a reverse proxy answering for a gateway that is down: unreachable.
            null or System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable
                or System.Net.HttpStatusCode.GatewayTimeout => (UnreachableCode, UnreachableMessage),
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                (RefusedCode, string.Format(System.Globalization.CultureInfo.InvariantCulture, SignInMessage, (int)status)),
            _ => (RefusedCode, string.Format(System.Globalization.CultureInfo.InvariantCulture, RefusedMessage, (int)status)),
        };
        return new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new Body(code, "$", message, incidentId), JsonSerializerOptions.Web) }]
        };
    }

    /// <summary>The error body, serialized with web (camelCase) names.</summary>
    private sealed record Body(string Code, string Path, string Message, string IncidentId);
}
