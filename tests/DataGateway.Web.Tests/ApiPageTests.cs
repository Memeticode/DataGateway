using DataGateway.Data.SqlServer;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DataGateway.Page;
using DataGateway.Web.Page;
using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using DataGateway.Core.Queries;
using DataGateway.Data.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Scalar.AspNetCore;
using SqlKata.JsonQuery;
using Xunit;
using Xunit.Abstractions;

namespace DataGateway.Web.Tests;

/// <summary>Pins the web host's API page: the OpenAPI document and the Scalar page that renders it, when they are
/// mapped, that they need sign-in, and that the document describes exactly what the wire carries.</summary>
/// <remarks>
/// <para>The page and document are mapped only in the Development environment and never read the catalog, so
/// opening them cannot fail on, or reveal anything about, the catalog.</para>
/// <para>The page loads only assets served by the host itself; no script, font or telemetry comes from outside.</para>
/// </remarks>
/// <param name="output">Records the page's resources and redirects for diagnosis.</param>
public sealed class ApiPageTests(ITestOutputHelper output)
{
    /// <summary>The exact body of the first-leg sign-in challenge, which carries no incident id.</summary>
    private const string Plain401 = """{"code":"NotAuthenticated","path":"$","message":"Sign in with Windows to call DataGateway: send the request with Windows credentials (HTTP Negotiate)."}""";
    /// <summary>The page's packaged assets, fetched even when the page does not reference them.</summary>
    private static readonly string[] Assets = ["scalar.js", "scalar.aspnetcore.js", "favicon.svg"];
    /// <summary>The query library's refusal codes, stated independently of the library.</summary>
    private static readonly string[] LibraryCodes = ["InvalidJson", "InvalidStructure", "UnknownColumn", "ColumnOperationNotAllowed", "InvalidOperator", "InvalidAggregateFunction", "InvalidSortDirection", "InvalidValue", "InvalidClauseCombination", "InvalidOrderTarget", "DuplicateReference", "InvalidLimit", "StructuralLimitExceeded"];
    /// <summary>Every code the document may name across all operations.</summary>
    private static readonly string[] ExpectedCodes = ["InvalidJson", "InvalidStructure", "UnknownColumn", "ColumnOperationNotAllowed", "InvalidOperator", "InvalidAggregateFunction", "InvalidSortDirection", "InvalidValue", "InvalidClauseCombination", "InvalidOrderTarget", "DuplicateReference", "InvalidLimit", "StructuralLimitExceeded", "UnknownSource", "QueryTimeout", "SourceUnavailable", "CatalogUnavailable", "NotAuthenticated", "InternalError", "UnexpectedError"];
    /// <summary>The request example each operation's document must carry.</summary>
    private static readonly Dictionary<string, string> Examples = new()
    {
        ["list"] = "{}",
        ["describe"] = """{"source":"sales_by_line"}""",
        ["query"] = """{"source":"sales_by_line","query":{"group_by":["country"],"aggregates":[{"fn":"sum","col":"line_total"}],"order_by":[{"col":"sum_line_total","dir":"desc"}],"limit":5}}"""
    };

    /// <summary>A catalog that counts and refuses every read, to prove the page and document never read the catalog.</summary>
    private sealed class Reads
    {
        /// <summary>How many catalog reads were attempted.</summary>
        internal int Count;
        /// <summary>A host catalog whose list and detail both go through <see cref="Read"/>.</summary>
        internal HostCatalogState Catalog => new() { List = _ => Read(), Detail = (_, _) => { Read(); return Task.FromResult<CatalogDetail?>(null); } };
        /// <summary>Counts the read and throws.</summary>
        private Task<IReadOnlyList<SourceListRow>> Read()
        {
            Interlocked.Increment(ref Count);
            throw new InvalidOperationException("Page requests must not read the catalog.");
        }
    }

    /// <summary>Every route endpoint the host mapped.</summary>
    private static RouteEndpoint[] Endpoints(WebApplication app) => ((IEndpointRouteBuilder)app).DataSources
        .SelectMany(source => source.Endpoints).Cast<RouteEndpoint>().ToArray();

