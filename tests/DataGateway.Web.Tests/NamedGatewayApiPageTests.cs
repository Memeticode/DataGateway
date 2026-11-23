using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataGateway.Data.Sqlite;
using DataGateway.Page;
using DataGateway.Web.Page;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>Pins the API page of named gateways: <c>MapDataGatewayApiPage</c> serves one gateway's OpenAPI document and
/// Scalar page, built from that gateway alone, under the prefix the host chooses.</summary>
/// <remarks>
/// <para>Each document describes only its own gateway's operations, carries no sign-in or server details (sign-in is
/// the host's, not the library's), and is identical to its neighbours apart from the title and paths. A document
/// that leaked another gateway's name, paths or policy would send a reader to the wrong endpoint.</para>
/// <para>The page loads only assets packaged with the library, so it works without internet access and sends
/// nothing to a third party.</para>
/// <para>Building a document reads no catalog: the counters on <see cref="NamedGatewayState"/> stay unchanged.</para>
/// </remarks>
public sealed class NamedGatewayApiPageTests
{
    /// <summary>The Scalar assets the library serves next to every page.</summary>
    private static readonly string[] Assets = ["scalar.js", "scalar.aspnetcore.js", "favicon.svg"];
    /// <summary>The three operation ids every document must carry.</summary>
    private static readonly string[] Operations = ["list", "describe", "query"];
    /// <summary>Creates the deadline that bounds one test.</summary>
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(90));

    /// <summary>Creates a catalog with one source under <paramref name="name"/>, whose row ids are shifted by
    /// <paramref name="offset"/>, so answers from two fixtures can be told apart.</summary>
    /// <param name="name">The source name.</param>
    /// <param name="offset">The amount added to every row id.</param>
    /// <returns>The fixture; the caller disposes it.</returns>
    private static SqliteFixture Fixture(string name = "synthetic_items", int offset = 0)
    {
        var fixture = new SqliteFixture();
        fixture.Change("DELETE FROM gateway_source WHERE id <> 101; UPDATE gateway_source SET name=@v0 WHERE id=101; UPDATE proof_items SET id=id+@v1;", name, offset);
        return fixture;
    }

    /// <summary>Registers a named gateway over the fixture's catalog with the real SQLite engine.</summary>
    /// <param name="services">The host's services.</param>
    /// <param name="name">The gateway name.</param>
    /// <param name="state">The catalog state.</param>
    /// <param name="title">The MCP server title, or null for the default.</param>
    /// <param name="about">The MCP server description, or null for the default.</param>
    private static void Register(IServiceCollection services, string name, NamedGatewayState state, string? title = null, string? about = null)
    {
        state.Metadata = McpMetadataFixture.Seed with { Title = title ?? "DataGateway", About = about ?? McpMetadataFixture.Seed.About };
        services.AddDataGateway(name, gateway => gateway.UseCatalog<SqliteConnection>(_ => new NamedGatewayCatalog(state, Guid.Empty)).AddEngine(new SqliteEngine()));
    }

    /// <summary>Gets a path, asserts 200 and, when given, the content type, and returns the body bytes.</summary>
    /// <param name="client">The client to send with.</param>
    /// <param name="path">The path to get.</param>
    /// <param name="contentType">The expected content type, or null to skip that check.</param>
    /// <returns>The body bytes.</returns>
    private static async Task<byte[]> Get(HttpClient client, string path, string? contentType = null)
    {
        using var response = await client.GetAsync(path);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (contentType is not null)
            Assert.Equal(contentType, response.Content.Headers.ContentType?.ToString());
        return bytes;
    }

    /// <summary>Parses bytes into a JSON element that outlives its document.</summary>
    private static JsonElement Json(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        return json.RootElement.Clone();
    }

    /// <summary>Finds the POST operation with the given operation id in an OpenAPI document.</summary>
    private static JsonElement Operation(JsonElement document, string operation) =>
        Assert.Single(document.GetProperty("paths").EnumerateObject().Select(path => path.Value.GetProperty("post")), value => value.GetProperty("operationId").GetString() == operation);

    /// <summary>Asserts that two name sets are equal, ignoring order.</summary>
    private static void Names(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

    /// <summary>Asserts that every <c>$ref</c> in an OpenAPI document points to an object that exists in the same
    /// document.</summary>
    /// <param name="document">The whole document, to resolve references against.</param>
    /// <param name="value">The part to search.</param>
    private static void References(JsonElement document, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name == "$ref")
                {
                    var reference = property.Value.GetString()!;
                    Assert.StartsWith("#/", reference);
                    var target = document;
                    foreach (var segment in reference[2..].Split('/'))
                        target = target.GetProperty(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
                    Assert.Equal(JsonValueKind.Object, target.ValueKind);
                }
                References(document, property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                References(document, item);
    }

    /// <summary>Follows <c>$ref</c> links from a schema until it reaches a schema without one.</summary>
    private static JsonElement Resolve(JsonElement document, JsonElement schema)
    {
        while (schema.TryGetProperty("$ref", out var reference))
        {
            var target = document;
            foreach (var segment in reference.GetString()![2..].Split('/'))
                target = target.GetProperty(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
            schema = target;
        }
        return schema;
    }

    /// <summary>Asserts the shape every named gateway document shares and returns it parsed.</summary>
    /// <remarks>The document must list exactly the gateway's three POST operations under its own prefix, carry the
    /// gateway name as its title, and say nothing about sign-in, servers or absolute URLs: host policy is added by the
    /// host, never by the library.</remarks>
    /// <param name="bytes">The document as served.</param>
    /// <param name="prefix">The prefix the gateway's operations are mapped under.</param>
    /// <param name="title">The expected title, which is the gateway name.</param>
    /// <returns>The parsed document.</returns>
    private static JsonElement Document(byte[] bytes, string prefix, string title)
    {
        var document = Json(bytes);
        Assert.StartsWith("3.1.", document.GetProperty("openapi").GetString());
        Assert.Equal(title, document.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("v1", document.GetProperty("info").GetProperty("version").GetString());
        Assert.Equal("A read-only data gateway.", document.GetProperty("info").GetProperty("description").GetString());
        Names(Operations.Select(operation => prefix + NamedGatewayHost.PathOf(operation)), document.GetProperty("paths").EnumerateObject().Select(path => path.Name));
        foreach (var path in document.GetProperty("paths").EnumerateObject())
            Assert.Equal("post", Assert.Single(path.Value.EnumerateObject()).Name);
        Names(Operations, document.GetProperty("paths").EnumerateObject().Select(path => path.Value.GetProperty("post").GetProperty("operationId").GetString()!));
        foreach (var forbidden in new[] { "NotAuthenticated", "sign-in", "Negotiate", "WWW-Authenticate", "\"401\"", "\"security\"", "\"securitySchemes\"", "http://", "https://", "datagateway-" })
            Assert.DoesNotContain(forbidden, Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
        Assert.True(!document.TryGetProperty("servers", out var servers) || servers.GetArrayLength() == 0);
        References(document, document);
        return document;
    }

    /// <summary>Calls each operation at the path the document gives and asserts that the answer matches the documented
    /// response schema and comes from the expected source.</summary>
    /// <param name="client">The client to send with.</param>
    /// <param name="document">The gateway's document.</param>
    /// <param name="source">The source the gateway's catalog holds.</param>
    /// <param name="first">The first row id that source returns.</param>
    private static async Task Witness(HttpClient client, JsonElement document, string source, int first)
    {
        foreach (var operation in Operations)
        {
            var path = Assert.Single(document.GetProperty("paths").EnumerateObject(), entry => entry.Value.GetProperty("post").GetProperty("operationId").GetString() == operation).Name;
            using var response = await client.PostAsync(path, new StringContent(NamedGatewayHost.Body(operation, source), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var answer = Json(await response.Content.ReadAsByteArrayAsync());
            var schema = Resolve(document, Operation(document, operation).GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
            Names(schema.GetProperty("properties").EnumerateObject().Select(property => property.Name), answer.EnumerateObject().Select(property => property.Name));
            if (operation == "list")
                Assert.Equal(source, Assert.Single(answer.GetProperty("sources").EnumerateArray()).GetProperty("name").GetString());
            else if (operation == "describe")
            {
                Assert.Equal(source, answer.GetProperty("source").GetProperty("name").GetString());
                Assert.Contains("id", answer.GetProperty("querySchema").GetProperty("columns").EnumerateArray().Select(column => column.GetProperty("name").GetString()));
            }
            else
            {
                Assert.Equal(source, answer.GetProperty("source").GetString());
                Assert.Equal("id", Assert.Single(answer.GetProperty("columns").EnumerateArray()).GetProperty("name").GetString());
                Assert.Equal(new[] { first, first + 1 }, answer.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("id").GetInt32()));
            }
        }
    }

    /// <summary>One host with two gateways, <c>north</c> and <c>north:archive</c>, over different catalogs, each with
    /// its own API and page prefix, plus ordinary host endpoints beside them.</summary>
    /// <remarks>The second name starts with the first, and a host endpoint is given a name that looks like a gateway
    /// endpoint name (<c>DataGateway:north:list:pretender</c>). A gateway's endpoints are recognised by the
    /// registration attached to them, never by parsing endpoint names, so a host endpoint whose name only looks like a
    /// gateway's must stay out of that gateway's document.</remarks>
    private sealed class Pair : IAsyncDisposable
    {
        /// <summary>The first gateway's catalog.</summary>
        internal readonly SqliteFixture North = Fixture("north_items");
        /// <summary>The second gateway's catalog, with ids shifted by 100.</summary>
        internal readonly SqliteFixture South = Fixture("south_items", 100);
        /// <summary>The first gateway's catalog state.</summary>
        internal readonly NamedGatewayState A;
        /// <summary>The second gateway's catalog state.</summary>
        internal readonly NamedGatewayState B;
        /// <summary>The host that serves both gateways.</summary>
        internal readonly NamedGatewayHost Host;
        /// <summary>Registers and maps both gateways, their pages and the ordinary endpoints.</summary>
        internal Pair()
        {
            A = new(North);
            B = new(South);
            Host = new(builder =>
            {
                Register(builder.Services, "north", A, "North display");
                Register(builder.Services, "north:archive", B, "South display");
            });
            Host.App.MapDataGateway("north", "/north");
            Host.App.MapDataGateway("north:archive", "/south");
            Host.App.MapDataGatewayApiPage("north", "/north-docs");
            Host.App.MapDataGatewayApiPage("north:archive", "/south-docs");
            Host.App.MapGet("/ordinary", () => "ordinary");
            Host.App.MapPost("/ordinary-post", () => "ordinary").WithName("DataGateway:north:list:pretender");
        }
        /// <summary>Stops the host and releases both catalogs.</summary>
        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            South.Dispose();
            North.Dispose();
        }
    }

    /// <summary>A minimal host, written without any test helper, serves the JSON routes, the MCP endpoint and the API
    /// page for one named gateway.</summary>
    [Fact]
    public async Task T01_twenty_line_host_serves_routes_mcp_and_page()
    {
        using var fixture = Fixture();
        var state = new NamedGatewayState(fixture);
        using var deadline = Deadline();
        var token = deadline.Token;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production", ApplicationName = typeof(NamedGatewayApiPageTests).Assembly.GetName().Name });
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(server => server.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Services.AddDataGateway("bare", gateway => gateway
            .UseCatalog<Microsoft.Data.Sqlite.SqliteConnection>(_ => new NamedGatewayCatalog(state, Guid.Empty))
            .AddEngine(new DataGateway.Data.Sqlite.SqliteEngine()));
        await using var app = builder.Build();
        app.MapDataGateway("bare", "/data");
        app.MapDataGatewayApiPage("bare", "/docs");
        await app.StartAsync(token);
        var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };
        var document = Document(await Get(client, "/docs/openapi/v1.json"), "/data", "bare");
        await Witness(client, document, "synthetic_items", 1);
        await using var mcp = await NamedGatewayMcpWire.Connect(client, "/data/mcp", null, token);
        var tools = await mcp.ListToolsAsync(cancellationToken: token);
        Names(["list_sources", "describe_source", "query_source"], tools.Select(tool => tool.Name));
        var result = await McpWire.Call(mcp, "list_sources", "{}", token);
        Assert.False(result.IsError == true);
        Assert.Equal("synthetic_items", Assert.Single(Json(Encoding.UTF8.GetBytes(McpWire.Text(result))).GetProperty("sources").EnumerateArray()).GetProperty("name").GetString());
        await Page(client, "/docs/scalar/", "/docs/openapi/v1.json", await Get(client, "/docs/openapi/v1.json"));
    }

    /// <summary>Each page serves its own gateway's document, the same bytes on every request, and the two documents
    /// differ only in title and paths.</summary>
    [Fact]
    public async Task T02_documents_select_exact_gateway_metadata()
    {
        await using var pair = new Pair();
        await pair.Host.Start();
        var north = await Get(pair.Host.Client, "/north-docs/openapi/v1.json");
        var south = await Get(pair.Host.Client, "/south-docs/openapi/v1.json");
        await Witness(pair.Host.Client, Document(north, "/north", "north"), "north_items", 1);
        await Witness(pair.Host.Client, Document(south, "/south", "north:archive"), "south_items", 101);
        Assert.Equal(north, await Get(pair.Host.Client, "/north-docs/openapi/v1.json"));
        Assert.Equal(south, await Get(pair.Host.Client, "/south-docs/openapi/v1.json"));
        Assert.Equal(Normalize(north, "/north", "north"), Normalize(south, "/south", "north:archive"));
    }

    /// <summary>Replaces a document's title and operation paths with fixed placeholders, after checking each occurs
    /// exactly once, so two gateways' documents can be compared.</summary>
    private static string Normalize(byte[] bytes, string prefix, string title)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var targets = new[] { JsonSerializer.Serialize(title) }.Concat(Operations.Select(operation => JsonSerializer.Serialize(prefix + NamedGatewayHost.PathOf(operation)))).ToArray();
        for (var index = 0; index < targets.Length; index++)
        {
            Assert.Equal(1, text.Split(targets[index], StringSplitOptions.None).Length - 1);
            text = text.Replace(targets[index], JsonSerializer.Serialize("SENTINEL" + index), StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>A named gateway's operations, components, examples and error responses match the example host's golden
    /// document exactly.</summary>
    [Fact]
    public async Task T03_named_operations_preserve_the_shared_contract()
    {
        await using var pair = new Pair();
        await pair.Host.Start();
        var document = Document(await Get(pair.Host.Client, "/north-docs/openapi/v1.json"), "/north", "north");
        var golden = Json(WebApiPageGolden.Document());
        Assert.Equal(golden.GetProperty("components").GetRawText(), document.GetProperty("components").GetRawText());
        foreach (var operation in Operations)
        {
            var actual = Operation(document, operation);
            var original = Operation(golden, operation);
            foreach (var property in new[] { "summary", "description", "requestBody" })
                Assert.Equal(original.GetProperty(property).GetRawText(), actual.GetProperty(property).GetRawText());
            var responses = actual.GetProperty("responses");
            Names(Table(operation).Keys.Append("200"), responses.EnumerateObject().Select(response => response.Name));
            foreach (var response in responses.EnumerateObject())
                Assert.Equal(original.GetProperty("responses").GetProperty(response.Name).GetRawText(), response.Value.GetRawText());
            var media = actual.GetProperty("requestBody").GetProperty("content").GetProperty("application/json");
            const string queryExample = """{"source":"sales_by_line","query":{"group_by":["country"],"aggregates":[{"fn":"sum","col":"line_total"}],"order_by":[{"col":"sum_line_total","dir":"desc"}],"limit":5}}""";
            Assert.Equal(queryExample, DataGateway.Page.OperationDocument.QueryExample);
            var example = operation == "list" ? "{}" : operation == "describe" ? """{"source":"sales_by_line"}""" : queryExample;
            using var expected = JsonDocument.Parse(example);
            Assert.True(JsonElement.DeepEquals(expected.RootElement, media.GetProperty("example")));
            Assert.Equal(original.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("example").GetRawText(), media.GetProperty("example").GetRawText());
            var body = media.GetProperty("schema");
            Assert.Equal("object", body.GetProperty("type").GetString());
            Assert.False(body.GetProperty("additionalProperties").GetBoolean());
            if (operation == "list")
            {
                Assert.Equal(0, body.GetProperty("maxProperties").GetInt32());
                Assert.Equal(JsonValueKind.Object, media.GetProperty("example").ValueKind);
                Assert.Empty(media.GetProperty("example").EnumerateObject());
            }
            else
                Assert.Equal(operation == "describe" ? new[] { "source" } : new[] { "source", "query" }, body.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
            foreach (var (status, codes) in Table(operation))
            {
                var schema = responses.GetProperty(status).GetProperty("content").GetProperty("application/json").GetProperty("schema");
                Assert.Equal("object", schema.GetProperty("type").GetString());
                Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
                Assert.Equal(new[] { "code", "path", "message", "incidentId" }, schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
                Assert.Equal(new[] { "code", "path", "message", "incidentId" }, schema.GetProperty("properties").EnumerateObject().Select(property => property.Name));
                foreach (var property in schema.GetProperty("properties").EnumerateObject())
                    Assert.Equal("string", property.Value.GetProperty("type").GetString());
                Assert.Equal(codes.Split(','), schema.GetProperty("properties").GetProperty("code").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
                Assert.Equal("^[0-9a-f]{32}$", schema.GetProperty("properties").GetProperty("incidentId").GetProperty("pattern").GetString());
            }
        }
        var query = document.GetProperty("components").GetProperty("schemas").GetProperty("QueryResponse");
        Assert.Contains("hint", query.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("string", query.GetProperty("properties").GetProperty("hint").GetProperty("type").GetString());
        await Witness(pair.Host.Client, document, "north_items", 1);
    }

    /// <summary>The error codes each operation documents, by HTTP status.</summary>
    /// <param name="operation">The operation id.</param>
    /// <returns>Comma-separated codes keyed by status; only describe and query can answer 404.</returns>
    private static Dictionary<string, string> Table(string operation)
    {
        var table = new Dictionary<string, string>
        {
            ["400"] = operation == "query" ? "InvalidJson,InvalidStructure,UnknownColumn,ColumnOperationNotAllowed,InvalidOperator,InvalidAggregateFunction,InvalidSortDirection,InvalidValue,InvalidClauseCombination,InvalidOrderTarget,DuplicateReference,InvalidLimit,StructuralLimitExceeded" : "InvalidJson,InvalidStructure,StructuralLimitExceeded",
            ["500"] = "InternalError,UnexpectedError",
            ["503"] = operation == "query" ? "SourceUnavailable,CatalogUnavailable" : "CatalogUnavailable",
            ["504"] = "QueryTimeout"
        };
        if (operation != "list")
            table.Add("404", "UnknownSource");
        return table;
    }

    /// <summary>Gateway names that differ only by case or whitespace, or that hold route and non-ASCII characters, each
    /// get their own document and page, titled with the exact name; the MCP description stays out of both.</summary>
    [Fact]
    public async Task T04_names_titles_and_prefixes_do_not_collide()
    {
        using var fixture = Fixture();
        var names = new[] { " a ", "a", "A", "a:b /?#", "v1", "\u6771\u4eac" };
        string?[] titles = [null, "  ", "  Display title  ", "Punctuation title", null, null];
        var state = new NamedGatewayState(fixture);
        await using var host = new NamedGatewayHost(builder =>
        {
            for (var index = 0; index < names.Length; index++)
                Register(builder.Services, names[index], state, titles[index], "About must stay out of OpenAPI");
        });
        for (var index = 0; index < names.Length; index++)
        {
            host.App.MapDataGateway(names[index], "/api-" + index);
            host.App.MapDataGatewayApiPage(names[index], "/page-" + index);
        }
        var registrations = host.App.Services.GetServices<GatewayRegistration>().ToArray();
        Assert.Equal(names.Length, registrations.Select(registration => registration.DocumentName).Distinct(StringComparer.Ordinal).Count());
        await host.Start();
        foreach (var index in Enumerable.Range(0, names.Length).Concat(Enumerable.Range(0, names.Length).Reverse()))
        {
            var title = names[index];
            var bytes = await Get(host.Client, "/page-" + index + "/openapi/v1.json");
            Document(bytes, "/api-" + index, title);
            Assert.DoesNotContain("About must stay out", Encoding.UTF8.GetString(bytes));
            var html = Encoding.UTF8.GetString(await Get(host.Client, "/page-" + index + "/scalar/"));
            var pageTitle = WebUtility.HtmlDecode(Regex.Match(html, "<title>(.*?)</title>").Groups[1].Value);
            Assert.Equal(title, pageTitle);
            foreach (var registration in registrations)
                Assert.DoesNotContain(registration.DocumentName, html);
            var configuration = InitializeArguments(html).Configuration;
            Assert.Equal("page-" + index + "/openapi/v1.json", Assert.Single(configuration.GetProperty("sources").EnumerateArray()).GetProperty("url").GetString());
        }
        Assert.Empty(host.Logs.Failures);
    }

    /// <summary>The arguments the Scalar page passes to its initializer script.</summary>
    private sealed record Initializer(string Path, bool Dynamic, JsonElement Configuration, string Module);

    /// <summary>Extracts the initializer arguments from a Scalar page's HTML.</summary>
    private static Initializer InitializeArguments(string html)
    {
        var match = Regex.Match(html, "initialize\\(\\s*'(?<path>[^']*)',\\s*(?<dynamic>true|false),\\s*(?<configuration>\\{.*\\}),\\s*'(?<module>[^']*)'\\)", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(match.Success, html);
        return new(match.Groups["path"].Value, bool.Parse(match.Groups["dynamic"].Value), Json(Encoding.UTF8.GetBytes(match.Groups["configuration"].Value)), match.Groups["module"].Value);
    }

    /// <summary>Runs the packaged Scalar initializer in Node with a stubbed browser window and returns the configuration
    /// it hands to Scalar.</summary>
    /// <remarks>The page gives a relative document URL; only running the real script shows the absolute URL a browser
    /// would fetch, which must point back at the gateway's own document.</remarks>
    /// <param name="page">The page's address, used as the stubbed window location.</param>
    /// <param name="arguments">The initializer arguments read from the page.</param>
    /// <returns>The configuration the initializer produced.</returns>
    private static async Task<JsonElement> ExecuteInitializer(Uri page, Initializer arguments)
    {
        const string script = """
            let input = '';
            for await (const chunk of process.stdin) input += chunk;
            const args = JSON.parse(input);
            let captured;
            globalThis.window = {
              location: new URL(args.url),
              Scalar: { createApiReference: (selector, configuration) => {
                if (selector !== '#app') throw Error(selector);
                captured = configuration;
              } }
            };
            const module = await import('data:text/javascript;base64,' + args.module);
            await module.initialize(args.path, args.dynamic, args.configuration, args.custom);
            if (!captured) throw Error('Initializer did not call Scalar');
            process.stdout.write(JSON.stringify(captured));
            """;
        var start = new ProcessStartInfo("node") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--input-type=module");
        start.ArgumentList.Add("--eval");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { module = Convert.ToBase64String(PageAssets.Packaged("scalar.aspnetcore.js")), url = page.AbsoluteUri, path = arguments.Path, dynamic = arguments.Dynamic, configuration = arguments.Configuration, custom = arguments.Module }));
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, await errors);
            return Json(Encoding.UTF8.GetBytes(await output));
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>Asserts that a Scalar page loads only same-origin packaged assets and resolves to its own document.</summary>
    /// <param name="client">The client to send with.</param>
    /// <param name="path">The page path.</param>
    /// <param name="documentPath">The path of the document the page must load.</param>
    /// <param name="expected">The document bytes the resolved URL must return.</param>
    /// <param name="pathBase">The host's path base, which must not appear twice in the resolved URL.</param>
    private static async Task Page(HttpClient client, string path, string documentPath, byte[] expected, string pathBase = "")
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var page = response.RequestMessage!.RequestUri!;
        var resources = Regex.Matches(html,
            "(?:\\bsrc\\s*=\\s*[\"']|<link\\b[^>]*?\\bhref\\s*=\\s*[\"']|\\bimport\\s+(?:[^;]*?\\s+from\\s+)?[\"'])(?<path>[^\"']+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Select(match => match.Groups["path"].Value).Distinct().ToArray();
        Assert.NotEmpty(resources);
        using var assets = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(15) };
        var assetPrefix = path[..path.IndexOf("/scalar", StringComparison.Ordinal)] + "/scalar/";
        foreach (var resource in resources.Concat(Assets.Select(name => assetPrefix + name)).Distinct())
        {
            Assert.False(resource.StartsWith("//", StringComparison.Ordinal));
            Assert.False(Uri.TryCreate(resource, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https");
            var uri = new Uri(page, resource);
            Assert.Equal(page.Authority, uri.Authority);
            Assert.Equal(PageAssets.Packaged(Path.GetFileName(uri.AbsolutePath)), await Get(assets, uri.AbsoluteUri));
        }
        var arguments = InitializeArguments(html);
        Assert.False(arguments.Dynamic);
        Assert.Empty(arguments.Module);
        var source = Assert.Single(arguments.Configuration.GetProperty("sources").EnumerateArray());
        Assert.Equal("v1", source.GetProperty("title").GetString());
        var configured = source.GetProperty("url").GetString()!;
        Assert.Equal(documentPath[pathBase.Length..].TrimStart('/'), configured);
        Assert.False(configured.StartsWith('/'));
        Assert.DoesNotContain("datagateway-", configured);
        if (pathBase.Length > 0)
            Assert.DoesNotContain(pathBase, configured);
        var normalized = await ExecuteInitializer(page, arguments);
        var url = Assert.Single(normalized.GetProperty("sources").EnumerateArray()).GetProperty("url").GetString()!;
        Assert.Equal(new Uri(client.BaseAddress!, documentPath).AbsoluteUri, url);
        if (pathBase.Length > 0)
            Assert.Equal(1, new Uri(url).AbsolutePath.Split(pathBase, StringSplitOptions.None).Length - 1);
        Assert.Equal(expected, await Get(client, url));
    }

    /// <summary>Pages use only local assets and their own document under every prefix shape: root, trailing slash,
    /// nested and parameterised parent groups, and a host path base.</summary>
    [Fact]
    public async Task T05_scalar_uses_only_local_resources_and_its_own_document()
    {
        await using (var pair = new Pair())
        {
            await pair.Host.Start();
            foreach (var (page, api, title) in new[] { ("/north-docs", "/north", "north"), ("/south-docs", "/south", "north:archive") })
            {
                var expected = await Get(pair.Host.Client, page + "/openapi/v1.json");
                Document(expected, api, title);
                await Page(pair.Host.Client, page + "/scalar/", page + "/openapi/v1.json", expected);
                await Page(pair.Host.Client, page + "/scalar/v1", page + "/openapi/v1.json", expected);
                await Redirect(pair.Host.Client, page + "/scalar");
                Configure(title, page + "/scalar/", null, page[1..] + "/openapi/v1.json");
                Configure(title, page + "/scalar/v1", "v1", page[1..] + "/openapi/v1.json");
            }
        }
        foreach (var (parent, prefix, concrete, pathBase) in new[]
        {
            ("", "", "", ""),
            ("", "/", "", ""),
            ("", "/trailing/", "/trailing", ""),
            ("/outer/nested", "/docs", "/outer/nested/docs", ""),
            ("/tenants/{tenant}", "/docs", "/tenants/alpha/docs", ""),
            ("", "/north-docs", "/north-docs", "/base"),
            ("/outer/nested", "/docs", "/outer/nested/docs", "/base"),
            ("/tenants/{tenant}", "/docs", "/tenants/alpha/docs", "/base")
        })
        {
            using var fixture = Fixture();
            var state = new NamedGatewayState(fixture);
            await using var host = new NamedGatewayHost(builder => Register(builder.Services, "root", state), app =>
            {
                if (pathBase.Length > 0)
                    app.UsePathBase(pathBase);
            });
            host.App.MapDataGateway("root", "/data");
            host.App.MapGroup(parent).MapDataGatewayApiPage("root", prefix);
            await host.Start();
            var documentPath = pathBase + concrete + "/openapi/v1.json";
            var bytes = await Get(host.Client, documentPath);
            Document(bytes, "/data", "root");
            foreach (var suffix in new[] { "/scalar/", "/scalar/v1" })
                await Page(host.Client, pathBase + concrete + suffix, documentPath, bytes, pathBase);
            await Redirect(host.Client, pathBase + concrete + "/scalar");
            Configure("root", concrete + "/scalar/", null, concrete.TrimStart('/') + (concrete.Length > 0 ? "/" : "") + "openapi/v1.json");
        }
    }

    /// <summary>Asserts the Scalar options the library sets for one page: the gateway title, no fonts, telemetry, agent,
    /// proxy, authentication or servers, no absolute bundle URL, and the expected relative document route.</summary>
    /// <param name="title">The gateway name.</param>
    /// <param name="path">The page request path.</param>
    /// <param name="document">The document name in the route, or null when the path has none.</param>
    /// <param name="expected">The expected document route pattern.</param>
    private static void Configure(string title, string path, string? document, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.PathBase = "/base";
        if (document is not null)
            context.Request.RouteValues["documentName"] = document;
        var registration = new GatewayRegistration(title, (_, _) => throw new InvalidOperationException(), new Dictionary<string, DataGateway.Data.Queries.QueryRunner>());
        var options = new ScalarOptions();
        ApiPage.Configure(options, registration, context);
        Assert.Equal(title, options.Title);
        Assert.False(options.DefaultFonts);
        Assert.False(options.Telemetry);
        Assert.True(options.Agent?.Disabled);
        Assert.Null(options.ProxyUrl);
        Assert.Null(options.Authentication);
        Assert.Null(options.BaseServerUrl);
        Assert.True(options.Servers is null || !options.Servers.Any());
        Assert.Equal(expected, options.OpenApiRoutePattern);
        foreach (var property in new[] { "BundleUrl", "CdnUrl" })
        {
            var url = (string?)typeof(ScalarOptions).GetProperty(property)!.GetValue(options);
            Assert.True(url is null || !Uri.TryCreate(url, UriKind.Absolute, out _));
        }
    }

    /// <summary>Asserts that a page path without its trailing slash redirects to the path with it.</summary>
    private static async Task Redirect(HttpClient client, string path)
    {
        using var redirects = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = client.BaseAddress, Timeout = TimeSpan.FromSeconds(15) };
        using var response = await redirects.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(new Uri(client.BaseAddress!, path + "/"), new Uri(new Uri(client.BaseAddress!, path), response.Headers.Location!));
    }

    /// <summary>Policy set on the returned group or on a parent group covers the document, both page forms, the redirect
    /// and every asset; the page cannot serve another gateway's document.</summary>
    [Fact]
    public async Task T06_returned_group_and_parent_policy_cover_all_page_endpoints()
    {
        foreach (var parentPolicy in new[] { false, true })
        {
            using var fixture = Fixture();
            var a = new NamedGatewayState(fixture);
            var b = new NamedGatewayState(fixture);
            await using var host = new NamedGatewayHost(builder =>
            {
                Register(builder.Services, "private", a);
                Register(builder.Services, "public", b);
            });
            host.App.MapDataGateway("private", "/private-data");
            host.App.MapDataGateway("public", "/public-data");
            if (parentPolicy)
                host.App.MapGroup("/parent").RequireAuthorization().MapDataGatewayApiPage("private", "/private");
            else
                host.App.MapDataGatewayApiPage("private", "/parent/private").RequireAuthorization();
            host.App.MapDataGatewayApiPage("public", "/public");
            await host.Start();
            var before = (a.Counts, b.Counts, a.Instances.Count, b.Instances.Count);
            using var unsigned = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = host.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(15) };
            using var signed = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) { BaseAddress = host.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(15) };
            signed.DefaultRequestHeaders.Add("X-Synthetic-User", "reader");
            var expected = await Get(signed, "/parent/private/openapi/v1.json");
            var expectedSlashPage = await Get(signed, "/parent/private/scalar/");
            var expectedNamedPage = await Get(signed, "/parent/private/scalar/v1");
            Document(expected, "/private-data", "private");
            foreach (var suffix in new[] { "/openapi/v1.json", "/scalar/", "/scalar/v1", "/scalar" }.Concat(Assets.Select(asset => "/scalar/" + asset)))
            {
                using var refused = await unsigned.GetAsync("/parent/private" + suffix);
                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
                using var allowed = await signed.GetAsync("/parent/private" + suffix);
                Assert.Equal(suffix == "/scalar" ? HttpStatusCode.Redirect : HttpStatusCode.OK, allowed.StatusCode);
                var bytes = await allowed.Content.ReadAsByteArrayAsync();
                if (suffix == "/openapi/v1.json")
                    Assert.Equal(expected, bytes);
                else if (Assets.Contains(Path.GetFileName(suffix)))
                    Assert.Equal(PageAssets.Packaged(Path.GetFileName(suffix)), bytes);
                else if (suffix != "/scalar")
                {
                    Assert.Equal(suffix == "/scalar/" ? expectedSlashPage : expectedNamedPage, bytes);
                    Assert.Contains("<title>private</title>", Encoding.UTF8.GetString(bytes));
                }
            }
            foreach (var endpoint in host.Endpoints.Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/parent/private", StringComparison.Ordinal)))
            {
                Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
                Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            }
            Document(await Get(host.Client, "/public/openapi/v1.json"), "/public-data", "public");
            await Get(host.Client, "/public/scalar/");
            foreach (var registration in host.App.Services.GetServices<GatewayRegistration>())
            {
                using var foreign = await signed.GetAsync("/parent/private/openapi/" + registration.DocumentName + ".json");
                Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            }
            using var other = await signed.GetAsync("/parent/private/openapi/other.json");
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
            Assert.Equal(before, (a.Counts, b.Counts, a.Instances.Count, b.Instances.Count));
            Assert.Empty(host.Logs.Failures);
        }
    }

    /// <summary>Asserts that an action throws the library's refusal message for a gateway.</summary>
    /// <param name="action">The action that must throw.</param>
    /// <param name="name">The gateway name the message must name.</param>
    /// <param name="reason">The expected reason.</param>
    private static void Refusal(Action action, string name, string reason) =>
        Assert.Equal($"Gateway '{name}'. {reason}", Assert.Throws<InvalidOperationException>(action).Message);

    /// <summary>Mapping a page refuses a null or blank name, an unregistered name, a wrong-case name, an invalid prefix
    /// and a second mapping, each with its exact message, and a refused mapping adds no endpoint.</summary>
    [Fact]
    public async Task T07_page_mapping_refusals_are_exact_and_do_not_claim_invalid_maps()
    {
        Assert.Equal("endpoints", Assert.Throws<ArgumentNullException>(() => DataGatewayExtensions.MapDataGatewayApiPage(null!, "a", "/docs")).ParamName);
        using var fixture = Fixture();
        var state = new NamedGatewayState(fixture);
        await using (var missing = new NamedGatewayHost())
        {
            foreach (var name in new string?[] { null, "", " ", "\t" })
                Assert.Equal("Gateway name must contain a non-whitespace character.", Assert.Throws<InvalidOperationException>(() => missing.App.MapDataGatewayApiPage(name!, "/docs")).Message);
            Refusal(() => missing.App.MapDataGatewayApiPage("a", null!), "a", "Register this gateway with AddDataGateway before mapping it.");
        }
        foreach (var prefix in new string?[] { null, "/broken/{" })
        {
            await using var host = new NamedGatewayHost(builder => Register(builder.Services, "a", state));
            Refusal(() => host.App.MapDataGatewayApiPage("A", "/docs"), "A", "Register this gateway with AddDataGateway before mapping it.");
            Refusal(() => host.App.MapDataGatewayApiPage("unknown", "/docs"), "unknown", "Register this gateway with AddDataGateway before mapping it.");
            Refusal(() => host.App.MapDataGatewayApiPage("a", prefix!), "a", "Prefix must be a valid route pattern.");
            Assert.Empty(host.Endpoints);
            host.App.MapDataGatewayApiPage("a", "/docs");
            Refusal(() => host.App.MapDataGatewayApiPage("a", "/else"), "a", "This gateway's API page has already been mapped.");
            Refusal(() => host.App.MapGroup("/parent").MapDataGatewayApiPage("a", "/other"), "a", "This gateway's API page has already been mapped.");
            host.App.MapDataGateway("a", "/api");
            Refusal(() => host.App.MapDataGateway("a", "/second-api"), "a", "This gateway has already been mapped.");
            await host.Start();
            Document(await Get(host.Client, "/docs/openapi/v1.json"), "/api", "a");
            using var absent = await host.Client.GetAsync("/else/openapi/v1.json");
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        }
        foreach (var prefix in new[] { "", "/" })
        {
            await using var host = new NamedGatewayHost(builder => Register(builder.Services, "a", state));
            host.App.MapDataGateway("a", "/api");
            host.App.MapDataGatewayApiPage("a", prefix);
            await host.Start();
            Document(await Get(host.Client, "/openapi/v1.json"), "/api", "a");
        }
        Assert.Equal("The API document has no entry for operation 'extra'.", Assert.Throws<InvalidOperationException>(() => DataGateway.Page.OperationDocument.Of("extra")).Message);
    }

    /// <summary>The example host with two extra named gateways, each with its own API and page prefix.</summary>
    private static LoggedHost Mixed(NamedGatewayState north, NamedGatewayState south)
    {
        var host = new LoggedHost(environment: "Development", configure: builder =>
        {
            Register(builder.Services, "north", north, "north");
            Register(builder.Services, "south", south, "south");
        });
        host.App.MapDataGateway("north", "/north");
        host.App.MapDataGateway("south", "/south");
        host.App.MapDataGatewayApiPage("north", "/north-docs");
        host.App.MapDataGatewayApiPage("south", "/south-docs");
        return host;
    }

    /// <summary>Asserts that the example host's own document and page match their golden bytes, including the sign-in
    /// response the host adds to every operation.</summary>
    private static async Task ExampleBytes(LoggedHost host)
    {
        var document = await Get(host.Client, "/openapi/v1.json", "application/json; charset=utf-8");
        var html = await Get(host.Client, "/scalar/", "text/html");
        Assert.Equal(WebApiPageGolden.Document(), document);
        Assert.Equal(WebApiPageGolden.Html(), html);
        Assert.Equal(37826, document.Length);
        Assert.Equal(686, html.Length);
        var value = Json(document);
        Names(["/sources/list", "/sources/describe", "/query"], value.GetProperty("paths").EnumerateObject().Select(path => path.Name));
        Names(Operations, value.GetProperty("paths").EnumerateObject().Select(path => path.Value.GetProperty("post").GetProperty("operationId").GetString()!));
        Assert.Equal("A read-only data gateway. Every call needs Windows sign-in.", value.GetProperty("info").GetProperty("description").GetString());
        foreach (var operation in Operations)
        {
            var response = Operation(value, operation).GetProperty("responses").GetProperty("401");
            Assert.Equal("Unauthorized. The code is one of: NotAuthenticated. The first leg of sign-in answers with WWW-Authenticate: Negotiate and no incidentId; a refused handshake adds an incidentId.", response.GetProperty("description").GetString());
            var schema = response.GetProperty("content").GetProperty("application/json").GetProperty("schema");
            Assert.Equal(new[] { "code", "path", "message" }, schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal("NotAuthenticated", Assert.Single(schema.GetProperty("properties").GetProperty("code").GetProperty("enum").EnumerateArray()).GetString());
        }
    }

    /// <summary>Adding named gateways beside the example host leaves its document and page byte for byte unchanged, and
    /// their documents are not reachable through the host's document route.</summary>
    [Fact]
    public async Task T08_example_document_and_page_match_prechange_bytes_with_named_neighbors()
    {
        using var settings = new ErrorLogSettingsScope();
        await using (var host = new LoggedHost(environment: "Development"))
        {
            await host.Start();
            await ExampleBytes(host);
            Assert.Empty(host.Lines());
        }
        using var north = Fixture("north_items");
        using var south = Fixture("south_items", 100);
        await using (var host = Mixed(new(north), new(south)))
        {
            await host.Start();
            await ExampleBytes(host);
            Document(await Get(host.Client, "/north-docs/openapi/v1.json"), "/north", "north");
            Document(await Get(host.Client, "/south-docs/openapi/v1.json"), "/south", "south");
            foreach (var registration in host.App.Services.GetServices<GatewayRegistration>())
            {
                using var hidden = await host.Client.GetAsync("/openapi/" + registration.DocumentName + ".json");
                Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            }
            Assert.Empty(host.Lines());
        }
        Assert.Null(typeof(Program).Assembly.GetType("DataGateway.Web.Page.HostOperationDocument"));
        Assert.Equal("DataGateway.Web.Tests", typeof(HostPolicyDocument).Assembly.GetName().Name);
        Assert.Equal("DataGateway.Web.Page", typeof(HostPolicyDocument).Namespace);
        Assert.Equal("DataGateway", typeof(DataGateway.Page.OperationDocument).Assembly.GetName().Name);
        Assert.Equal("DataGateway.Page", typeof(DataGateway.Page.OperationDocument).Namespace);
    }

    /// <summary>A named host endpoint stays out of the example host's document, and the named gateway documents keep
    /// working beside it.</summary>
    [Fact]
    public async Task T09_unknown_example_endpoint_still_fails_while_named_documents_work()
    {
        using var settings = new ErrorLogSettingsScope();
        using var north = Fixture("north_items");
        using var south = Fixture("south_items", 100);
        await using var host = Mixed(new(north), new(south));
        host.App.MapGet("/extra", () => "x").WithName("extra");
        await host.Start();
        Document(await Get(host.Client, "/north-docs/openapi/v1.json"), "/north", "north");
        Document(await Get(host.Client, "/south-docs/openapi/v1.json"), "/south", "south");
        using var response = await host.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = Json(await response.Content.ReadAsByteArrayAsync());
        Names(["/sources/list", "/sources/describe", "/query"], document.GetProperty("paths").EnumerateObject().Select(path => path.Name));
        Assert.Equal("x", await host.Client.GetStringAsync("/extra"));
        Assert.Empty(host.Lines());
    }

    /// <summary>Registering gateways maps no endpoint and reads no catalog; mapping the JSON routes alone serves no page;
    /// and a host's own OpenAPI document lists gateway routes without being replaced by the gateway documents.</summary>
    /// <remarks>A failed or duplicate registration leaves the service collection unchanged.</remarks>
    [Fact]
    public async Task T10_service_registration_maps_nothing_and_keeps_host_openapi_independent()
    {
        using var fixture = Fixture();
        var state = new NamedGatewayState(fixture);
        Assert.Equal(Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1, new OpenApiOptions().OpenApiVersion);
        var services = new ServiceCollection();
        Register(services, "a", state);
        Register(services, "b", state);
        var documents = services.Where(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(IOpenApiDocumentProvider)).ToArray();
        Assert.Equal(2, documents.Length);
        Assert.Equal(2, documents.Select(descriptor => descriptor.ServiceKey).Distinct().Count());
        var before = services.ToArray();
        Assert.Throws<InvalidOperationException>(() => services.AddDataGateway("bad", _ => throw new InvalidOperationException("configuration refused")));
        Assert.Throws<InvalidOperationException>(() => services.AddDataGateway("incomplete", gateway => gateway.UseCatalog<SqliteConnection>(_ => new NamedGatewayCatalog(state, Guid.Empty))));
        Refusal(() => Register(services, "a", state), "a", "A gateway with this name is already registered.");
        Assert.Equal(before, services.ToArray());
        Assert.Equal((0, 0, 0), state.Counts);
        Assert.Empty(state.Instances);
        await using (var host = new NamedGatewayHost(builder =>
        {
            Register(builder.Services, "a", state);
            Register(builder.Services, "b", state);
        }))
        {
            Assert.Empty(host.Endpoints);
            var registrations = host.App.Services.GetServices<GatewayRegistration>().ToArray();
            var first = host.App.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>(registrations[0].DocumentName);
            var second = host.App.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>(registrations[1].DocumentName);
            Assert.NotSame(first, second);
            using var deadline = Deadline();
            var empty = await first.GetOpenApiDocumentAsync(deadline.Token);
            Assert.True(empty.Paths is null || empty.Paths.Count == 0);
            Assert.Empty(host.Endpoints);
            Assert.Equal((0, 0, 0), state.Counts);
            Assert.Empty(state.Instances);
        }
        await using (var host = new NamedGatewayHost(builder => Register(builder.Services, "a", state)))
        {
            host.App.MapDataGateway("a", "/data");
            await host.Start();
            foreach (var operation in Operations)
                Assert.Equal(200, (await host.Post("/data", operation)).Status);
            foreach (var path in new[] { "/openapi/v1.json", "/scalar/", "/scalar/scalar.js", "/docs/openapi/v1.json", "/docs/scalar/" })
            {
                using var missing = await host.Client.GetAsync(path);
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
        }
        await using (var host = new NamedGatewayHost(builder =>
        {
            Register(builder.Services, "a", state);
            Register(builder.Services, "b", state);
            Func<IServiceCollection, string, Action<OpenApiOptions>, IServiceCollection> add = OpenApiServiceCollectionExtensions.AddOpenApi;
            add(builder.Services, "v1", _ => { });
        }))
        {
            host.App.MapDataGatewayApiPage("a", "/a-docs");
            host.App.MapDataGateway("a", "/a");
            host.App.MapDataGateway("b", "/b");
            host.App.MapOpenApi();
            await host.Start();
            var counts = (state.Counts, state.Instances.Count);
            for (var iteration = 0; iteration < 3; iteration++)
            {
                Document(await Get(host.Client, "/a-docs/openapi/v1.json"), "/a", "a");
                await Get(host.Client, "/a-docs/scalar/");
            }
            var ordinary = Json(await Get(host.Client, "/openapi/v1.json"));
            Names(new[] { "/a", "/b" }.SelectMany(prefix => Operations.Select(operation => prefix + NamedGatewayHost.PathOf(operation))), ordinary.GetProperty("paths").EnumerateObject().Select(path => path.Name));
            Assert.Equal(counts, (state.Counts, state.Instances.Count));
            Assert.Empty(host.Logs.Failures);
        }
    }

    /// <summary>Two gateways whose page routes collide, by case, by route parameter or through the same parent group,
    /// fail when endpoints are built, before the host serves anything.</summary>
    [Fact]
    public async Task T11_duplicate_effective_page_routes_fail_before_serving()
    {
        using var fixture = Fixture();
        var state = new NamedGatewayState(fixture);
        foreach (var mode in new[] { "case", "parameter", "parents" })
        {
            await using var host = new NamedGatewayHost(builder =>
            {
                Register(builder.Services, "a", state);
                Register(builder.Services, "b", state);
            });
            string left;
            string right;
            if (mode == "parents")
            {
                host.App.MapGroup("/outer").MapDataGatewayApiPage("a", "/docs");
                host.App.MapGroup("/outer").MapDataGatewayApiPage("b", "/docs");
                left = "/outer/docs/openapi/v1.json";
                right = left;
            }
            else if (mode == "parameter")
            {
                host.App.MapDataGatewayApiPage("a", "/tenants/{tenant}/docs");
                host.App.MapDataGatewayApiPage("b", "/tenants/{tenant}/docs");
                left = "/tenants/{tenant}/docs/openapi/v1.json";
                right = left;
            }
            else
            {
                host.App.MapDataGatewayApiPage("a", "/shared");
                host.App.MapDataGatewayApiPage("b", "/SHARED/");
                left = "/shared/openapi/v1.json";
                right = "/SHARED/openapi/v1.json";
            }
            var error = Assert.Throws<InvalidOperationException>(() => _ = host.Endpoints);
            Assert.Contains(error.Message, new[]
            {
                $"Gateway 'b'. API page route '{right}' is already mapped by gateway 'a'.",
                $"Gateway 'a'. API page route '{left}' is already mapped by gateway 'b'."
            });
            Assert.Empty(host.App.Urls);
            Assert.Empty(state.Instances);
        }
        foreach (var separateParents in new[] { false, true })
        {
            await using var host = new NamedGatewayHost(builder =>
            {
                Register(builder.Services, "a", state);
                Register(builder.Services, "b", state);
            });
            host.App.MapDataGateway("a", "/a");
            host.App.MapDataGateway("b", "/b");
            var parent = host.App.MapGroup("/outer");
            if (separateParents)
            {
                host.App.MapGroup("/outer-a").MapDataGatewayApiPage("a", "/docs");
                host.App.MapGroup("/outer-b").MapDataGatewayApiPage("b", "/docs");
            }
            else
            {
                parent.MapDataGatewayApiPage("a", "/a-docs");
                parent.MapDataGatewayApiPage("b", "/b-docs");
            }
            Assert.Equal(host.Endpoints.Length, host.Endpoints.Length);
            await host.Start();
            for (var iteration = 0; iteration < 3; iteration++)
            {
                Document(await Get(host.Client, separateParents ? "/outer-a/docs/openapi/v1.json" : "/outer/a-docs/openapi/v1.json"), "/a", "a");
                Document(await Get(host.Client, separateParents ? "/outer-b/docs/openapi/v1.json" : "/outer/b-docs/openapi/v1.json"), "/b", "b");
            }
        }
    }

    /// <summary>Concurrent document requests return the same bytes and read no catalog, both for gateways in one host and
    /// for same-named gateways in two hosts.</summary>
    [Fact]
    public async Task T12_concurrent_documents_do_not_share_gateway_or_host_policy()
    {
        using var settings = new ErrorLogSettingsScope();
        using var north = Fixture("north_items");
        using var south = Fixture("south_items", 100);
        var a = new NamedGatewayState(north);
        var b = new NamedGatewayState(south);
        await using (var host = Mixed(a, b))
        {
            await host.Start();
            var paths = new[] { "/openapi/v1.json", "/north-docs/openapi/v1.json", "/south-docs/openapi/v1.json" };
            var expected = new[] { await Get(host.Client, paths[0]), await Get(host.Client, paths[1]), await Get(host.Client, paths[2]) };
            Assert.Equal(WebApiPageGolden.Document(), expected[0]);
            var first = Document(expected[1], "/north", "north");
            var second = Document(expected[2], "/south", "south");
            References(Json(expected[0]), Json(expected[0]));
            await Witness(host.Client, first, "north_items", 1);
            await Witness(host.Client, second, "south_items", 101);
            var counts = (a.Counts, b.Counts, a.Instances.Count, b.Instances.Count);
            await Task.WhenAll(Enumerable.Range(0, 24).Select(async index =>
            {
                var selected = index % paths.Length;
                Assert.Equal(expected[selected], await Get(host.Client, paths[selected]));
            }));
            Assert.Equal(counts, (a.Counts, b.Counts, a.Instances.Count, b.Instances.Count));
            Assert.Empty(host.Lines());
        }
        var firstState = new NamedGatewayState(north);
        var secondState = new NamedGatewayState(south);
        await using var firstHost = new NamedGatewayHost(builder => Register(builder.Services, "same", firstState, "First app"));
        await using var secondHost = new NamedGatewayHost(builder => Register(builder.Services, "same", secondState, "Second app"));
        firstHost.App.MapDataGateway("same", "/first");
        firstHost.App.MapDataGatewayApiPage("same", "/first-docs");
        secondHost.App.MapDataGatewayApiPage("same", "/second-docs");
        secondHost.App.MapDataGateway("same", "/second");
        await firstHost.Start();
        await secondHost.Start();
        var firstBytes = await Get(firstHost.Client, "/first-docs/openapi/v1.json");
        var secondBytes = await Get(secondHost.Client, "/second-docs/openapi/v1.json");
        await Witness(firstHost.Client, Document(firstBytes, "/first", "same"), "north_items", 1);
        await Witness(secondHost.Client, Document(secondBytes, "/second", "same"), "south_items", 101);
        var before = (firstState.Counts, secondState.Counts, firstState.Instances.Count, secondState.Instances.Count);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            Assert.Equal(firstBytes, await Get(firstHost.Client, "/first-docs/openapi/v1.json"));
            Assert.Equal(secondBytes, await Get(secondHost.Client, "/second-docs/openapi/v1.json"));
        }));
        Assert.Equal(before, (firstState.Counts, secondState.Counts, firstState.Instances.Count, secondState.Instances.Count));
        Assert.Empty(firstHost.Logs.Failures);
        Assert.Empty(secondHost.Logs.Failures);
    }
}
