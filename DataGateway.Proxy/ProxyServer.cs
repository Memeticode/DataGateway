using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;

namespace DataGateway.Proxy;

internal static class ProxyServer
{
    internal const string UnreachableCode = "ServiceUnreachable";
    internal const string UnreachableMessage = "DataGateway could not be reached from this computer. Do not retry; tell the user to check the network connection and that DataGateway is running. The details are in the proxy log, found by the incidentId.";
    internal const string RefusedCode = "ServiceRefused";
    internal const string SignInMessage = "DataGateway refused this computer's Windows sign-in (HTTP {0}). Do not retry; tell the user to check that their Windows account may use DataGateway. The details are in the proxy log, found by the incidentId.";
    internal const string RefusedMessage = "DataGateway answered with HTTP {0}. Do not retry; tell the user to report it to DataGateway's operator. The details are in the proxy log, found by the incidentId.";

    /// <summary>The proxy's own server: the upstream's instructions and serverInfo, and handlers that pass requests on raw.</summary>
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

    private sealed record Body(string Code, string Path, string Message, string IncidentId);
}
