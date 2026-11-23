using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;
using ModelContextProtocol.Client;

namespace DataGateway.Web.Tests;

/// <summary>Runs the built DataGateway.Proxy as a child process and talks to it over stdio, as a desktop client would.</summary>
internal static class ProxyWire
{
    /// <summary>The full path of the built proxy assembly.</summary>
    /// <remarks>The test project's csproj records where the proxy is built (AssemblyMetadata ProxyDll).</remarks>
    internal static string Dll => Path.GetFullPath(typeof(ProxyWire).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ProxyDll").Value ?? "");

    /// <summary>The gateway's MCP endpoint on the test server.</summary>
    internal static Uri Endpoint(HttpClient server) => new(server.BaseAddress!, "/mcp");

    /// <summary>Starts the proxy against the test server and connects an MCP client to it over stdio.</summary>
    /// <param name="server">The test server the proxy forwards to.</param>
    /// <param name="protocolVersion">The MCP protocol version the client asks for; the client's default when
    /// null.</param>
    /// <param name="stderr">Receives each line the proxy writes to stderr.</param>
    /// <param name="token">Cancels the connect.</param>
    internal static Task<McpClient> Connect(HttpClient server, string? protocolVersion = null, Action<string>? stderr = null, CancellationToken token = default) =>
        McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            { Name = "proxy", Command = "dotnet", Arguments = [Dll, Endpoint(server).ToString()], StandardErrorLines = stderr, ShutdownTimeout = TimeSpan.FromSeconds(1) }),
            new McpClientOptions { ProtocolVersion = protocolVersion }, cancellationToken: token);

    /// <summary>A tools/call with an id the test chooses, so the test can name it in a protocol cancel.</summary>
    internal static Task<JsonRpcResponse> StartCall(McpClient client, string id, string tool, string arguments, CancellationToken token) =>
        client.SendRequestAsync(new JsonRpcRequest
        {
            Id = new RequestId(id), Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(new CallToolRequestParams { Name = tool, Arguments = McpWire.Arguments(arguments) }, McpJsonUtilities.DefaultOptions)
        }, token);

    /// <summary>Sends the protocol's cancel notification for the call with <paramref name="id"/>.</summary>
    /// <remarks>A test sends notifications/cancelled itself: it tests the protocol contract, whatever the client's own
    /// cancellation does.</remarks>
    internal static Task Cancel(McpClient client, string id, CancellationToken token = default) =>
        client.SendNotificationAsync(NotificationMethods.CancelledNotification,
            new CancelledNotificationParams { RequestId = new RequestId(id), Reason = "test" }, cancellationToken: token);

    /// <summary>The same result, text block by text block. Only the incident ID may differ between two calls that fail alike.</summary>
    internal static void AssertSame(CallToolResult expected, CallToolResult actual, bool tolerateAggregates = false)
    {
        Assert.Equal(expected.IsError == true, actual.IsError == true);
        Assert.Equal(expected.Content.Count, actual.Content.Count);
        Assert.Equal(expected.StructuredContent?.GetRawText(), actual.StructuredContent?.GetRawText());
        for (var i = 0; i < expected.Content.Count; i++)
        {
            var left = McpWire.Text(expected, i); var right = McpWire.Text(actual, i);
            if (tolerateAggregates && i == 0) McpWire.SameDocument(left, right, tolerateAggregates: true);
            else Assert.Equal(Normal(left), Normal(right));
        }
    }

    /// <summary>Replaces every 32-hex-digit incident ID with a placeholder, so two failures can be compared.</summary>
    internal static string Normal(string text) => Regex.Replace(text, "[0-9a-f]{32}", "<incident>");

    /// <summary>The proxy with its three standard streams in the test's hands: what a tap on the wire would see.</summary>
    /// <param name="arguments">The proxy's command-line arguments.</param>
    /// <param name="input">Lines written to stdin, in order; stdin is then closed.</param>
    /// <returns>The exit code and the non-empty stdout and stderr lines.</returns>
    internal static async Task<(int ExitCode, string[] Stdout, string[] Stderr)> Tap(string[] arguments, string[] input)
    {
        var start = new ProcessStartInfo("dotnet")
            { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Dll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = new List<string>();
        var patience = TimeSpan.FromSeconds(30);
        foreach (var line in input)
        {
            await process.StandardInput.WriteLineAsync(line);
            // A request gets one reply line before the next message is sent; notifications get none.
            if (line.Contains("\"id\"", StringComparison.Ordinal)) stdout.Add((await process.StandardOutput.ReadLineAsync().WaitAsync(patience)) ?? "");
        }
        process.StandardInput.Close();
        stdout.AddRange(Lines(await process.StandardOutput.ReadToEndAsync().WaitAsync(patience)));
        await process.WaitForExitAsync().WaitAsync(patience);
        return (process.ExitCode, [.. stdout], Lines(await stderr));
    }

    /// <summary>The non-empty, trimmed lines of a stream's text.</summary>
    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
