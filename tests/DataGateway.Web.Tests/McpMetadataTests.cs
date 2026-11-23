using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using DataGateway.Core.Catalog;
using DataGateway.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DataGateway.Web.Tests.NamedGatewayMcpWire;

namespace DataGateway.Web.Tests;

/// <summary>Pins the catalog-owned MCP metadata (server title, about text, tool titles and descriptions): where it
/// is read, how it is validated, and what a client sees when it is invalid or cannot be read.</summary>
/// <remarks>
/// <para>The metadata is read from the catalog on each presentation request (initialize and tools/list), with no
/// snapshot, so an edit in the catalog reaches the next client without a restart.</para>
/// <para>Invalid metadata refuses startup, naming the table and field to fix. Once running, invalid or unreadable
/// metadata fails only the presentation requests, with CatalogUnavailable; tool calls keep their own routing,
/// errors and log fields.</para>
/// </remarks>
public sealed class McpMetadataTests
{
    /// <summary>A raw MCP initialize request.</summary>
    private const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"retail","version":"1"}}}""";

    /// <summary>The API page and document are titled with the gateway name and never read the MCP metadata, which
    /// belongs to MCP clients only.</summary>
    [Fact]
    public async Task T8_page_and_document_use_gateway_name_without_metadata_reads()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture)
        {
            Metadata = McpMetadataFixture.Seed with { Title = "Catalog display", About = "Private MCP About." }
        };
        await using var host = new NamedGatewayHost(builder => builder.Services.AddDataGateway("retail", gateway =>
            gateway.UseCatalog(state.Create).AddEngine(new NamedGatewayEngine())));
        host.App.MapDataGateway("retail", "/data");
        host.App.MapDataGatewayApiPage("retail", "/docs");
        await host.Start();
        var reads = state.MetadataReads;
        var counts = state.Counts;
        state.ReadMetadata = _ => throw new IOException("Page handlers must not read MCP metadata.");
        var html = await host.Client.GetStringAsync("/docs/scalar/");
        Assert.Contains("<title>retail</title>", html);
        var text = await host.Client.GetStringAsync("/docs/openapi/v1.json");
        using var document = JsonDocument.Parse(text);
        Assert.Equal("retail", document.RootElement.GetProperty("info").GetProperty("title").GetString());
        Assert.DoesNotContain("Catalog display", html + text);
        Assert.DoesNotContain("Private MCP About.", html + text);
        Assert.Equal(reads, state.MetadataReads);
        Assert.Equal(counts, state.Counts);
    }

    /// <summary>A metadata edit reaches an open client on its next explicit tool listing and a new client on connect,
    /// while instructions a client already received stay as they were.</summary>
    [Fact]
    public async Task T2_edits_refresh_on_connection_and_explicit_listing_without_a_snapshot()
    {
        await using var pair = new NamedGatewayMcpPair();
        pair.Sa.Metadata = McpMetadataFixture.Seed;
        await pair.Start();
        await using var first = await pair.Connect("a");
        var savedInstructions = first.ServerInstructions;
        var saved = await first.ListToolsAsync(cancellationToken: pair.Token);
        var initial = McpMetadataFixture.Seed;
        pair.Sa.Metadata = initial with
        {
            Title = "Retail catalog",
            About = "Edited retail data.",
            Tools = initial.Tools.Select(tool => tool.ToolName == "query_source"
                ? tool with { Title = "Retail query", Description = "Call describe_source, then query_source for retail data." }
                : tool).ToArray()
        };
        Assert.Equal(initial.About + " " + DataGatewayTools.Instructions, savedInstructions);
        Assert.Equal(savedInstructions, first.ServerInstructions);
        Assert.Equal(initial.Tools.Single(tool => tool.ToolName == "query_source").Description,
            saved.Single(tool => tool.Name == "query_source").Description);
        var refreshed = await first.ListToolsAsync(cancellationToken: pair.Token);
        Assert.Equal("Retail query", refreshed.Single(tool => tool.Name == "query_source").ProtocolTool.Title);
        Assert.Equal("Call describe_source, then query_source for retail data.", refreshed.Single(tool => tool.Name == "query_source").Description);
        Assert.Equal(savedInstructions, first.ServerInstructions);
        await using var second = await pair.Connect("a");
        Assert.Equal("Retail catalog", second.ServerInfo.Title);
        Assert.Equal("Edited retail data. " + DataGatewayTools.Instructions, second.ServerInstructions);
        Assert.Equal("Retail query", (await second.ListToolsAsync(cancellationToken: pair.Token)).Single(tool => tool.Name == "query_source").ProtocolTool.Title);
        pair.Capture.Stateless("/north/mcp");
    }

    /// <summary>Every invalid metadata shape: blank, oversized and multi-line texts per field, and a missing server row,
    /// a missing, unknown or duplicate tool row.</summary>
    public static IEnumerable<object[]> InvalidRows()
    {
        foreach (var field in new[] { "server.title", "server.about", "tool.title", "tool.description" })
        {
            yield return [field, ""];
            yield return [field, "   "];
            yield return [field, new string('x', field.EndsWith("title") ? 81 : 1001)];
            if (field.EndsWith("title"))
            {
                yield return [field, "retail\ntext"];
                yield return [field, "retail\rtext"];
            }
        }
        yield return ["missing.server", ""];
        yield return ["missing.tool", ""];
        yield return ["extra.tool", ""];
        yield return ["duplicate.tool", ""];
    }

    /// <summary>The seed metadata with one invalid shape from <see cref="InvalidRows"/> applied.</summary>
    private static McpMetadata Fault(string field, string value)
    {
        var seed = McpMetadataFixture.Seed;
        return field switch
        {
            "server.title" => seed with { Title = value },
            "server.about" => seed with { About = value },
            "tool.title" => seed with { Tools = seed.Tools.Select(tool => tool with { Title = value }).ToArray() },
            "tool.description" => seed with { Tools = seed.Tools.Select(tool => tool with { Description = value }).ToArray() },
            "missing.server" => null!,
            "missing.tool" => seed with { Tools = seed.Tools.Skip(1).ToArray() },
            "extra.tool" => seed with { Tools = [.. seed.Tools, new("unknown", "Retail", "Retail data.")] },
            "duplicate.tool" => seed with { Tools = [.. seed.Tools, seed.Tools[0]] },
            _ => throw new ArgumentException("Unknown fault field.", nameof(field))
        };
    }

    /// <summary>The catalog table and column the startup error must name for a fault.</summary>
    private static string Field(string field) => field switch
    {
        "server.title" => "mcpserver.title",
        "server.about" => "mcpserver.about",
        "tool.title" => "mcptool.title",
        "tool.description" => "mcptool.description",
        "missing.server" => "mcpserver.id",
        _ => "mcptool.tool_name"
    };

    /// <summary>Invalid metadata stops startup with an error naming the gateway and the catalog field to fix.</summary>
    [Theory]
    [MemberData(nameof(InvalidRows))]
    public async Task T3_invalid_metadata_refuses_startup_naming_table_and_field(string field, string value)
    {
        await using var pair = new NamedGatewayMcpPair();
        pair.Sa.Metadata = Fault(field, value);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pair.Start());
        Assert.Contains(Field(field), error.Message);
        Assert.Contains("Gateway 'a'", error.Message);
    }

    /// <summary>Metadata that turns invalid after startup makes initialize and tools/list answer 503 CatalogUnavailable,
    /// with no session and no tool list, and logs the field at fault.</summary>
    [Theory]
    [MemberData(nameof(InvalidRows))]
    public async Task T3_invalid_metadata_at_read_time_refuses_initialize_and_listing(string field, string value)
    {
        await using var pair = new NamedGatewayMcpPair();
        await pair.Start();
        pair.Sa.Metadata = Fault(field, value);
        foreach (var envelope in new[] { Initialize, LegacyList })
        {
            using var response = await Raw(pair.Http, "/north/mcp", envelope, pair.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.False(response.Headers.Contains("Mcp-Session-Id"));
            using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(pair.Token));
            Assert.Equal("CatalogUnavailable", body.RootElement.GetProperty("code").GetString());
            Assert.Equal("Cannot read the catalog. Check catalog availability and the configured catalog connection. Tell the user, give the incidentId and stop; do not retry.", body.RootElement.GetProperty("message").GetString());
            Assert.False(body.RootElement.TryGetProperty("tools", out _));
            var line = pair.Host.Logs.Failures.Last();
            Assert.Contains(Field(field), Assert.IsType<string>(line.Fields["Message"]));
        }
        Assert.Equal(2, pair.Host.Logs.Failures.Length);
    }

    /// <summary>The 1,000-character limit applies to a tool description after tool names are prefixed and the
    /// description is rewritten, and a description of exactly 1,000 characters is allowed, at startup and at read
    /// time.</summary>
    [Theory]
    [InlineData(999)]
    [InlineData(1000)]
    [InlineData(1001)]
    public async Task T4_final_table_description_budget_is_inclusive_at_startup_and_read_time(int finalLength)
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        const string text = "Call query_source.";
        const string rewritten = "Retail. Call retail_query_source.";
        var description = text + new string('x', finalLength - rewritten.Length);
        var metadata = McpMetadataFixture.Seed with
        {
            Tools = McpMetadataFixture.Seed.Tools.Select(tool => tool.ToolName == "query_source"
                ? tool with { Description = description } : tool).ToArray()
        };
        await using var host = new NamedGatewayHost(builder => builder.Services.AddDataGateway("retail", gateway =>
            gateway.UseCatalog(state.Create).AddEngine(new NamedGatewayEngine()).UseMcpTools("retail", "Retail.")));
        host.App.MapDataGateway("retail", "");
        if (finalLength > 1000)
        {
            state.Metadata = metadata;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Start());
            Assert.Contains("Gateway 'retail'. MCP tool 'retail_query_source' description must be at most 1,000 characters.", error.Message);
            return;
        }
        state.Metadata = metadata;
        await host.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await Connect(host.Client, "/mcp", null, deadline.Token);
        var tool = (await client.ListToolsAsync(cancellationToken: deadline.Token)).Single(tool => tool.Name == "retail_query_source");
        Assert.Equal(finalLength, tool.Description!.Length);
        Assert.StartsWith(rewritten, tool.Description);
        state.Metadata = metadata with { Tools = metadata.Tools.Select(row => row.ToolName == "query_source"
            ? row with { Description = row.Description + new string('x', 1001 - finalLength) } : row).ToArray() };
        using var failed = await Raw(host.Client, "/mcp", LegacyList, deadline.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Contains("at most 1,000 characters", Assert.IsType<string>(host.Logs.Failures.Last().Fields["Message"]));
    }

    /// <summary>With invalid or unreadable metadata, each tool call still reaches its own tool and answers
    /// CatalogUnavailable at that tool's own path, with its own request, step and source in the log line.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T8_metadata_failures_preserve_tool_routing_error_paths_and_logging(bool invalid)
    {
        await using var pair = new NamedGatewayMcpPair();
        await pair.Start();
        await using var client = await pair.Connect("a");
        if (invalid) pair.Sa.Metadata = McpMetadataFixture.Seed with { Title = "" };
        else pair.Sa.ReadMetadata = _ => throw new IOException("private metadata read marker");
        var metadata = pair.Sa.MetadataReads;
        foreach (var (operation, name) in new[] { ("list", "list_sources"), ("describe", "describe_source"), ("query", "query_source") })
        {
            var body = NamedGatewayHost.Body(operation);
            var result = await McpWire.Call(client, name, body, pair.Token);
            Assert.True(result.IsError);
            Assert.Single(result.Content);
            using var json = JsonDocument.Parse(McpWire.Text(result));
            Assert.Equal("CatalogUnavailable", json.RootElement.GetProperty("code").GetString());
            Assert.Equal("Cannot read the catalog. Check catalog availability and the configured catalog connection. Tell the user, give the incidentId and stop; do not retry.", json.RootElement.GetProperty("message").GetString());
            Assert.Equal(operation == "list" ? "$" : "$.source", json.RootElement.GetProperty("path").GetString());
            var line = pair.Host.Logs.Failures.Last();
            Assert.Equal(name, line.Fields["Operation"]);
            Assert.Equal(body, line.Fields["Request"]);
            Assert.Equal("catalog", line.Fields["Step"]);
            Assert.Equal(operation == "list" ? null : "synthetic_items", line.Fields["SourceName"]);
            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, line.Level);
            Assert.Null(line.Fields["ExceptionText"]);
        }
        Assert.Equal(metadata + 3, pair.Sa.MetadataReads);
        foreach (var envelope in new[] { Initialize, LegacyList })
        {
            using var failed = await Raw(pair.Http, "/north/mcp", envelope, pair.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        }
        pair.Capture.Stateless("/north/mcp");
    }

    /// <summary>With invalid or unreadable metadata, a call for a missing or inactive source still answers
    /// UnknownSource exactly as the route does and as it did when the metadata was healthy, with the same log fields.</summary>
    [Theory]
    [InlineData(false, "retail_missing")]
    [InlineData(true, "retail_missing")]
    [InlineData(false, "synthetic_hidden")]
    [InlineData(true, "synthetic_hidden")]
    public async Task Compare_T1_metadata_failures_preserve_unknown_source_and_log_fields(bool invalid, string source)
    {
        await using var pair = new NamedGatewayMcpPair();
        await pair.Start();
        await using var client = await pair.Connect("a");
        foreach (var operation in new[] { "describe", "query" })
        {
            var body = NamedGatewayHost.Body(operation, source);
            var healthyRoute = await pair.Host.Post("/north", operation, body);
            Assert.Equal(404, healthyRoute.Status);
            var healthyTool = await McpWire.Call(client, Tool(operation), body, pair.Token);
            Failed(healthyTool, Unknown("a"));
            var healthyLog = pair.Host.Logs.Failures.Last();
            if (invalid)
                pair.Sa.Metadata = McpMetadataFixture.Seed with { Title = "" };
            else
                pair.Sa.ReadMetadata = _ => throw new IOException("Retail metadata read fault.");
            var reads = pair.Sa.MetadataReads;
            var route = await pair.Host.Post("/north", operation, body);
            Assert.Equal(404, route.Status);
            Assert.Equal(Normalize(healthyRoute.Text, out _), Normalize(route.Text, out _));
            Assert.Equal(Unknown("a"), Normalize(route.Text, out _));
            var routeLog = pair.Host.Logs.Failures.Last();
            var result = await McpWire.Call(client, Tool(operation), body, pair.Token);
            var incident = Failed(result, Unknown("a"));
            var toolLog = Event(pair.Host, incident, "a", Tool(operation), body, "UnknownSource");
            EqualFields(healthyLog, toolLog);
            EqualFields(routeLog, toolLog);
            Assert.Equal("$.source", toolLog.Fields["Path"]);
            Assert.Equal(source, toolLog.Fields["SourceName"]);
            Assert.Equal(source == "synthetic_hidden" ? 103 : (int?)null, toolLog.Fields["SourceId"]);
            Assert.Equal("catalog", toolLog.Fields["Step"]);
            Assert.Equal(reads + 1, pair.Sa.MetadataReads);
            pair.Sa.ReadMetadata = null;
            pair.Sa.Metadata = McpMetadataFixture.Seed;
        }
        Assert.Empty(pair.Ea.Executed);
    }

    /// <summary>Tool names inside the server title are renamed with the tool prefix, and the title's surrounding spaces
    /// are kept.</summary>
    [Theory]
    [InlineData(false, "  Retail describe_source  ")]
    [InlineData(true, "  Retail retail_describe_source  ")]
    public async Task Compare_T2_server_title_renames_tool_references(bool prefixed, string expected)
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture)
        {
            Metadata = McpMetadataFixture.Seed with { Title = "  Retail describe_source  " }
        };
        await using var host = new NamedGatewayHost(builder => builder.Services.AddDataGateway("retail", gateway =>
        {
            gateway.UseCatalog(state.Create).AddEngine(new NamedGatewayEngine());
            if (prefixed)
                gateway.UseMcpTools("retail", "Retail.");
        }));
        host.App.MapDataGateway("retail", "");
        await host.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await Connect(host.Client, "/mcp", null, deadline.Token);
        Assert.Equal(expected, client.ServerInfo.Title);
        var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
        Assert.Contains(tools, tool => tool.Name == (prefixed ? "retail_describe_source" : "describe_source"));
    }

    /// <summary>MCP messages that need no metadata (responses, notifications, requests without an id), which must be
    /// accepted as when healthy, and the two presentation requests, which must fail with 503.</summary>
    public static IEnumerable<object[]> CompareEnvelopes()
    {
        foreach (var invalid in new[] { false, true })
        {
            yield return [invalid, """{"jsonrpc":"2.0","id":1,"result":{}}""", 202];
            yield return [invalid, """{"jsonrpc":"2.0","id":1,"error":{"code":-32603,"message":"Retail error."}}""", 202];
            yield return [invalid, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", 202];
            yield return [invalid, """{"jsonrpc":"2.0","method":"initialize"}""", 202];
            yield return [invalid, """{"jsonrpc":"2.0","method":"tools/list"}""", 202];
            yield return [invalid, Initialize, 503];
            yield return [invalid, LegacyList, 503];
        }
    }

    /// <summary>Unavailable metadata fails only initialize and tools/list; every other message gets its healthy
    /// answer and no log line, and the request body is read to its end either way.</summary>
    [Theory]
    [MemberData(nameof(CompareEnvelopes))]
    public async Task Compare_T3_only_presentation_requests_fail_for_unavailable_metadata(bool invalid, string envelope, int status)
    {
        long position = -1;
        bool readable = false;
        await using var pair = new NamedGatewayMcpPair(pipeline: app => app.Use(async (context, next) =>
        {
            await next(context);
            position = context.Request.Body.Position;
            readable = context.Request.Body.CanRead;
        }));
        await pair.Start();
        using var healthy = await Raw(pair.Http, "/north/mcp", envelope, pair.Token);
        Assert.Equal(status == 503 ? HttpStatusCode.OK : HttpStatusCode.Accepted, healthy.StatusCode);
        if (invalid)
            pair.Sa.Metadata = McpMetadataFixture.Seed with { Title = "" };
        else
            pair.Sa.ReadMetadata = _ => throw new IOException("Retail metadata read fault.");
        var reads = pair.Sa.MetadataReads;
        using var failed = await Raw(pair.Http, "/north/mcp", envelope, pair.Token);
        Assert.Equal(status, (int)failed.StatusCode);
        Assert.Equal(reads + 1, pair.Sa.MetadataReads);
        Assert.True(readable);
        Assert.Equal(Encoding.UTF8.GetByteCount(envelope), position);
        if (status == 202)
        {
            Assert.Equal(await healthy.Content.ReadAsStringAsync(pair.Token), await failed.Content.ReadAsStringAsync(pair.Token));
            Assert.Empty(pair.Host.Logs.Failures);
        }
        else
        {
            using var body = JsonDocument.Parse(await failed.Content.ReadAsByteArrayAsync(pair.Token));
            Assert.Equal("CatalogUnavailable", body.RootElement.GetProperty("code").GetString());
            Assert.Single(pair.Host.Logs.Failures);
        }
    }

    /// <summary>A tools/call sent as UTF-8 or UTF-16 gets the same answer with unavailable metadata as with healthy
    /// metadata, and the request body is read to its end.</summary>
    [Theory]
    [InlineData(false, "describe")]
    [InlineData(true, "describe")]
    [InlineData(false, "query")]
    [InlineData(true, "query")]
    public async Task Compare_T4_utf16_tools_call_preserves_answer_and_request_stream(bool invalid, string operation)
    {
        long position = -1;
        bool readable = false;
        await using var pair = new NamedGatewayMcpPair(
            configure: builder => builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.MaxDepth = 1),
            pipeline: app => app.Use(async (context, next) =>
        {
            await next(context);
            position = context.Request.Body.Position;
            readable = context.Request.Body.CanRead;
        }));
        await pair.Start();
        var arguments = NamedGatewayHost.Body(operation, "retail");
        var envelope = "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"tools/call\",\"params\":{\"name\":\"" + Tool(operation)
            + "\",\"arguments\":" + arguments + "}}";
        using var healthy = await EncodedCall(pair, envelope, Encoding.UTF8);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        var baseline = CallText(await healthy.Content.ReadAsStringAsync(pair.Token));
        Assert.Equal(Unknown("a"), Normalize(baseline, out _));
        if (invalid)
            pair.Sa.Metadata = McpMetadataFixture.Seed with { Title = "" };
        else
            pair.Sa.ReadMetadata = _ => throw new IOException("Retail metadata read fault.");
        foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode })
        {
            var details = pair.Sa.Details;
            var lists = pair.Sa.Lists;
            using var response = await EncodedCall(pair, envelope, encoding);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Normalize(baseline, out _), Normalize(CallText(await response.Content.ReadAsStringAsync(pair.Token)), out _));
            Assert.Equal(details + 1, pair.Sa.Details);
            Assert.Equal(lists + 1, pair.Sa.Lists);
            Assert.True(readable);
            Assert.Equal(encoding.GetByteCount(envelope), position);
            Assert.Equal("retail", pair.Host.Logs.Failures.Last().Fields["SourceName"]);
        }
    }

    /// <summary>Posts an MCP envelope in the given text encoding.</summary>
    private static async Task<HttpResponseMessage> EncodedCall(NamedGatewayMcpPair pair, string envelope, Encoding encoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/north/mcp")
        {
            Content = new StringContent(envelope, encoding, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        return await pair.Http.SendAsync(request, pair.Token);
    }

    /// <summary>The text of a failed tool call's one content block, from a JSON or event-stream reply.</summary>
    private static string CallText(string text)
    {
        var data = text.Split('\n').FirstOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(data is null ? text : data[6..]);
        var result = document.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        return content.GetProperty("text").GetString()!;
    }

    /// <summary>The about text opens the server instructions exactly as stored, surrounding spaces included.</summary>
    [Theory]
    [InlineData("  Retail data.  ")]
    [InlineData("Retail data.")]
    public async Task Compare_T5_about_keeps_surrounding_spaces_byte_for_byte(string about)
    {
        await using var pair = new NamedGatewayMcpPair();
        pair.Sa.Metadata = McpMetadataFixture.Seed with { About = about };
        await pair.Start();
        await using var client = await pair.Connect("a");
        Assert.Equal(Encoding.UTF8.GetBytes(about + " " + Instructions), Encoding.UTF8.GetBytes(client.ServerInstructions!));
    }
}
