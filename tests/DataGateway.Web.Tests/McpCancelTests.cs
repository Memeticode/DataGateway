using DataGateway.Data.SqlServer;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataGateway.Mcp;
using DataGateway.Core.Tests;
using DataGateway.Data.Catalog;
using DataGateway.Data.Queries;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using Xunit;
using Xunit.Abstractions;

namespace DataGateway.Web.Tests;

/// <summary>Pins cancellation on the MCP endpoint: a tool call the caller abandons, or a driver cancellation while the
/// caller is still there, gives the same single failure line the equivalent JSON route gives.</summary>
/// <remarks>When the caller aborts a request, the MCP SDK cancels the call, waits for it, then rethrows the abort
/// out of the endpoint. The call has already logged its one line, so the endpoint's catch-all must drop that abort
/// rather than log a second, unexpected-error line. Tests run on the real host (<see cref="LoggedHost"/>) and read
/// its file log.</remarks>
public sealed class McpCancelTests(ITestOutputHelper output)
{
    /// <summary>The host README's MCP section and the configuration guide state the cancellation contract the tests
    /// below pin.</summary>
    [Fact]
    public void T7_published_docs_state_cancellation_contract()
    {
        var readme = File.ReadAllText(TestFiles.InRepo("DataGateway.Web/README.md"));
        var start = readme.IndexOf("## MCP", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = readme.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var section = end < 0 ? readme[start..] : readme[start..end];
        foreach (var text in new[] { "notifications/cancelled", "QueryCancelled", "CatalogCancelled", "query_timeout_seconds", "source's catalog" })
            Assert.Contains(text, section);
        Assert.Contains("rethrowing the abort", File.ReadAllText(TestFiles.InRepo("DataGateway.Web/CONFIGURATION.md")));
    }

    /// <summary>The endpoint treats an exception as the caller's own abort only when the request was aborted, the MCP
    /// server had started, and the exception is a cancellation or IO error.</summary>
    [Fact]
    public async Task T6a_caller_ended_guard_truth_table()
    {
        await using var server = McpServer.Create(new StreamableHttpServerTransport { Stateless = true }, new McpServerOptions());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        foreach (var aborted in new[] { false, true })
            foreach (var present in new[] { false, true })
                foreach (var error in new Exception[] { new OperationCanceledException(), new IOException(), new SyntheticFault("synthetic") })
                {
                    var context = new DefaultHttpContext { RequestAborted = aborted ? cancelled.Token : CancellationToken.None };
                    if (present) context.Features.Set(server);
                    Assert.Equal(aborted && present && error is OperationCanceledException or IOException, McpEndpoint.EndedByCaller(context, error));
                }
    }

    /// <summary>An abort before the MCP server exists (here, during a stalled sign-in) is not covered by a tool's line,
    /// so the catch-all keeps its one warning line.</summary>
    [Fact]
    public async Task T6b_abort_during_sign_in_keeps_catch_all_line()
    {
        using var settings = new ErrorLogSettingsScope();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new LoggedHost(configure: b => SyntheticSignIn.UseStalling(b, entered));
        await host.Start();
        using var caller = new CancellationTokenSource();
        var pending = host.Client.PostAsync("/mcp", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { caller.Cancel(); }
        await host.App.StopAsync();
        var line = Assert.Single(host.Lines());
        Assert.Equal("UnexpectedError", line.GetProperty("Code").GetString());
        Assert.Equal("Warning", line.GetProperty("@l").GetString());
        Assert.Equal("anonymous", line.GetProperty("Caller").GetString());
        Assert.Equal("mcp", line.GetProperty("Operation").GetString());
        UnexpectedErrorTests.NoFrameworkError(host);
    }

    /// <summary>A <c>notifications/cancelled</c> message is accepted but cannot cancel a call made in another stateless
    /// request; only the caller's own abort ends it.</summary>
    [Fact]
    public async Task T3_notification_cannot_reach_another_stateless_call()
    {
        using var settings = new ErrorLogSettingsScope();
        var seam = new Waiting();
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), seam.Engine());
        await host.Start();
        using var caller = new CancellationTokenSource();
        var pending = McpWire.PostRaw(host.Client,
            """{"jsonrpc":"2.0","id":41,"method":"tools/call","params":{"name":"query_source","arguments":{"source":"synthetic","query":{}}}}""", token: caller.Token);
        try
        {
            await seam.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var notification = await McpWire.PostRaw(host.Client,
                """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":41,"reason":"test"}}""");
            Assert.Equal(HttpStatusCode.Accepted, notification.StatusCode);
            Assert.Equal("", await notification.Content.ReadAsStringAsync());
            Assert.False(seam.Observed.Task.IsCompleted);
            Assert.False(pending.IsCompleted);
            Assert.Empty(host.Lines());
            caller.Cancel();
            await seam.Observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { caller.Cancel(); }
        await host.App.StopAsync();
        Report(host);
        var line = Assert.Single(host.Lines());
        Assert.Equal("QueryCancelled", line.GetProperty("Code").GetString());
        Assert.Equal("Warning", line.GetProperty("@l").GetString());
        Assert.Equal("query_source", line.GetProperty("Operation").GetString());
    }

    /// <summary>A cancellation thrown by the catalog or driver while the caller is still connected is a fault, not a
    /// cancellation: the tool answers and logs exactly as the route does.</summary>
    [Theory]
    [InlineData("list_sources")]
    [InlineData("describe_source")]
    [InlineData("query_source")]
    public async Task T4_driver_OCE_with_live_caller_matches_route_fault(string name)
    {
        using var settings = new ErrorLogSettingsScope();
        var catalog = ErrorLogTests.Catalog();
        if (name == "list_sources") catalog.List = _ => throw new OperationCanceledException("synthetic");
        else if (name == "describe_source") catalog.Detail = (_, _) => throw new OperationCanceledException("synthetic");
        await using var host = new LoggedHost(catalog, UnexpectedErrorTests.FaultEngine(new OperationCanceledException("synthetic")));
        await host.Start();
        await using var client = await McpWire.Connect(host.Client);
        var body = ErrorLogTests.Body(Operation(name));
        var result = await McpWire.Call(client, name, body);
        using var route = await host.Client.PostAsync(ErrorLogTests.Route(Operation(name)), new StringContent(body));
        var routeBody = await route.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, route.StatusCode);
        Assert.True(result.IsError);
        McpWire.SameDocument(routeBody, McpWire.Text(result));
        var answer = JsonSerializer.Deserialize<JsonElement>(McpWire.Text(result));
        var runner = name == "query_source";
        Assert.Equal(runner ? "UnexpectedError" : "InternalError", answer.GetProperty("code").GetString());
        Assert.Equal(runner ? 2 : 1, result.Content.Count);
        if (runner)
            Assert.Equal(string.Format(ToolWire.Guidance, answer.GetProperty("incidentId").GetString()), McpWire.Text(result, 1));
        await host.App.StopAsync();
        var lines = host.Lines(); Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            Assert.Equal("Error", line.GetProperty("@l").GetString());
            Assert.Equal(runner ? "UnexpectedError" : "CatalogCancelled", line.GetProperty("Code").GetString());
            Assert.Equal("OperationCanceledException", line.GetProperty("ExceptionType").GetString());
            if (runner) Assert.Contains("System.OperationCanceledException", line.GetProperty("ExceptionText").GetString());
        }
        EqualFields(lines[0], lines[1]);
        McpToolTests.NoSdkFailure(host);
    }

    /// <summary>Each tool, aborted in the catalog or (for query) in the database, with the default and an explicit
    /// protocol version.</summary>
    public static IEnumerable<object?[]> Aborts()
    {
        foreach (var protocol in new string?[] { null, "2025-11-25" })
            foreach (var (name, catalog) in new[] { ("list_sources", true), ("describe_source", true), ("query_source", true), ("query_source", false) })
                yield return [name, catalog, protocol];
    }

    /// <summary>A tool call and a route call aborted at the same point each write one warning line, and the two lines
    /// agree in every field but the incident id and operation.</summary>
    [Theory]
    [MemberData(nameof(Aborts))]
    public async Task T5_aborted_tool_matches_route_with_one_line_each(string name, bool catalog, string? protocol)
    {
        using var settings = new ErrorLogSettingsScope();
        var seam = new Waiting();
        await using var host = new LoggedHost(catalog ? seam.Catalog(name) : ErrorLogTests.Catalog(), seam.Engine());
        await host.Start();
        await using var client = await McpWire.Connect(host.Client, protocol);
        var body = ErrorLogTests.Body(Operation(name));
        using (var caller = new CancellationTokenSource())
        {
            var pending = McpWire.Call(client, name, body, caller.Token).AsTask();
            try
            {
                await seam.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                caller.Cancel();
                await seam.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            finally { caller.Cancel(); }
        }
        seam.Reset();
        using (var caller = new CancellationTokenSource())
        {
            var pending = host.Client.PostAsync(ErrorLogTests.Route(Operation(name)), new StringContent(body), caller.Token);
            try
            {
                await seam.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                caller.Cancel();
                await seam.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            finally { caller.Cancel(); }
        }
        await host.App.StopAsync();
        Report(host);
        var lines = host.Lines(); Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            Assert.Equal("Warning", line.GetProperty("@l").GetString());
            Assert.Equal(catalog ? "CatalogCancelled" : "QueryCancelled", line.GetProperty("Code").GetString());
            Assert.Equal(catalog ? "catalog" : "command", line.GetProperty("Step").GetString());
            Assert.Equal(name == "list_sources" ? "$" : catalog ? "$.source" : "$.query", line.GetProperty("Path").GetString());
            Assert.Matches("^[0-9a-f]{32}$", line.GetProperty("IncidentId").GetString()!);
        }
        var (tool, route) = PairLines(lines, name);
        Assert.Equal(name, tool.GetProperty("Operation").GetString());
        Assert.Equal(body, tool.GetProperty("Request").GetString());
        EqualFields(tool, route);
        Assert.DoesNotContain(lines, line => line.GetProperty("Code").GetString() == "UnexpectedError");
        foreach (var line in host.AllLines()) LoggedHost.Scan.LogLine("aborted call", line);
    }

    /// <summary>Pins the test helper <see cref="PairLines"/>: it pairs the tool and route lines whatever order the log
    /// wrote them in.</summary>
    [Theory]
    [InlineData("list_sources", false)]
    [InlineData("list_sources", true)]
    [InlineData("describe_source", false)]
    [InlineData("describe_source", true)]
    [InlineData("query_source", false)]
    [InlineData("query_source", true)]
    public void T5_pairing_finds_tool_and_route_in_either_order(string name, bool reverse)
    {
        var tool = JsonSerializer.SerializeToElement(new { Operation = name, Request = "tool request" });
        var route = JsonSerializer.SerializeToElement(new { Operation = Operation(name), Request = "route request" });
        var pair = PairLines(reverse ? [route, tool] : [tool, route], name);
        Assert.Equal(tool.GetRawText(), pair.Tool.GetRawText());
        Assert.Equal(route.GetRawText(), pair.Route.GetRawText());
    }

    /// <summary>Finds the tool's line and the route's line by operation name.</summary>
    private static (JsonElement Tool, JsonElement Route) PairLines(JsonElement[] lines, string name) =>
        (Assert.Single(lines, line => line.GetProperty("Operation").GetString() == name),
         Assert.Single(lines, line => line.GetProperty("Operation").GetString() == Operation(name)));

    /// <summary>Writes the failure lines and any MCP SDK warnings to the test output, to diagnose a failing run.</summary>
    private void Report(LoggedHost host)
    {
        foreach (var line in host.Lines())
            output.WriteLine("DataGateway line: Code={0}; level={1}; Operation={2}; ExceptionType={3}",
                line.GetProperty("Code"), line.GetProperty("@l"), line.GetProperty("Operation"), line.GetProperty("ExceptionType"));
        var sdk = host.AllLines().Where(line => line.GetProperty("SourceContext").GetString()!.StartsWith("ModelContextProtocol", StringComparison.Ordinal)
            && line.TryGetProperty("@l", out var level) && level.GetString() == "Warning").ToArray();
        output.WriteLine("SDK Warning count: {0}", sdk.Length);
        foreach (var line in sdk) output.WriteLine("SDK Warning: category={0}; template={1}", line.GetProperty("SourceContext"), line.GetProperty("@mt"));
    }

    /// <summary>The route operation that matches a tool name.</summary>
    private static string Operation(string name) => name switch { "list_sources" => "list", "describe_source" => "describe", _ => "query" };

    /// <summary>Asserts two failure lines agree in every field but the incident id and operation; exception text is
    /// compared without stack frames, which differ between the two paths.</summary>
    private static void EqualFields(JsonElement tool, JsonElement route)
    {
        foreach (var field in ErrorLogTests.Fields.Except(["IncidentId", "Operation"]))
        {
            var a = JsonNode.Parse(tool.GetProperty(field).GetRawText());
            var b = JsonNode.Parse(route.GetProperty(field).GetRawText());
            if (field == "ExceptionText")
            {
                if (a is JsonValue av && av.TryGetValue<string>(out var at)) a = JsonValue.Create(McpToolTests.WithoutFrames(at));
                if (b is JsonValue bv && bv.TryGetValue<string>(out var bt)) b = JsonValue.Create(McpToolTests.WithoutFrames(bt));
            }
            Assert.True(JsonNode.DeepEquals(a, b), "Failure field differs: " + field);
        }
    }

    /// <summary>A seam that holds a catalog or database read until it is cancelled, signalling when the read starts
    /// and when it sees the cancellation.</summary>
    internal sealed class Waiting
    {
        /// <summary>Completes when a read starts waiting.</summary>
        internal TaskCompletionSource Entered { get; private set; } = Signal();
        /// <summary>Completes when the waiting read sees cancellation.</summary>
        internal TaskCompletionSource Observed { get; private set; } = Signal();
        /// <summary>Replaces both signals for a second call.</summary>
        internal void Reset() { Entered = Signal(); Observed = Signal(); }
        /// <summary>A signal whose continuations run off the setting thread.</summary>
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Signals entry, then waits until the token is cancelled, signalling that too.</summary>
        private async Task Wait(CancellationToken token)
        {
            var observed = Observed;
            using var registration = token.Register(() => observed.TrySetResult());
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
        /// <summary>A SQL Server engine whose query read connects, then waits for cancellation.</summary>
        internal DataGateway.Data.IDatabaseEngine Engine() =>FakeRows.Engine(new SqlServerEngine(), FakeRows.From(async (_, _, connected, token) =>
        { connected(); await Wait(token); return []; }));
        /// <summary>A catalog whose list read (for <c>list_sources</c>) or detail read (for the other tools) waits for
        /// cancellation.</summary>
        internal HostCatalogState Catalog(string name)
        {
            var catalog = new HostCatalogState();
            if (name == "list_sources") catalog.List = async token => { await Wait(token); return []; };
            else catalog.Detail = async (_, token) => { await Wait(token); return null; };
            return catalog;
        }

    }
}
