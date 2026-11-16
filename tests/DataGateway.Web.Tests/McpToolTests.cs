using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataGateway.Web.Configuration;
using DataGateway.Mcp;
using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using DataGateway.Core.Queries;
using DataGateway.Data.Catalog;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace DataGateway.Web.Tests;

public sealed class McpToolTests(ITestOutputHelper output)
{
    [Fact]
    public void T15_wire_preserves_values_order_and_text_shape()
    {
        Assert.Equal("{}", Encoding.UTF8.GetString(ToolWire.Body(null)));
        const string body = "{\"query\":{\"n\":1.50,\"dup\":1,\"dup\":2,\"nested\":[null,true,{\"x\":\"\u00e9\"}]},\"<source>\":\"\u00e9\"}";
        Assert.Equal(body, Encoding.UTF8.GetString(ToolWire.Body(McpWire.Arguments(body))));
        var answer = ToolWire.Answer("{}"); Assert.Single(answer.Content); Assert.False(answer.IsError == true);
        var failed = ToolWire.Failed("{}"); Assert.Single(failed.Content); Assert.True(failed.IsError);
        const string id = "0123456789abcdef0123456789abcdef";
        var unexpected = ToolWire.Unexpected(id);
        Assert.True(unexpected.IsError); Assert.Equal(2, unexpected.Content.Count);
        Assert.Equal(JsonSerializer.Serialize(ErrorEnvelope.From(ErrorEnvelope.Unexpected, id).Body, JsonSerializerOptions.Web), McpWire.Text(unexpected));
        Assert.Equal(string.Format(ToolWire.Guidance, id), McpWire.Text(unexpected, 1));
        Assert.Equal(1, McpWire.Text(unexpected, 1).Split(id).Length - 1);
    }

