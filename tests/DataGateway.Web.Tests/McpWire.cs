using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DataGateway.Web.Tests;

internal static class McpWire
{
    internal static Task<McpClient> Connect(HttpClient http, string? protocolVersion = null, CancellationToken token = default) =>
        McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false
        }, http), new McpClientOptions { ProtocolVersion = protocolVersion }, cancellationToken: token);

    internal static IDictionary<string, JsonElement> Arguments(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    internal static ValueTask<CallToolResult> Call(McpClient client, string name, string? body, CancellationToken token = default) =>
        client.CallToolAsync(new CallToolRequestParams { Name = name, Arguments = body is null ? null : Arguments(body) }, token);

    internal static string Text(CallToolResult result, int index = 0) => Assert.IsType<TextContentBlock>(result.Content[index]).Text;

    internal static void SameDocument(string route, string tool, bool tolerateAggregates = false)
    {
        using var left = JsonDocument.Parse(route);
        using var right = JsonDocument.Parse(tool);
        Compare(left.RootElement, right.RootElement, tolerateAggregates, "");
    }

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

    internal sealed class Capture : DelegatingHandler
    {
        internal string? Envelope { get; private set; }
        internal Dictionary<string, string[]> Headers { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        internal Capture() : base(new HttpClientHandler { UseDefaultCredentials = true }) { }
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
