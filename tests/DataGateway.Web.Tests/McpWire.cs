using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>Helpers for calling a gateway's MCP endpoint in tests, through the official client or as raw HTTP, and
/// for comparing a tool's JSON answer with the JSON API's answer to the same request.</summary>
/// <remarks>The MCP tools and the JSON API share one wire contract, so most MCP tests assert that the tool text is
/// the same document the route returns (<see cref="SameDocument"/>).</remarks>
internal static class McpWire
{
    /// <summary>Connects an MCP client to <c>/mcp</c> on the client's base address over Streamable HTTP, without the
    /// standalone GET stream, which the gateway's stateless endpoint does not offer.</summary>
    /// <param name="http">A client bound to the host.</param>
    /// <param name="protocolVersion">The protocol version to ask for; the client's default when null.</param>
    /// <param name="token">Cancels the connection.</param>
    internal static Task<McpClient> Connect(HttpClient http, string? protocolVersion = null, CancellationToken token = default) =>
        McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false
        }, http), new McpClientOptions { ProtocolVersion = protocolVersion }, cancellationToken: token);

    /// <summary>Turns a JSON object into tool-call arguments, so a test can send the same body it posts to a route.</summary>
    internal static IDictionary<string, JsonElement> Arguments(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    /// <summary>Calls a tool with a JSON object as its arguments, or with no arguments when <paramref name="body"/> is
    /// null.</summary>
    internal static ValueTask<CallToolResult> Call(McpClient client, string name, string? body, CancellationToken token = default) =>
        client.CallToolAsync(new CallToolRequestParams { Name = name, Arguments = body is null ? null : Arguments(body) }, token);

    /// <summary>The text of one content block of a tool result, asserting it is text.</summary>
    internal static string Text(CallToolResult result, int index = 0) => Assert.IsType<TextContentBlock>(result.Content[index]).Text;

    /// <summary>Asserts two JSON documents are the same, member order included, with incident ids only required to be
    /// well formed (each failure has its own).</summary>
    /// <param name="route">The JSON API's answer.</param>
    /// <param name="tool">The tool's answer.</param>
    /// <param name="tolerateAggregates">Allows a tiny difference in <c>sum_</c> and <c>avg_</c> numbers, which two runs
    /// of a floating-point aggregate need not reproduce exactly.</param>
    internal static void SameDocument(string route, string tool, bool tolerateAggregates = false)
    {
        using var left = JsonDocument.Parse(route);
        using var right = JsonDocument.Parse(tool);
        Compare(left.RootElement, right.RootElement, tolerateAggregates, "");
    }

    /// <summary>Compares two elements recursively; <paramref name="name"/> is the member the value belongs to, used for
    /// the aggregate tolerance and the failure message.</summary>
    private static void Compare(JsonElement left, JsonElement right, bool tolerance, string name)
    {
        Assert.Equal(left.ValueKind, right.ValueKind);
        if (left.ValueKind == JsonValueKind.Object)
        {
            var a = left.EnumerateObject().ToArray(); var b = right.EnumerateObject().ToArray();
            Assert.Equal(a.Select(p => p.Name), b.Select(p => p.Name));
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i].Name == "incidentId")
                {
                    Assert.Matches("^[0-9a-f]{32}$", a[i].Value.GetString()!);
                    Assert.Matches("^[0-9a-f]{32}$", b[i].Value.GetString()!);
                }
                else Compare(a[i].Value, b[i].Value, tolerance, a[i].Name);
            }
        }
        else if (left.ValueKind == JsonValueKind.Array)
        {
            Assert.Equal(left.GetArrayLength(), right.GetArrayLength());
            for (var i = 0; i < left.GetArrayLength(); i++) Compare(left[i], right[i], tolerance, name);
        }
        else if (left.ValueKind == JsonValueKind.Number && tolerance && (name.StartsWith("sum_", StringComparison.Ordinal) || name.StartsWith("avg_", StringComparison.Ordinal)))
        {
            var a = left.GetDouble(); var b = right.GetDouble();
            Assert.True(Math.Abs(a - b) <= 1e-9 + 1e-10 * Math.Max(Math.Abs(a), Math.Abs(b)), "Aggregate differs beyond tolerance.");
        }
        else Assert.True(JsonElement.DeepEquals(left, right), "JSON scalar differs at " + name);
    }

    /// <summary>A handler that keeps the last tools/call request the MCP client sends, body and headers, so a test can
    /// replay it with changed arguments the client itself would not send.</summary>
    internal sealed class Capture : DelegatingHandler
    {
        /// <summary>The JSON-RPC body of the last tools/call request.</summary>
        internal string? Envelope { get; private set; }
        /// <summary>The headers of the last tools/call request, protocol version included.</summary>
        internal Dictionary<string, string[]> Headers { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Creates the handler over a client that sends the current user's credentials.</summary>
        internal Capture() : base(new HttpClientHandler { UseDefaultCredentials = true }) { }
        /// <summary>Keeps a tools/call request's body and headers, then sends it.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post && request.Content is not null)
            {
                var text = await request.Content.ReadAsStringAsync(token);
                if (text.Contains("tools/call", StringComparison.Ordinal))
                {
                    Envelope = text;
                    Headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
                }
            }
            return await base.SendAsync(request, token);
        }

        /// <summary>The captured envelope with its arguments replaced by raw JSON text, which may be malformed or of any
        /// type.</summary>
        /// <param name="arguments">The raw JSON to put in <c>params.arguments</c>.</param>
        internal string WithArguments(string arguments)
        {
            var root = JsonNode.Parse(Envelope!)!.AsObject();
            root["params"]!.AsObject().Remove("arguments");
            var prefix = root["params"]!.ToJsonString();
            var replacement = prefix[..^1] + ",\"arguments\":" + arguments + "}";
            var whole = root.ToJsonString();
            return whole.Replace(prefix, replacement, StringComparison.Ordinal);
        }
    }

    /// <summary>Posts a JSON-RPC envelope to <c>/mcp</c> as raw text.</summary>
    /// <param name="http">A client bound to the host.</param>
    /// <param name="envelope">The request body.</param>
    /// <param name="captured">When given, sends its captured headers, so the request looks like the client's own; otherwise
    /// sends the headers a stateless MCP request needs.</param>
    /// <param name="token">Cancels the request.</param>
    internal static async Task<HttpResponseMessage> PostRaw(HttpClient http, string envelope, Capture? captured = null, CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        { Content = new StringContent(envelope, Encoding.UTF8, "application/json") };
        if (captured is null)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-11-25");
        }
        else foreach (var (key, values) in captured.Headers) request.Headers.TryAddWithoutValidation(key, values);
        return await http.SendAsync(request, token);
    }
}