    [Fact]
    public async Task T8_readme_examples_and_tool_table()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var wire = new ReadmeWire(); await wire.Start();
        await using var list = await McpWire.Connect(wire.ListHost.Client);
        await using var source = await McpWire.Connect(wire.SourceHost.Client);
        foreach (var example in ReadmeTests.Examples)
        {
            if (example.Anonymous)
            {
                using var response = await wire.Anonymous.Client.PostAsync("/mcp", new StringContent("{}"));
                Assert.Equal(401, (int)response.StatusCode);
                ReadmeTests.EqualAnswer(example.Answer!.Value, JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()));
                continue;
            }
            using var parsed = JsonDocument.Parse(example.Request ?? "{}");
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) continue;
            var name = DataGatewayTools.Routes.Single(p => p.Value == example.Route).Key;
            var http = name == "list_sources" ? wire.ListHost.Client : wire.SourceHost.Client;
            var before = wire.ListHost.Lines().Length + wire.SourceHost.Lines().Length;
            var route = await ReadmeWire.Send(http, example.Route!, example.Request ?? "{}");
            var result = await McpWire.Call(name == "list_sources" ? list : source, name, example.Request);
            McpWire.SameDocument(route.Body, McpWire.Text(result));
            Assert.Equal(route.Status != 200, result.IsError == true);
            Assert.Single(result.Content);
            if (route.Status == 200) Assert.Equal(before, wire.ListHost.Lines().Length + wire.SourceHost.Lines().Length);
            if (example.Answer is { } expected) ReadmeTests.EqualAnswer(expected, JsonSerializer.Deserialize<JsonElement>(McpWire.Text(result)));
        }
        var markdown = ReadmeTests.Markdown;
        const string table = "<!-- tool-table -->\n| Tool | Arguments | Route |\n| --- | --- | --- |\n| `list_sources` | none | `POST /sources/list` |\n| `describe_source` | `source` | `POST /sources/describe` |\n| `query_source` | `source`, `query` | `POST /query` |";
        Assert.Contains(table, markdown.Replace("\r", ""));
        var tools = await source.ListToolsAsync();
        foreach (var tool in tools)
        {
            Assert.Contains("`" + tool.Name + "`", table);
            Assert.Contains("`POST " + DataGatewayTools.Routes[tool.Name] + "`", table);
            var expected = tool.Name switch { "list_sources" => Array.Empty<string>(), "describe_source" => new[] { "source" }, _ => ["source", "query"] };
            Assert.Equal(expected, tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        }
        NoSdkFailure(wire.ListHost); NoSdkFailure(wire.SourceHost);
    }

    public static IEnumerable<object[]> Triggers() => ReadmeTests.Triggers().Where(row =>
    {
        var trigger = (ReadmeTests.Trigger)row[0];
        return trigger.Mode is not ("anonymous" or "body") && trigger.Body.StartsWith('{');
    });

    [Theory]
    [MemberData(nameof(Triggers))]
    public async Task T9_every_documented_failure_and_line_has_parity(ReadmeTests.Trigger trigger)
    {
        trigger = ReadmeTests.ForHost(trigger);
        using var settings = new ErrorLogSettingsScope();
        if (trigger.Setting == "MaxJsonLength") trigger = trigger with
        { Body = """{"source":"sales_by_line","query":{"filters":[{"col":"country","op":"eq","value":"xxxxxxxxxxxxxxxxxxxxxxxx"}]}}""" };
        await using var wire = new ReadmeWire(mode: trigger.Mode, settings: trigger.Setting is null ? null :
            new Dictionary<string, string?> { ["DataGateway:Limits:" + trigger.Setting] = trigger.Value });
        await wire.Start();
        var name = DataGatewayTools.Routes.Single(p => p.Value == trigger.Route).Key;
        var host = name == "list_sources" ? wire.ListHost : wire.SourceHost;
        await using var client = await McpWire.Connect(host.Client);
        var route = await ReadmeWire.Send(host.Client, trigger.Route, trigger.Body);
        var result = await McpWire.Call(client, name, trigger.Body);
        Assert.True(result.IsError);
        McpWire.SameDocument(route.Body, McpWire.Text(result));
        Assert.Equal(trigger.Mode == "unexpected" ? 2 : 1, result.Content.Count);
        var lines = host.Lines(); Assert.Equal(2, lines.Length);
        using var a = JsonDocument.Parse(route.Body); using var b = JsonDocument.Parse(McpWire.Text(result));
        ReadmeTests.AssertStopGuidance(a.RootElement, trigger);
        ReadmeTests.AssertStopGuidance(b.RootElement, trigger);
        Assert.Equal(a.RootElement.GetProperty("incidentId").GetString(), lines[0].GetProperty("IncidentId").GetString());
        Assert.Equal(b.RootElement.GetProperty("incidentId").GetString(), lines[1].GetProperty("IncidentId").GetString());
        Assert.Equal(name, lines[1].GetProperty("Operation").GetString());
        Assert.Equal(trigger.Code == "InternalError" ? "InvalidCatalog" : trigger.Code, lines[1].GetProperty("Code").GetString());
        EqualLines(lines[0], lines[1]);
        if (trigger.Mode == "unexpected") Assert.Equal(string.Format(ToolWire.Guidance, b.RootElement.GetProperty("incidentId").GetString()), McpWire.Text(result, 1));
        NoSdkFailure(host);
    }

    internal static void EqualLines(JsonElement a, JsonElement b)
    {
        var left = JsonNode.Parse(a.GetRawText())!.AsObject(); var right = JsonNode.Parse(b.GetRawText())!.AsObject();
        foreach (var key in new[] { "IncidentId", "Operation", "@t", "RequestId", "RequestPath", "ConnectionId", "TraceId", "SpanId", "@tr", "@sp" })
        { left.Remove(key); right.Remove(key); }
        // A stack trace lists its caller's frames, so a route and a tool reaching the same fault differ there by design.
        foreach (var line in new[] { left, right })
            if (line["ExceptionText"] is JsonValue value && value.TryGetValue<string>(out var detail))
                line["ExceptionText"] = WithoutFrames(detail);
        var differences = left.Select(p => p.Key).Union(right.Select(p => p.Key)).Where(key => !JsonNode.DeepEquals(left[key], right[key])).ToList();
        Assert.True(JsonNode.DeepEquals(left, right), "Failure line fields differ: " + string.Join(", ", differences));
    }

    internal static string WithoutFrames(string detail) => string.Join("\n", detail.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
        .Where(line => !line.StartsWith("   at ", StringComparison.Ordinal) && !line.TrimStart().StartsWith("--- End of", StringComparison.Ordinal)));

    public static IEnumerable<object?[]> BadArguments()
    {
        foreach (var (name, body) in new (string, string?)[]
        {
            ("describe_source", null), ("describe_source", """{"source":5}"""), ("describe_source", """{"source":null}"""),
            ("describe_source", """{"source":"sales_by_line","extra":1}"""),
            ("query_source", null), ("query_source", """{"source":"sales_by_line"}"""), ("query_source", """{"query":{}}"""),
            ("query_source", """{"source":"sales_by_line","query":"x"}"""),
            ("query_source", """{"source":"sales_by_line","query":{"select":["no_such_column"]}}"""),
            ("query_source", """{"source":"no_such_source","query":"x"}"""), ("list_sources", """{"source":"x"}"""), ("list_sources", null)
        }) yield return [name, body];
    }

    [Theory]
    [MemberData(nameof(BadArguments))]
    public async Task T10_bad_arguments_are_route_refusals(string name, string? body)
    {
        using var settings = new ErrorLogSettingsScope();
        await using var wire = new ReadmeWire(); await wire.Start();
        var host = name == "list_sources" ? wire.ListHost : wire.SourceHost;
        await using var client = await McpWire.Connect(host.Client);
        var route = await ReadmeWire.Send(host.Client, DataGatewayTools.Routes[name], body ?? "{}");
        var result = await McpWire.Call(client, name, body);
        McpWire.SameDocument(route.Body, McpWire.Text(result)); Assert.Single(result.Content);
        Assert.Equal(route.Status != 200, result.IsError == true);
        Assert.DoesNotContain("An error occurred invoking", McpWire.Text(result));
        if (route.Status == 200) Assert.Empty(host.Lines());
        else
        {
            var lines = host.Lines(); Assert.Equal(2, lines.Length);
            Assert.Equal(name, lines[1].GetProperty("Operation").GetString());
            Assert.Equal(body ?? "{}", lines[1].GetProperty("Request").GetString());
            EqualLines(lines[0], lines[1]);
        }
    }

    [Fact]
    public async Task T10_sdk_owned_protocol_observations()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var wire = new ReadmeWire(); await wire.Start();
        using var capture = new McpWire.Capture();
        using var http = new HttpClient(capture) { BaseAddress = wire.SourceHost.Client.BaseAddress };
        await using var client = await McpWire.Connect(http);
        await McpWire.Call(client, "describe_source", """{"source":"sales_by_line"}""");
        var error = await Assert.ThrowsAsync<McpProtocolException>(async () => await McpWire.Call(client, "no_such_tool", null));
        Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
        // Capture a known tool envelope again, then mutate only its arguments.
        await McpWire.Call(client, "describe_source", """{"source":"sales_by_line"}""");
        var validEnvelope = capture.Envelope!;
        foreach (var (label, arguments) in new[]
        {
            ("array", "[]"), ("duplicate arguments", """{"source":"no_such_source","source":"sales_by_line"}"""),
            ("deep JSON", "{\"source\":\"sales_by_line\",\"query\":" + new string('[', 70) + "0" + new string(']', 70) + "}")
        })
        {
            using var response = await McpWire.PostRaw(http, capture.WithArguments(arguments), capture);
            var text = await response.Content.ReadAsStringAsync();
            LoggedHost.Scan.TextResponse(label, response, text);
            var document = ProtocolDocument(text);
            if (label == "duplicate arguments")
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var result = document.GetProperty("result"); Assert.False(result.TryGetProperty("isError", out var failed) && failed.GetBoolean());
                Assert.Equal(1, result.GetProperty("content").GetArrayLength());
                var answer = result.GetProperty("content")[0].GetProperty("text").GetString()!;
                Assert.Equal("sales_by_line", JsonSerializer.Deserialize<JsonElement>(answer).GetProperty("source").GetProperty("name").GetString());
                var route = await ReadmeWire.Send(wire.SourceHost.Client, "/sources/describe", """{"source":"sales_by_line"}""");
                McpWire.SameDocument(route.Body, answer);
                output.WriteLine(label + ": HTTP 200; success; last source wins; one text block; isError unset; describe route parity.");
            }
            else
            {
                Assert.Equal(label == "array" ? 200 : 400, (int)response.StatusCode);
                Assert.Equal(label == "array" ? -32603 : -32600, document.GetProperty("error").GetProperty("code").GetInt32());
                output.WriteLine(label + ": HTTP " + (int)response.StatusCode + " " + document.GetProperty("error").GetRawText());
            }
            Assert.DoesNotContain("Exception", text);
            Assert.DoesNotContain("An error occurred invoking", text);
        }
        foreach (var (label, envelope, accept, protocol) in new[]
        {
            ("malformed JSON", "{", "application/json, text/event-stream", "2025-11-25"),
            ("malformed JSON-RPC", "{}", "application/json, text/event-stream", "2025-11-25"),
            ("wrong Accept", validEnvelope, "text/plain", "2025-11-25"),
            ("wrong protocol", validEnvelope, "application/json, text/event-stream", "1900-01-01")
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            { Content = new StringContent(envelope, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Accept", accept);
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocol);
            using var response = await wire.SourceHost.Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            LoggedHost.Scan.TextResponse(label, response, text);
            output.WriteLine(label + ": HTTP " + (int)response.StatusCode + " " + text);
            Assert.DoesNotContain("Exception", text);
            Assert.DoesNotContain("An error occurred invoking", text);
        }
        Assert.Empty(wire.SourceHost.Lines());
    }

    private static JsonElement ProtocolDocument(string text)
    {
        var data = text.Split('\n').FirstOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal));
        return JsonSerializer.Deserialize<JsonElement>(data is null ? text : data[6..]);
    }

    [Theory]
    [InlineData("list_sources")]
    [InlineData("describe_source")]
    [InlineData("query_source")]
    public async Task T12_answer_faults_are_safe_tool_results(string name)
    {
        using var settings = new ErrorLogSettingsScope();
        var fault = new AnswerFault([typeof(SourceListResponse), typeof(SourceDescribeResponse), typeof(QueryResponse)],
            () => new SyntheticFault("synthetic outer detail",
                new InvalidOperationException("synthetic inner detail")));
        fault.Arm();
        await using var host = new LoggedHost(ErrorLogTests.Catalog(list: name == "list_sources"), ErrorLogTests.Engine(),
            configure: b => b.Services.AddSingleton(new global::DataGateway.GatewayJson(fault)));
        await host.Start(); await using var client = await McpWire.Connect(host.Client);
        var operation = name switch { "list_sources" => "list", "describe_source" => "describe", _ => "query" };
        var body = ErrorLogTests.Body(operation);
        var result = await McpWire.Call(client, name, body);
        Assert.True(result.IsError); Assert.Equal(2, result.Content.Count);
        var json = JsonSerializer.Deserialize<JsonElement>(McpWire.Text(result));
        var id = json.GetProperty("incidentId").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal("UnexpectedError", json.GetProperty("code").GetString());
        Assert.Equal(string.Format(ToolWire.Guidance, id), McpWire.Text(result, 1));
        LoggedHost.Scan.Body("unexpected body", McpWire.Text(result));
        LoggedHost.Scan.Guidance("guidance", McpWire.Text(result, 1));
        Assert.DoesNotContain("synthetic", McpWire.Text(result));
        var line = Assert.Single(host.Lines());
        Assert.Equal(name, line.GetProperty("Operation").GetString());
        Assert.Equal("SyntheticFault", line.GetProperty("ExceptionType").GetString());
        Assert.Equal(id, line.GetProperty("IncidentId").GetString());
        Assert.Equal(body, line.GetProperty("Request").GetString());
        foreach (var part in new[] { "synthetic outer detail", "synthetic inner detail", "SyntheticFault", "   at " })
            Assert.Contains(part, line.GetProperty("ExceptionText").GetString());
        if (name == "query_source")
        { Assert.Equal(JsonValueKind.String, line.GetProperty("Sql").ValueKind); Assert.Equal(JsonValueKind.Object, line.GetProperty("SqlParameters").ValueKind); }
        NoSdkFailure(host);
    }

    [Fact]
    public async Task T12_dependency_activation_fault_is_caught_inside_tool()
    {
        using var settings = new ErrorLogSettingsScope();
        var catalog = new HostCatalogState { FactoryFailure = () => new SyntheticFault("synthetic activation fault") };
        await using var host = new LoggedHost(catalog);
        await host.Start(); await using var client = await McpWire.Connect(host.Client);
        var result = await McpWire.Call(client, "describe_source", """{"source":"sales_by_line"}""");
        Assert.True(result.IsError); Assert.Single(result.Content);
        Assert.Equal("CatalogUnavailable", JsonSerializer.Deserialize<JsonElement>(McpWire.Text(result)).GetProperty("code").GetString());
        var line = Assert.Single(host.Lines());
        Assert.Equal("CatalogUnavailable", line.GetProperty("Code").GetString());
        foreach (var field in new[] { "SqlNumber", "DriverMessage", "ExceptionText" }) Assert.Equal(JsonValueKind.Null, line.GetProperty(field).ValueKind);
        NoSdkFailure(host);
    }

    [Fact]
    public async Task T13_verbose_sdk_logs_and_console_are_checked()
    {
        using var settings = new ErrorLogSettingsScope(); using var console = new ConsoleCapture();
        var allFailures = new List<JsonElement>();
        Dictionary<string, string?> Verbose() => new()
        {
            ["Serilog:MinimumLevel:Override:ModelContextProtocol"] = "Verbose",
            ["Serilog:WriteTo:File:Args:restrictedToMinimumLevel"] = "Verbose"
        };
        await using (var wire = new ReadmeWire(settings: Verbose()))
        {
            await wire.Start();
            await using var source = await McpWire.Connect(wire.SourceHost.Client);
            await using var list = await McpWire.Connect(wire.ListHost.Client);
            foreach (var row in BadArguments())
                Check(await McpWire.Call((string)row[0]! == "list_sources" ? list : source, (string)row[0]!, (string?)row[1]));
            Check(await McpWire.Call(source, "query_source", """{"source":"sales_by_line","query":{"limit":1}}"""));
            await Assert.ThrowsAsync<McpProtocolException>(async () => await McpWire.Call(source, "no_such_tool", null));
            // T10's SDK-owned rows too: the SDK answers them before any tool runs, so only its own log lines are scanned here.
            using var capture = new McpWire.Capture();
            using var http = new HttpClient(capture) { BaseAddress = wire.SourceHost.Client.BaseAddress };
            await using var raw = await McpWire.Connect(http);
            await McpWire.Call(raw, "describe_source", """{"source":"sales_by_line"}""");
            var validEnvelope = capture.Envelope!;
            foreach (var arguments in new[] { "[]", """{"source":"no_such_source","source":"sales_by_line"}""",
                "{\"source\":\"sales_by_line\",\"query\":" + new string('[', 70) + "0" + new string(']', 70) + "}" })
            {
                using var response = await McpWire.PostRaw(http, capture.WithArguments(arguments), capture);
            }
            foreach (var (envelope, accept, protocol) in new[]
            {
                ("{", "application/json, text/event-stream", "2025-11-25"),
                ("{}", "application/json, text/event-stream", "2025-11-25"),
                (validEnvelope, "text/plain", "2025-11-25"),
                (validEnvelope, "application/json, text/event-stream", "1900-01-01")
            })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
                { Content = new StringContent(envelope, Encoding.UTF8, "application/json") };
                request.Headers.TryAddWithoutValidation("Accept", accept);
                request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocol);
                using var response = await wire.SourceHost.Client.SendAsync(request);
            }
            Collect(wire.ListHost); Collect(wire.SourceHost);
        }
        foreach (var row in Triggers())
        {
            var trigger = (ReadmeTests.Trigger)row[0];
            var options = Verbose();
            if (trigger.Setting is { } setting) options["DataGateway:Limits:" + setting] = trigger.Value;
            await using var wire = new ReadmeWire(mode: trigger.Mode, settings: options); await wire.Start();
            var name = DataGatewayTools.Routes.Single(p => p.Value == trigger.Route).Key;
            var host = name == "list_sources" ? wire.ListHost : wire.SourceHost;
            await using var client = await McpWire.Connect(host.Client);
            Check(await McpWire.Call(client, name, trigger.Body));
            Collect(host);
        }
        foreach (var name in DataGatewayTools.Routes.Keys)
        {
            var fault = new AnswerFault([typeof(SourceListResponse), typeof(SourceDescribeResponse), typeof(QueryResponse)],
                () => new SyntheticFault("synthetic outer detail", new InvalidOperationException("synthetic inner detail")));
            fault.Arm();
            await using var host = new LoggedHost(ErrorLogTests.Catalog(list: name == "list_sources"), ErrorLogTests.Engine(), settings: Verbose(),
                configure: b => b.Services.AddSingleton(new global::DataGateway.GatewayJson(fault)));
            await host.Start(); await using var client = await McpWire.Connect(host.Client);
            var operation = name switch { "list_sources" => "list", "describe_source" => "describe", _ => "query" };
            var result = await McpWire.Call(client, name, ErrorLogTests.Body(operation));
            Assert.True(result.IsError); Assert.Equal(2, result.Content.Count); Check(result); Collect(host);
        }
        LoggedHost.Scan.Console("verbose console", console.Lines, allFailures);
        Assert.Empty(console.Errors);
        void Check(CallToolResult result)
        {
            LoggedHost.Scan.Body("verbose tool body", McpWire.Text(result));
            if (result.Content.Count > 1) LoggedHost.Scan.Guidance("verbose guidance", McpWire.Text(result, 1));
        }
        void Collect(LoggedHost host)
        {
            allFailures.AddRange(host.Lines());
            foreach (var line in host.AllLines()) LoggedHost.Scan.LogLine("all categories", line);
        }
    }

    internal static void NoSdkFailure(LoggedHost host) => Assert.DoesNotContain(host.AllLines(), line =>
        line.GetProperty("SourceContext").GetString()!.StartsWith("ModelContextProtocol", StringComparison.Ordinal) &&
        (line.TryGetProperty("@l", out var level) && level.GetString() is "Warning" or "Error" or "Fatal" || line.TryGetProperty("@x", out _)));
}
