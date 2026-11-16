using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DataGateway.Mcp;
using ModelContextProtocol;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>The stdio proxy (src/DataGateway.Proxy) run as a child process against the real DataGateway.Web host on a synthetic catalog.</summary>
public sealed class ProxyTests
{
    private const string UnreachableText = """{"code":"ServiceUnreachable","path":"$","message":"DataGateway could not be reached from this computer. Do not retry; tell the user to check the network connection and that DataGateway is running. The details are in the proxy log, found by the incidentId.","incidentId":"<incident>"}""";

    [Theory]
    [InlineData(null)]
    [InlineData("2025-11-25")]
    public async Task T1_tool_list_instructions_and_server_info_equal_the_servers(string? protocol)
    {
        using var settings = new ErrorLogSettingsScope();
        await using var wire = new ReadmeWire(settings: new Dictionary<string, string?>
            { ["DataGateway:Mcp:Title"] = "Synthetic Title", ["DataGateway:Mcp:About"] = "Synthetic about text." });
        await wire.Start();
        await using var direct = await McpWire.Connect(wire.SourceHost.Client, protocol);
        await using var proxy = await ProxyWire.Connect(wire.SourceHost.Client, protocol);
        Assert.Equal(("DataGateway.Web", "Synthetic Title", "1.0.0"), (proxy.ServerInfo.Name, proxy.ServerInfo.Title, proxy.ServerInfo.Version));
        Assert.StartsWith("Synthetic about text. ", proxy.ServerInstructions);
        Assert.Equal((direct.ServerInfo.Name, direct.ServerInfo.Title, direct.ServerInfo.Version), (proxy.ServerInfo.Name, proxy.ServerInfo.Title, proxy.ServerInfo.Version));
        Assert.Equal(direct.ServerInstructions, proxy.ServerInstructions);
        var expected = (await direct.ListToolsAsync()).Select(t => JsonSerializer.Serialize(t.ProtocolTool)).Order().ToArray();
        Assert.Equal(3, expected.Length);
        Assert.Equal(expected, (await proxy.ListToolsAsync()).Select(t => JsonSerializer.Serialize(t.ProtocolTool)).Order().ToArray());
    }

    [Theory]
    [MemberData(nameof(McpToolTests.Triggers), MemberType = typeof(McpToolTests))]
    public async Task T2_every_documented_failure_is_identical_through_the_proxy(ReadmeTests.Trigger trigger)
    {
        using var settings = new ErrorLogSettingsScope();
        // The body McpToolTests.T9 uses for this setting: a value that the 40-character limit refuses.
        if (trigger.Setting == "MaxJsonLength") trigger = trigger with
        { Body = """{"source":"sales_by_line","query":{"filters":[{"col":"country","op":"eq","value":"xxxxxxxxxxxxxxxxxxxxxxxx"}]}}""" };
        await using var wire = new ReadmeWire(mode: trigger.Mode, settings: trigger.Setting is null ? null :
            new Dictionary<string, string?> { ["DataGateway:Limits:" + trigger.Setting] = trigger.Value });
        await wire.Start();
        var name = DataGatewayTools.Routes.Single(p => p.Value == trigger.Route).Key;
        var host = name == "list_sources" ? wire.ListHost : wire.SourceHost;
        await using var direct = await McpWire.Connect(host.Client);
        await using var proxy = await ProxyWire.Connect(host.Client);
        var expected = await McpWire.Call(direct, name, trigger.Body);
        Assert.True(expected.IsError == true);
        ProxyWire.AssertSame(expected, await McpWire.Call(proxy, name, trigger.Body));
    }