    /// <summary>A concrete request path for every GET endpoint, with optional document names both left out and filled in.</summary>
    private static string[] GetPaths(WebApplication app) => Endpoints(app)
        .Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("GET"))
        .SelectMany(endpoint => endpoint.RoutePattern.Parameters.Any(p => p.IsOptional)
            ? new[] { endpoint.RoutePattern.RawText!.Replace("{documentName?}", ""), endpoint.RoutePattern.RawText!.Replace("{documentName?}", "v1") }
            : new[] { endpoint.RoutePattern.RawText!.Replace("{documentName}", "v1") }).Distinct().ToArray();

    /// <summary>Gets <paramref name="path"/>, asserts a leak-free JSON 200, and returns the body.</summary>
    private static async Task<string> JsonAnswer(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        LoggedHost.Scan.Response("document", response, text);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return text;
    }

    /// <summary>Asserts an anonymous request gets the Negotiate challenge with the exact JSON 401 body.</summary>
    private static async Task PlainChallenge(HttpClient client, string path, bool post = false)
    {
        using var response = post ? await client.PostAsync(path, new StringContent("{}")) : await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        LoggedHost.Scan.Response("anonymous response", response, text);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Negotiate", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(Plain401, text);
    }

    /// <summary>The script, stylesheet and module paths a page's HTML references.</summary>
    private static string[] Resources(string html) => Regex.Matches(html,
        "(?:\\bsrc\\s*=\\s*[\"']|<link\\b[^>]*?\\bhref\\s*=\\s*[\"']|\\bimport\\s+(?:[^;]*?\\s+from\\s+)?[\"'])(?<path>[^\"']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Select(match => match.Groups["path"].Value).Distinct().ToArray();

    /// <summary>Asserts the page renders, every resource it references and every packaged asset is served locally, and
    /// <c>/scalar</c> redirects to <c>scalar/</c>.</summary>
    private async Task Page(LoggedHost host)
    {
        using var response = await host.Client.GetAsync("/scalar/");
        var html = await response.Content.ReadAsStringAsync();
        LoggedHost.Scan.TextResponse("page", response, html);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("DataGateway", html);
        var resources = Resources(html);
        Assert.NotEmpty(resources);
        output.WriteLine("Page resources: " + string.Join(", ", resources));
        using var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { BaseAddress = host.Client.BaseAddress };
        foreach (var resource in resources.Concat(Assets.Select(name => "/scalar/" + name)).Distinct())
        {
            Assert.False(resource.StartsWith("//", StringComparison.Ordinal));
            Assert.False(Uri.TryCreate(resource, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https");
            Assert.DoesNotContain(":", resource);
            var uri = new Uri(new Uri(host.Client.BaseAddress!, "/scalar/"), resource);
            using var asset = await client.GetAsync(uri);
            var bytes = await asset.Content.ReadAsByteArrayAsync();
            LoggedHost.Scan.Asset("page asset", asset, bytes, PageAssets.Packaged(Path.GetFileName(uri.AbsolutePath)));
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        }
        using var redirects = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = host.Client.BaseAddress };
        using var redirect = await redirects.GetAsync("/scalar");
        LoggedHost.Scan.TextResponse("redirect", redirect, await redirect.Content.ReadAsStringAsync());
        output.WriteLine("/scalar status: " + (int)redirect.StatusCode + "; location: " + redirect.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("scalar/", redirect.Headers.Location?.OriginalString);
    }

    /// <summary>Only the Development environment, matched without regard to case, maps the document and page; elsewhere
    /// their paths are not found. No environment reads the catalog to do it.</summary>
    [Theory]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("Dev", false)]
    [InlineData("Development", true)]
    [InlineData("development", true)]
    public async Task T2_environment_rule_never_reads_catalog(string environment, bool enabled)
    {
        using var settings = new ErrorLogSettingsScope();
        var reads = new Reads();
        await using var host = new LoggedHost(reads.Catalog, environment: environment);
        await host.Start();
        var endpoints = Endpoints(host.App);
        Assert.Equal(4, endpoints.Count(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("POST")));
        Assert.Single(endpoints, e => e.RoutePattern.RawText!.TrimEnd('/') == "/mcp");
        if (enabled)
        {
            Assert.True(endpoints.Length > 4);
            foreach (var endpoint in endpoints.Where(e => !e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("POST")))
            {
                Assert.Equal(new[] { "GET" }, endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
                Assert.True(endpoint.RoutePattern.RawText!.StartsWith("/openapi/", StringComparison.Ordinal) || endpoint.RoutePattern.RawText.StartsWith("/scalar", StringComparison.Ordinal));
            }
            await JsonAnswer(host.Client, "/openapi/v1.json");
            await Page(host);
        }
        else
        {
            Assert.Equal(4, endpoints.Length);
            foreach (var path in new[] { "/openapi/v1.json", "/scalar/", "/scalar/scalar.js" })
            {
                using var response = await host.Client.GetAsync(path);
                var text = await response.Content.ReadAsStringAsync();
                LoggedHost.Scan.TextResponse("unmapped", response, text);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Empty(text);
            }
        }
        Assert.Empty(host.Lines());
        Assert.Equal(0, reads.Count);
    }

    /// <summary>No endpoint allows anonymous access: the document, the page, its assets and the operations all answer an
    /// anonymous caller with the sign-in challenge.</summary>
    [Fact]
    public async Task T3_every_page_endpoint_needs_sign_in_in_both_environments()
    {
        using var settings = new ErrorLogSettingsScope();
        var reads = new Reads();
        string[] paths;
        await using (var host = new LoggedHost(reads.Catalog, synthetic: false, environment: "Development"))
        {
            await host.Start();
            Assert.All(Endpoints(host.App), endpoint => Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>()));
            paths = GetPaths(host.App);
            Assert.Contains("/scalar/scalar.js", paths);
            foreach (var path in paths) await PlainChallenge(host.Client, path);
            foreach (var path in new[] { "/sources/list", "/sources/describe", "/query" }) await PlainChallenge(host.Client, path, true);
            Assert.Empty(host.Lines());
        }
        await using (var host = new LoggedHost(reads.Catalog, synthetic: false))
        {
            await host.Start();
            foreach (var path in paths) await PlainChallenge(host.Client, path);
            Assert.Empty(host.Lines());
        }
        Assert.Equal(0, reads.Count);
    }

    /// <summary>The document's request and response schemas, examples and references match the records the wire
    /// actually serializes, and it names no server or security scheme.</summary>
    [Fact]
    public async Task T4_document_shapes_examples_and_every_reference_match_core_wire_records()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(environment: "Development");
        await host.Start();
        using var json = JsonDocument.Parse(await JsonAnswer(host.Client, "/openapi/v1.json"));
        var document = json.RootElement;
        Assert.StartsWith("3.1.", document.GetProperty("openapi").GetString());
        Assert.Equal("DataGateway.Web", document.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("v1", document.GetProperty("info").GetProperty("version").GetString());
        Assert.True(!document.TryGetProperty("servers", out var servers) || servers.GetArrayLength() == 0);
        Walk(document, element =>
        {
            if (element.ValueKind != JsonValueKind.Object) return;
            Assert.False(element.TryGetProperty("security", out _));
            Assert.False(element.TryGetProperty("securitySchemes", out _));
            if (element.TryGetProperty("$ref", out _)) Resolve(document, element);
        });
        EqualNames(["/sources/list", "/sources/describe", "/query"], document.GetProperty("paths").EnumerateObject().Select(p => p.Name));
        foreach (var (name, example) in Examples)
        {
            var path = document.GetProperty("paths").GetProperty(Route(name));
            Assert.Equal("post", Assert.Single(path.EnumerateObject()).Name);
            var operation = path.GetProperty("post");
            Assert.Equal(name, operation.GetProperty("operationId").GetString());
            var request = operation.GetProperty("requestBody");
            Assert.True(request.GetProperty("required").GetBoolean());
            Assert.Equal("application/json", Assert.Single(request.GetProperty("content").EnumerateObject()).Name);
            var media = request.GetProperty("content").GetProperty("application/json");
            using var expected = JsonDocument.Parse(example);
            Assert.True(JsonElement.DeepEquals(expected.RootElement, media.GetProperty("example")));
            var schema = media.GetProperty("schema");
            var pinned = name switch
            {
                "list" => """{"type":"object","additionalProperties":false,"maxProperties":0}""",
                "describe" => """{"type":"object","additionalProperties":false,"required":["source"],"properties":{"source":{"type":"string","description":"A name from list sources."}}}""",
                _ => """{"type":"object","additionalProperties":false,"required":["source","query"],"properties":{"source":{"type":"string","description":"A name from list sources."},"query":{"type":"object","description":"Use SqlKata.JsonQuery: select, filters with nested and/or, group_by, aggregates count/sum/avg/min/max, order_by and limit. Describe source returns that source's querySchema."}}}"""
            };
            var actualBody = JsonNode.Parse(Resolve(document, schema).GetRawText())!.AsObject();
            var expectedBody = JsonNode.Parse(pinned)!.AsObject();
            foreach (var body in new[] { actualBody, expectedBody })
                if (body["required"] is JsonArray required)
                    body["required"] = new JsonArray(required.Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            Assert.True(JsonNode.DeepEquals(expectedBody, actualBody), "Request schema differs from its independent literal: " + name + " " + actualBody.ToJsonString());
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            var properties = schema.TryGetProperty("properties", out var props) ? props.EnumerateObject().Select(p => p.Name).ToArray() : [];
            EqualNames(name == "list" ? [] : name == "describe" ? ["source"] : ["source", "query"], properties);
            if (name == "list") Assert.Equal(0, schema.GetProperty("maxProperties").GetInt32());
            else
            {
                EqualNames(properties, schema.GetProperty("required").EnumerateArray().Select(v => v.GetString()!));
                Assert.Equal("string", props.GetProperty("source").GetProperty("type").GetString());
                if (name == "query")
                {
                    Assert.Equal("object", props.GetProperty("query").GetProperty("type").GetString());
                    Assert.False(props.GetProperty("query").TryGetProperty("properties", out _));
                    Assert.Contains("querySchema", props.GetProperty("query").GetProperty("description").GetString());
                }
            }
            Assert.Null(Parse(name, expected.RootElement));
            var extra = JsonNode.Parse(example)!.AsObject();
            extra["extra"] = true;
            Assert.Equal("InvalidStructure", Parse(name, JsonSerializer.SerializeToElement(extra))?.Code);
            if (name != "list")
                foreach (var required in properties)
                {
                    var missing = JsonNode.Parse(example)!.AsObject();
                    missing.Remove(required);
                    Assert.Equal("InvalidStructure", Parse(name, JsonSerializer.SerializeToElement(missing))?.Code);
                }
            else Assert.Equal("InvalidStructure", Parse(name, JsonSerializer.SerializeToElement(new { source = "x" }))?.Code);
        }
        var contract = ErrorLogTests.Contract();
        var samples = new Dictionary<string, object>
        {
            ["list"] = new SourceListResponse([contract.Source]),
            ["describe"] = SourceDescription.Map(contract, QueryLimits.ForRowCap(500)),
            ["query"] = new QueryResponse("synthetic", [new OutputColumn("text", "text")], [new Dictionary<string, object?> { ["text"] = "value" }], 1, true)
        };
        foreach (var (name, sample) in samples)
        {
            var schema = SuccessSchema(document, name);
            var wire = JsonSerializer.SerializeToElement(sample, JsonSerializerOptions.Web);
            EqualNames(wire.EnumerateObject().Select(p => p.Name), schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
            foreach (var property in wire.EnumerateObject().Where(p => p.Name == "source" && p.Value.ValueKind == JsonValueKind.Object || p.Value.ValueKind == JsonValueKind.Array && p.Name is "sources" or "columns"))
            {
                var child = Resolve(document, schema.GetProperty("properties").GetProperty(property.Name));
                var value = property.Value;
                if (value.ValueKind == JsonValueKind.Array) { child = Resolve(document, child.GetProperty("items")); value = value[0]; }
                EqualNames(value.EnumerateObject().Select(p => p.Name), child.GetProperty("properties").EnumerateObject().Select(p => p.Name));
                if (property.Name is "source" or "sources")
                {
                    EqualNames(["name", "title", "description", "grain", "rowCap"], child.GetProperty("required").EnumerateArray().Select(p => p.GetString()!));
                    Assert.Equal("integer", child.GetProperty("properties").GetProperty("rowCap").GetProperty("type").GetString());
                    Assert.Equal(contract.Source.RowCap, value.GetProperty("rowCap").GetInt32());
                }
            }
        }
        var querySchema = SuccessSchema(document, "query");
        Assert.True(querySchema.GetProperty("properties").TryGetProperty("hint", out _));
        EqualNames(["source", "columns", "rows", "rowCount", "more", "hint"],
            querySchema.GetProperty("required").EnumerateArray().Select(p => p.GetString()!));
        Assert.Equal("string", querySchema.GetProperty("properties").GetProperty("hint").GetProperty("type").GetString());
        foreach (var sample in new[] { new QueryResponse("synthetic", [], [], 0, false), new QueryResponse("synthetic", [], [], 1, false) })
        {
            var uncut = JsonSerializer.SerializeToElement(sample, JsonSerializerOptions.Web);
            EqualNames(querySchema.GetProperty("properties").EnumerateObject().Select(p => p.Name),
                uncut.EnumerateObject().Select(p => p.Name));
        }
        var describe = SuccessSchema(document, "describe").GetProperty("properties").GetProperty("querySchema");
        EqualNames(new[] { "columns", "select", "filters", "group_by", "aggregates", "order_by", "limit" },
            Resolve(document, describe).GetProperty("properties").EnumerateObject().Select(p => p.Name));
        var row = Resolve(document, SuccessSchema(document, "query").GetProperty("properties").GetProperty("rows"));
        row = Resolve(document, row.GetProperty("items"));
        Assert.Equal("object", row.GetProperty("type").GetString());
        Assert.True(!row.TryGetProperty("additionalProperties", out var any) || any.ValueKind != JsonValueKind.False);
        Assert.Empty(host.Lines());
    }

    /// <summary>Each operation documents exactly the statuses and codes in <see cref="Table"/>, each error body schema
    /// matches <see cref="ErrorBody"/>, and each code maps to its documented status.</summary>
    [Fact]
    public async Task T5_statuses_and_codes_are_exactly_the_public_literal_table()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(environment: "Development");
        await host.Start();
        using var json = JsonDocument.Parse(await JsonAnswer(host.Client, "/openapi/v1.json"));
        var union = new HashSet<string>();
        EqualNames(LibraryCodes, Enum.GetNames<JsonQueryErrorCode>());
        foreach (var name in Examples.Keys)
        {
            var expected = Table(name);
            var actual = HostPolicyDocument.Errors(name).ToDictionary(row => row.Status, row => row.Codes);
            EqualNames(expected.Keys.Select(v => v.ToString()), actual.Keys.Select(v => v.ToString()));
            var responses = Operation(json.RootElement, name).GetProperty("responses");
            EqualNames(expected.Keys.Select(v => v.ToString()).Append("200"), responses.EnumerateObject().Select(p => p.Name));
            foreach (var (status, codes) in expected)
            {
                EqualNames(codes, actual[status]);
                foreach (var code in codes)
                {
                    union.Add(code);
                    var reply = ErrorEnvelope.From(new CatalogFailure(code, "$", code), null);
                    Assert.Equal((status, code), (reply.Status, reply.Body.Code));
                }
                var response = responses.GetProperty(status.ToString());
                var schema = response.GetProperty("content").GetProperty("application/json").GetProperty("schema");
                Assert.Equal("object", schema.GetProperty("type").GetString());
                Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
                var properties = schema.GetProperty("properties");
                EqualNames(JsonSerializer.SerializeToElement(new ErrorBody("c", "p", "m", "i"), JsonSerializerOptions.Web).EnumerateObject().Select(p => p.Name), properties.EnumerateObject().Select(p => p.Name));
                EqualNames(codes, properties.GetProperty("code").GetProperty("enum").EnumerateArray().Select(v => v.GetString()!));
                foreach (var property in properties.EnumerateObject()) Assert.Equal("string", property.Value.GetProperty("type").GetString());
                EqualNames(status == 401 ? ["code", "path", "message"] : ["code", "path", "message", "incidentId"], schema.GetProperty("required").EnumerateArray().Select(v => v.GetString()!));
                Assert.Equal("^[0-9a-f]{32}$", properties.GetProperty("incidentId").GetProperty("pattern").GetString());
                if (status == 401)
                {
                    Assert.Contains("WWW-Authenticate: Negotiate", response.GetProperty("description").GetString());
                    Assert.Contains("refused handshake", response.GetProperty("description").GetString());
                }
            }
        }
        EqualNames(ExpectedCodes, union);
        Assert.Throws<InvalidOperationException>(() => HostPolicyDocument.StatusOf("CatalogCancelled"));
        Assert.Throws<InvalidOperationException>(() => HostPolicyDocument.StatusOf("InvalidCatalog"));
        Assert.Equal(504, HostPolicyDocument.StatusOf("QueryTimeout"));
    }

    /// <summary>The operations answer and log the same in Production, without the page, and in Development, with it, and
    /// every answer fits the documented schema for its status.</summary>
    [Fact]
    public async Task T6_operations_keep_their_bodies_statuses_and_failure_lines()
    {
        using var settings = new ErrorLogSettingsScope();
        var calls = new List<(string Name, string Body, string? ContentType, bool Get)>
        {
            ("list", "{}", "application/json", false), ("list", "{}", "text/plain", false), ("list", "{}", null, false),
            ("list", "not json", "application/json", false),
            ("describe", """{"source":"synthetic"}""", "application/json", false),
            ("describe", """{"source":"absent"}""", "application/json", false),
            ("query", """{"source":"synthetic","query":{"limit":1}}""", "application/json", false),
            ("query", """{"source":"synthetic","query":{"select":["absent"]}}""", "application/json", false),
            ("list", "", null, true)
        };
        calls.AddRange(Examples.Select(pair => (pair.Key, pair.Value, (string?)"application/json", false)));
        var answers = new List<(int Status, string? Type, string Body)>[2];
        JsonElement[][] lines = new JsonElement[2][];
        JsonElement document = default;
        for (var index = 0; index < 2; index++)
        {
            // The list seam and source seam each use the existing mapping and route code.
            var environment = index == 0 ? "Production" : "Development";
            await using var host = new LoggedHost(SourceCatalog(), ErrorLogTests.Engine(), environment: environment);
            await using var listHost = new LoggedHost(ErrorLogTests.Catalog(list: true), environment: environment);
            await host.Start();
            await listHost.Start();
            if (index == 1) { using var json = JsonDocument.Parse(await JsonAnswer(host.Client, "/openapi/v1.json")); document = json.RootElement.Clone(); }
            answers[index] = [];
            foreach (var call in calls)
            {
                using HttpContent content = call.ContentType is null ? new ByteArrayContent(Encoding.UTF8.GetBytes(call.Body)) : new StringContent(call.Body, Encoding.UTF8, call.ContentType);
                var client = call.Name == "list" ? listHost.Client : host.Client;
                using var response = call.Get ? await client.GetAsync(Route(call.Name)) : await client.PostAsync(Route(call.Name), content);
                var text = await response.Content.ReadAsStringAsync();
                if (call.Get) LoggedHost.Scan.TextResponse("wrong method", response, text);
                else LoggedHost.Scan.Response("operation response", response, text);
                Assert.NotEqual(415, (int)response.StatusCode);
                answers[index].Add(((int)response.StatusCode, response.Content.Headers.ContentType?.ToString(), Mask(text)));
                if (index == 1 && !call.Get) Documented(document, call.Name, response, text);
            }
            lines[index] = [.. host.Lines(), .. listHost.Lines()];
        }
        Assert.Equal(answers[0], answers[1]);
        Assert.Equal(lines[0].Length, lines[1].Length);
        foreach (var run in lines)
            Assert.Equal(new[] { "UnknownSource", "UnknownColumn", "UnknownSource", "UnknownSource", "InvalidJson" },
                run.Select(line => line.GetProperty("Code").GetString()));
        for (var index = 0; index < lines[0].Length; index++)
        {
            var left = JsonNode.Parse(LineMask(lines[0][index]))!.AsObject();
            var right = JsonNode.Parse(LineMask(lines[1][index]))!.AsObject();
            EqualNames(left.Select(p => p.Key), right.Select(p => p.Key));
            foreach (var field in left) Assert.True(JsonNode.DeepEquals(field.Value, right[field.Key]), "Failure line differs at field " + field.Key);
        }
        for (var index = 0; index < 2; index++)
        {
            await using var host = new LoggedHost(synthetic: false, environment: index == 0 ? "Production" : "Development");
            await host.Start();
            await PlainChallenge(host.Client, "/query", true);
            Assert.Empty(host.Lines());
        }
    }

    /// <summary>The page is configured with no external fonts, telemetry, agent, proxy, server list or remote script
    /// location, and its packaged assets exist.</summary>
    [Fact]
    public async Task T7_package_options_resources_and_local_script_configuration()
    {
        var options = new ScalarOptions();
        ApiPage.Configure(options);
        Assert.Equal("DataGateway", options.Title);
        Assert.False(options.DefaultFonts);
        Assert.False(options.Telemetry);
        Assert.True(options.Agent?.Disabled);
        Assert.Null(options.ProxyUrl);
        Assert.Null(options.Authentication);
        Assert.Null(options.BaseServerUrl);
        Assert.True(options.Servers is null || !options.Servers.Any());
#pragma warning disable CS0618 // The contract also checks the obsolete alias remains unset.
        foreach (var url in new[] { options.BundleUrl, options.CdnUrl })
            Assert.True(url is null || !Uri.TryCreate(url, UriKind.Absolute, out _));
#pragma warning restore CS0618
        foreach (var resource in typeof(ScalarOptions).Assembly.GetManifestResourceNames()) output.WriteLine("Resource: " + resource);
        foreach (var asset in Assets) Assert.NotEmpty(PageAssets.Packaged(asset));
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(environment: "Development");
        await host.Start();
        await Page(host);
        Assert.Empty(host.Lines());
    }

    /// <summary>Two hosts on different ports with failing catalogs serve the same document bytes, and nothing is logged.</summary>
    [Fact]
    public async Task T8_document_has_no_host_port_or_catalog_dependency()
    {
        using var settings = new ErrorLogSettingsScope();
        using var console = new ConsoleCapture();
        await using var first = new LoggedHost(ErrorLogTests.Catalog("io"), environment: "Development");
        await using var second = new LoggedHost(ErrorLogTests.Catalog("unknown"), environment: "Development");
        await first.Start();
        await second.Start();
        Assert.NotEqual(first.Client.BaseAddress, second.Client.BaseAddress);
        Assert.Equal(await JsonAnswer(first.Client, "/openapi/v1.json"), await JsonAnswer(second.Client, "/openapi/v1.json"));
        await Page(first);
        Assert.Empty(first.Lines());
        Assert.Empty(second.Lines());
        LoggedHost.Scan.Console("console", console.Lines, []);
        foreach (var line in console.Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries)) LoggedHost.Scan.Notice("stderr", line);
    }

    /// <summary>An endpoint the host adds itself stays out of the document, which lists only the three gateway
    /// operations, and the endpoint still works.</summary>
    [Fact]
    public async Task T9_unknown_operation_is_one_logged_unexpected_error()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(environment: "Development");
        host.App.MapGet("/extra", () => "x").WithName("extra");
        await host.Start();
        using var response = await host.Client.GetAsync("/openapi/v1.json");
        var text = await response.Content.ReadAsStringAsync();
        LoggedHost.Scan.Response("document failure", response, text);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(text);
        EqualNames(new[] { "/sources/list", "/sources/describe", "/query" }, json.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name));
        using var extra = await host.Client.GetAsync("/extra");
        Assert.Equal(HttpStatusCode.OK, extra.StatusCode);
        Assert.Equal("x", await extra.Content.ReadAsStringAsync());
        Assert.Empty(host.Lines());
    }

    /// <summary>A raw catalog with one source named <c>synthetic</c>; any other name has no detail.</summary>
    private static HostCatalogState SourceCatalog() => HostCatalogState.Raw(
        _ => Task.FromResult<IReadOnlyList<dynamic>>([CatalogEngineTests.ListRow()]),
        (name, _) => Task.FromResult<(IReadOnlyList<dynamic>, IReadOnlyList<dynamic>)>(name == "synthetic" ? (ErrorLogTests.Rows(), []) : ([], [new Dictionary<string, object?> { ["name"] = "synthetic" }])));

    /// <summary>The request check of the named operation.</summary>
    private static CatalogFailure? Parse(string name, JsonElement body) => name switch
    {
        "list" => SourceListing.CheckRequest(body), "describe" => SourceDescription.ParseRequest(body).Failure,
        "query" => QueryAnswer.ParseRequest(body).Failure, _ => throw new InvalidOperationException()
    };

    /// <summary>The route path of the named operation.</summary>
    private static string Route(string name) => name == "query" ? "/query" : "/sources/" + name;
    /// <summary>The document's entry for the named operation.</summary>
    private static JsonElement Operation(JsonElement document, string name) => document.GetProperty("paths").GetProperty(Route(name)).GetProperty("post");
    /// <summary>The resolved 200 response schema of the named operation.</summary>
    private static JsonElement SuccessSchema(JsonElement document, string name) => Resolve(document, Operation(document, name).GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
    /// <summary>Asserts two name lists hold the same names, ignoring order.</summary>
    private static void EqualNames(IEnumerable<string> expected, IEnumerable<string> actual) => Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

    /// <summary>Follows local <c>$ref</c> pointers until a schema with content is reached.</summary>
    private static JsonElement Resolve(JsonElement document, JsonElement schema)
    {
        var depth = 0;
        while (schema.TryGetProperty("$ref", out var reference))
        {
            Assert.True(++depth < 20);
            var path = reference.GetString()!;
            Assert.StartsWith("#/", path);
            schema = document;
            foreach (var segment in path[2..].Split('/')) schema = schema.GetProperty(segment.Replace("~1", "/").Replace("~0", "~"));
        }
        return schema;
    }

    /// <summary>Visits <paramref name="value"/> and every value nested in it.</summary>
    private static void Walk(JsonElement value, Action<JsonElement> visit)
    {
        visit(value);
        if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) Walk(property.Value, visit);
        if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Walk(item, visit);
    }

    /// <summary>The statuses and codes each operation may answer with, stated independently of the product.</summary>
    private static Dictionary<int, string[]> Table(string name)
    {
        var table = new Dictionary<int, string[]>
        {
            [400] = name == "query" ? LibraryCodes : ["InvalidJson", "InvalidStructure", "StructuralLimitExceeded"],
            [401] = ["NotAuthenticated"], [500] = ["InternalError", "UnexpectedError"],
            [503] = name == "query" ? ["CatalogUnavailable", "SourceUnavailable"] : ["CatalogUnavailable"], [504] = ["QueryTimeout"]
        };
        if (name != "list") table[404] = ["UnknownSource"];
        return table;
    }

    /// <summary>A body with its incident id masked, for comparing bodies from separate requests.</summary>
    private static string Mask(string body) => Regex.Replace(body, "\"incidentId\":\"[0-9a-f]{32}\"", "\"incidentId\":\"masked\"");
    /// <summary>A log line without the fields that differ between requests, for comparing lines.</summary>
    private static string LineMask(JsonElement line)
    {
        var node = JsonNode.Parse(line.GetRawText())!.AsObject();
        foreach (var name in new[] { "IncidentId", "@t", "RequestId", "ConnectionId", "TraceId", "SpanId", "ParentId", "Scope", "@tr", "@sp" }) node.Remove(name);
        return node.ToJsonString();
    }

    /// <summary>Asserts a response body has exactly the members its documented schema lists, and an error code the
    /// schema allows.</summary>
    private static void Documented(JsonElement document, string name, HttpResponseMessage response, string text)
    {
        var status = ((int)response.StatusCode).ToString();
        var documented = Operation(document, name).GetProperty("responses").GetProperty(status);
        using var body = JsonDocument.Parse(text);
        var schema = Resolve(document, documented.GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        var expectedNames = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name);
        EqualNames(expectedNames, body.RootElement.EnumerateObject().Select(p => p.Name));
        if (status != "200") Assert.Contains(body.RootElement.GetProperty("code").GetString(), schema.GetProperty("properties").GetProperty("code").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
    }
}