    [Fact]
    public async Task T3_every_readme_example_is_identical_through_the_proxy()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var wire = new ReadmeWire();
        await wire.Start();
        await using var directList = await McpWire.Connect(wire.ListHost.Client);
        await using var directSource = await McpWire.Connect(wire.SourceHost.Client);
        await using var proxyList = await ProxyWire.Connect(wire.ListHost.Client);
        await using var proxySource = await ProxyWire.Connect(wire.SourceHost.Client);
        var compared = 0;
        foreach (var example in ReadmeTests.Examples.Where(e => !e.Anonymous && e.Route is not null))
        {
            using var parsed = JsonDocument.Parse(example.Request ?? "{}");
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) continue;
            var name = DataGatewayTools.Routes.Single(p => p.Value == example.Route).Key;
            var list = name == "list_sources";
            var expected = await McpWire.Call(list ? directList : directSource, name, example.Request);
            ProxyWire.AssertSame(expected, await McpWire.Call(list ? proxyList : proxySource, name, example.Request));
            compared++;
        }
        Assert.True(compared >= 3);
        var direct = await Assert.ThrowsAsync<McpProtocolException>(() => McpWire.Call(directSource, "no_such_tool", "{}").AsTask());
        var through = await Assert.ThrowsAsync<McpProtocolException>(() => McpWire.Call(proxySource, "no_such_tool", "{}").AsTask());
        // A protocol error keeps its code; the proxy's client side adds its own "Request failed (remote)" prefix to the text.
        Assert.Equal(direct.ErrorCode, through.ErrorCode);
        Assert.Contains("Unknown tool: 'no_such_tool'", through.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2025-11-25")]
    public async Task T4_a_cancel_notification_ends_the_forwarded_request_within_two_seconds(string? protocol)
    {
        using var settings = new ErrorLogSettingsScope();
        var seam = new McpCancelTests.Waiting();
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), seam.Engine());
        await host.Start();
        await using var client = await ProxyWire.Connect(host.Client, protocol);
        using var caller = new CancellationTokenSource();
        var pending = ProxyWire.StartCall(client, "cancel-me", "query_source", ErrorLogTests.Body("query"), caller.Token);
        try
        {
            await seam.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(seam.Observed.Task.IsCompleted);
            await ProxyWire.Cancel(client, "cancel-me");
            await seam.Observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { caller.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await host.App.StopAsync();
        Assert.Equal("QueryCancelled", Assert.Single(host.Lines()).GetProperty("Code").GetString());
    }

    [Fact]
    public async Task T5_a_stopped_server_gives_service_unreachable_and_the_next_call_works_when_it_returns()
    {
        using var settings = new ErrorLogSettingsScope();
        var port = FreePort();
        void Fixed(Microsoft.AspNetCore.Builder.WebApplicationBuilder builder) => builder.WebHost.UseSetting("urls", "http://127.0.0.1:" + port);
        var first = new LoggedHost(ErrorLogTests.Catalog(list: true), configure: Fixed);
        await first.Start();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>(); // stderr lines arrive on their own thread
        await using var client = await ProxyWire.Connect(first.Client, stderr: log.Enqueue);
        Assert.NotEqual(true, (await McpWire.Call(client, "list_sources", "{}")).IsError);
        await first.App.StopAsync();
        await first.DisposeAsync();

        var down = await McpWire.Call(client, "list_sources", "{}");
        Assert.True(down.IsError == true);
        Assert.Single(down.Content);
        var text = McpWire.Text(down);
        var incident = JsonDocument.Parse(text).RootElement.GetProperty("incidentId").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", incident);
        Assert.Equal(UnreachableText, text.Replace(incident, "<incident>"));
        var until = DateTime.UtcNow.AddSeconds(5); // the result can arrive before the stderr line that names it
        while (!log.Any(line => line.Contains(incident, StringComparison.Ordinal)) && DateTime.UtcNow < until)
            await Task.Delay(50);
        Assert.Contains(log, line => line.Contains(incident, StringComparison.Ordinal));

        await using var second = new LoggedHost(ErrorLogTests.Catalog(list: true), configure: Fixed);
        await second.Start();
        Assert.NotEqual(true, (await McpWire.Call(client, "list_sources", "{}")).IsError);
    }

    [Fact]
    public async Task T6_a_server_that_is_down_at_start_stops_the_proxy_before_it_answers_anything()
    {
        var url = "http://127.0.0.1:" + FreePort() + "/mcp";
        var (exit, stdout, stderr) = await ProxyWire.Tap([url], []);
        Assert.Equal(1, exit);
        Assert.Empty(stdout);
        Assert.Contains(stderr, line => line.Contains(url, StringComparison.Ordinal));
        foreach (var arguments in new[] { Array.Empty<string>(), ["not a url"], ["ftp://host/mcp"], [url, "extra"] })
        {
            var usage = await ProxyWire.Tap(arguments, []);
            Assert.Equal(2, usage.ExitCode);
            Assert.Empty(usage.Stdout);
            Assert.Equal(["Usage: DataGateway.Proxy <URL of the server's /mcp endpoint>"], usage.Stderr);
        }
    }

    [Fact]
    public async Task T7_stdout_holds_only_json_rpc_and_logs_go_to_stderr_and_a_file()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(ErrorLogTests.Catalog(list: true));
        await host.Start();
        var url = ProxyWire.Endpoint(host.Client).ToString();
        var (exit, stdout, stderr) = await ProxyWire.Tap([url],
        [
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"tap","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_sources","arguments":{}}}"""
        ]);
        Assert.Equal(0, exit);
        Assert.Equal(3, stdout.Length);
        var ids = new List<int>();
        foreach (var line in stdout)
        {
            using var message = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, message.RootElement.ValueKind);
            Assert.Equal("2.0", message.RootElement.GetProperty("jsonrpc").GetString());
            ids.Add(message.RootElement.GetProperty("id").GetInt32());
        }
        Assert.Equal([1, 2, 3], ids.Order());
        Assert.Contains(stderr, line => line.Contains("Connected to " + url, StringComparison.Ordinal));
        Assert.All(stderr, line => Assert.Contains(" INF] ", line, StringComparison.Ordinal)); // a clean run logs no warning
        var logs = Path.Combine(Path.GetDirectoryName(ProxyWire.Dll)!, "logs");
        Assert.Contains(Directory.GetFiles(logs, "datagateway-proxy-*.log"), file =>
        {
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return reader.ReadToEnd().Contains("Connected to " + url, StringComparison.Ordinal);
        });
    }

    // A port nothing listens on now: the test starts a server there later, or expects none.
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
