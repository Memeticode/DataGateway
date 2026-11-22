using Microsoft.Extensions.DependencyInjection;
using DataGateway.Data.SqlServer;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DataGateway.Page;
using DataGateway.Web.Page;
using DataGateway.Core.Queries;
using DataGateway.Core.Tests;
using DataGateway.Demo;
using DataGateway.Data.Catalog;
using DataGateway.Data.Queries;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class ReadmeTests
{
    [Fact]
    public void T14_catalog_database_variable_and_default_setup_commands_agree()
    {
        foreach (var name in new[] { "catalog-ddl.sql", "catalog-views.sql", "catalog-row-cap.sql", "catalog-seed-example.sql" })
        {
            var sql = File.ReadAllText(TestFiles.InRepo("db/" + name));
            Assert.Single(Regex.Matches(sql, @"(?im)^USE \[\$\(CatalogDatabase\)\]"));
            Assert.DoesNotMatch(@"(?im)^\s*USE\s+\[Catalog\]", sql);
            Assert.DoesNotMatch(@"(?im)^\s*:setvar\s+CatalogDatabase\b", sql);
            foreach (var database in new[] { "Catalog", "SyntheticOtherCatalog" })
            {
                var expanded = sql.Replace("$(CatalogDatabase)", database, StringComparison.Ordinal);
                Assert.Single(Regex.Matches(expanded, @"(?im)^USE \[" + database + @"\]"));
                Assert.DoesNotContain("$(CatalogDatabase)", expanded);
            }
        }
        foreach (var command in new[] {
            "sqlcmd -S $instance -E -b -i db/catalog-ddl.sql -v CatalogDatabase=Catalog",
            "sqlcmd -S $instance -E -b -i db/catalog-views.sql -v CatalogDatabase=Catalog",
            "sqlcmd -S $instance -E -b -i db/catalog-row-cap.sql -v CatalogDatabase=Catalog",
            "sqlcmd -S $instance -d Catalog -E -b -r 1 -i <your copy> -v CatalogDatabase=Catalog" })
            Assert.Contains(command, Markdown);
        Assert.Contains("sqlcmd -S $instance -d Catalog -E -b -r 1 -i <your copy> -v CatalogDatabase=Catalog",
            File.ReadAllText(TestFiles.InRepo("db/catalog-seed-example.sql")));
        Assert.Contains("CREATE DATABASE [Catalog]", Markdown);
        Assert.Contains("ALTER DATABASE [Catalog] SET RECOVERY SIMPLE", Markdown);
        Assert.Contains("-i db/gateway-reader.sql -v CatalogDatabase=Catalog SourceDatabase=$sourceDatabase", Markdown);
        Assert.Contains("another explicitly named SQL Server database is accepted", Markdown);
        Assert.Contains("The logical key stays `ConnectionStrings:Catalog`", Markdown);
        Assert.Contains("the scripts define no default", Markdown);
    }

    internal static string Markdown => File.ReadAllText(TestFiles.InRepo("DataGateway.Web/README.md"));
    internal static string LibraryMarkdown => File.ReadAllText(TestFiles.InRepo("README.md"));
    internal static IReadOnlyList<ReadmeExample> Examples => ReadmeExamples.Parse(Markdown);
    internal static JsonElement Example(string name) => Examples.Single(e => e.Name == name).Answer!.Value;

    [Fact]
    public void T12_client_registration_names_the_data_and_is_not_an_API_example()
    {
        var markdown = Markdown.Replace("\r", "");
        var match = Regex.Match(markdown, @"(?ms)^```text\n(\{\n  ""mcpServers"":.*?\n\})\n```$");
        Assert.True(match.Success, "Missing client configuration text fence.");
        using var config = JsonDocument.Parse(match.Groups[1].Value);
        var servers = config.RootElement.GetProperty("mcpServers");
        Assert.Equal(new[] { "sales-data" }, servers.EnumerateObject().Select(p => p.Name));
        var proxy = servers.GetProperty("sales-data");
        Assert.Equal("DataGateway.Proxy", proxy.GetProperty("command").GetString());
        Assert.Equal(new[] { "http://localhost:5000/mcp" }, proxy.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Contains("mcp__sales-data__<tool>", markdown);
        Assert.Contains("Claude Desktop", markdown);
        Assert.Contains("Claude Code", markdown);
        Assert.Contains("A deferred tool list may show only names before a tool is loaded", markdown);
        Assert.Contains("claude mcp add sales-data -- DataGateway.Proxy http://localhost:5000/mcp", markdown);
        Assert.Equal(new[] { "challenge", "describe", "list", "query", "refusal", "unknown-source", "vocabulary" },
            ReadmeExamples.Parse(markdown).Select(e => e.Name).Order());
    }

    [Fact]
    public void T13_opening_describes_the_relational_data_window()
    {
        var opening = LibraryMarkdown.Replace("\r", "").Split('\n')[2];
        Assert.Equal("""DataGateway is a read-only library for relational data. An ASP.NET Core app supplies a catalog reader and database engines, then maps JSON and MCP endpoints.""", opening);
    }

    [Fact]
    public async Task T3_examples_match_real_routes_in_order_and_number_spelling()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var wire = new ReadmeWire();
        await wire.Start();
        using var client = wire.Client();
        foreach (var example in Examples)
        {
            var answer = await ReadmeWire.Send(example.Anonymous ? wire.Anonymous.Client : client, example.Route!, example.Request ?? "{}");
            Assert.Equal("application/json; charset=utf-8", answer.ContentType);
            if (example.Answer is null) { Assert.Equal(200, answer.Status); continue; }
            Assert.Equal(example.Status, answer.Status);
            EqualAnswer(example.Answer.Value, JsonSerializer.Deserialize<JsonElement>(answer.Body));
        }
        Assert.Equal(Compact(JsonSerializer.Deserialize<JsonElement>(HostPolicyDocument.QueryExample)),
            Compact(JsonSerializer.Deserialize<JsonElement>(Examples.Single(e => e.Name == "query").Request!)));
        EqualAnswer(Example("list").GetProperty("sources")[0], Example("describe").GetProperty("source"));
    }

    internal static string Compact(JsonElement value) => JsonSerializer.Serialize(value);
    internal static void EqualAnswer(JsonElement expected, JsonElement actual)
    {
        var left = JsonNode.Parse(expected.GetRawText())!.AsObject();
        var right = JsonNode.Parse(actual.GetRawText())!.AsObject();
        if (left.ContainsKey("incidentId"))
        {
            foreach (var node in new[] { left, right })
            {
                Assert.Matches("^[0-9a-f]{32}$", node["incidentId"]!.GetValue<string>());
                node["incidentId"] = "incident";
            }
        }
        if (left.ContainsKey("querySchema"))
        {
            Subset(left["querySchema"]!, right["querySchema"]!);
            right["querySchema"] = left["querySchema"]!.DeepClone();
        }
        Assert.Equal(Compact(JsonSerializer.SerializeToElement(left)), Compact(JsonSerializer.SerializeToElement(right)));
    }

    private static void Subset(JsonNode expected, JsonNode actual)
    {
        if (expected is JsonObject obj)
            foreach (var pair in obj)
            {
                Assert.True(actual is JsonObject && actual.AsObject().ContainsKey(pair.Key), "Schema missing " + pair.Key);
                if (pair.Value is null) Assert.Null(actual[pair.Key]);
                else Subset(pair.Value, actual[pair.Key]!);
            }
        else Assert.True(JsonNode.DeepEquals(expected, actual), "Schema subset scalar or array differs.");
    }

    public sealed record Trigger(string Code, int Status, string Path, string Route, string Body, string Mode = "ok", string? Setting = null, string? Value = null);
    public static Trigger ForHost(Trigger trigger) => trigger.Mode == "timeout"
        ? trigger with { Code = "CatalogUnavailable", Status = 503 } : trigger;
    private static Trigger Q(string code, string path, string query) => new(code, 400, "$.query" + path, "/query", "{\"source\":\"sales_by_line\",\"query\":" + query + "}");
    public static IEnumerable<object[]> Triggers()
    {
        yield return [new Trigger("InvalidJson", 400, "$", "/query", "not json")];
        yield return [Q("InvalidJson", "", "[]")];
        yield return [Q("InvalidJson", ".limit", """{"limit":2,"limit":3}""")];
        yield return [new Trigger("InvalidStructure", 400, "$", "/sources/list", """{"source":"x"}""")];
        yield return [new Trigger("InvalidStructure", 400, "$.source", "/sources/describe", """{"source":1}""")];
        yield return [Q("InvalidStructure", ".select", """{"select":null}""")];
        yield return [Q("UnknownColumn", ".select[0]", """{"select":["no_such_column"]}""")];
        yield return [Q("ColumnOperationNotAllowed", ".group_by[0]", """{"group_by":["line_total"]}""")];
        yield return [Q("InvalidOperator", ".filters[0].op", """{"filters":[{"col":"country","op":"bogus","value":"x"}]}""")];
        yield return [Q("InvalidAggregateFunction", ".aggregates[0].fn", """{"aggregates":[{"fn":"median","col":"line_total"}]}""")];
        yield return [Q("InvalidSortDirection", ".order_by[0].dir", """{"order_by":[{"col":"line_total","dir":"up"}]}""")];
        yield return [Q("InvalidValue", ".filters[0].value", """{"filters":[{"col":"line_total","op":"eq","value":"ten"}]}""")];
        yield return [Q("InvalidClauseCombination", ".select", """{"select":["country"],"group_by":["country"]}""")];
        yield return [Q("InvalidOrderTarget", ".order_by[0].col", """{"group_by":["country"],"order_by":[{"col":"line_total","dir":"asc"}]}""")];
        yield return [Q("DuplicateReference", ".select[1]", """{"select":["country","COUNTRY"]}""")];
        yield return [Q("InvalidLimit", ".limit", """{"limit":0}""")];
        yield return [new Trigger("StructuralLimitExceeded", 400, "$", "/query", new string('x', 2000), "body")];
        yield return [Q("StructuralLimitExceeded", "", "{\"select\":[\"country\"]" + new string(' ', 19) + "}") with { Setting = "MaxJsonLength", Value = "40" }];
        yield return [Q("StructuralLimitExceeded", ".limit", """{"limit":501}""")];
        yield return [Q("StructuralLimitExceeded", ".filters[0].value", """{"filters":[{"col":"country","op":"in","value":["a","b","c"]}]}""") with { Setting = "MaxInValues", Value = "2" }];
        yield return [new Trigger("UnknownSource", 404, "$.source", "/sources/describe", """{"source":"no_such_source"}""")];
        yield return [new Trigger("NotAuthenticated", 401, "$", "/sources/list", "{}", "anonymous")];
        foreach (var (mode, code, status) in new[] { ("io", "CatalogUnavailable", 503), ("timeout", "QueryTimeout", 504) })
        {
            yield return [new Trigger(code, status, "$", "/sources/list", "{}", mode)];
            yield return [new Trigger(code, status, "$.source", "/sources/describe", """{"source":"sales_by_line"}""", mode)];
        }
        yield return [new Trigger("SourceUnavailable", 503, "$.source", "/query", DemoPlan.Cap("sales_by_line", DemoPlan.Columns(Example("describe"))[0]), "runner-io")];
        yield return [new Trigger("QueryTimeout", 504, "$.query", "/query", DemoPlan.Cap("sales_by_line", DemoPlan.Columns(Example("describe"))[0]), "runner-timeout")];
        yield return [new Trigger("InternalError", 500, "$", "/sources/describe", """{"source":"sales_by_line"}""", "off")];
        yield return [new Trigger("UnexpectedError", 500, "$", "/sources/describe", """{"source":"sales_by_line"}""", "unexpected")];
    }

    [Fact]
    public void T4_error_table_matches_operation_codes_statuses_and_all_trigger_pairs()
    {
        var table = ReadmeExamples.ErrorTable(Markdown);
        Assert.Equal(new[] { "list", "describe", "query" }.SelectMany(op => HostPolicyDocument.Of(op).Codes).Distinct().Order(), table.Select(row => row.Code).Order());
        foreach (var row in table) Assert.Equal(HostPolicyDocument.StatusOf(row.Code), row.Status);
        var pairs = table.SelectMany(row => row.Paths.Select(path => (row.Code, path))).Order().ToArray();
        Assert.Equal(Triggers().Select(row => (Trigger)row[0]).Select(t => (t.Code, t.Path)).Order(), pairs);
        Assert.Equal(30, pairs.Length);
    }

    internal static void AssertStopGuidance(JsonElement body, Trigger trigger)
    {
        const string guidance = "Tell the user, give the incidentId and stop; do not retry.";
        var message = body.GetProperty("message").GetString()!;
        var stop = trigger.Code is "CatalogUnavailable" or "SourceUnavailable" or "InternalError" or "UnexpectedError"
            || (trigger.Code == "QueryTimeout" && trigger.Mode == "timeout");
        Assert.Equal(stop ? 1 : 0, message.Split(guidance, StringSplitOptions.None).Length - 1);
        if (stop)
            Assert.EndsWith(" " + guidance, message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Triggers))]
    public async Task T4_each_documented_code_path_and_incident_is_observed(Trigger trigger)
    {
        trigger = ForHost(trigger);
        using var settings = new ErrorLogSettingsScope();
        await using var wire = new ReadmeWire(mode: trigger.Mode, settings: trigger.Setting is null ? null :
            new Dictionary<string, string?> { ["DataGateway:Limits:" + trigger.Setting] = trigger.Value });
        await wire.Start();
        using var client = wire.Client();
        var answer = await ReadmeWire.Send(trigger.Mode == "anonymous" ? wire.Anonymous.Client : client, trigger.Route, trigger.Body);
        using var json = JsonDocument.Parse(answer.Body);
        Assert.Equal(trigger.Status, answer.Status);
        Assert.Equal(trigger.Code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(trigger.Path, json.RootElement.GetProperty("path").GetString());
        AssertStopGuidance(json.RootElement, trigger);
        var lines = wire.ListHost.Lines().Concat(wire.SourceHost.Lines()).Concat(wire.Anonymous.Lines()).ToArray();
        if (trigger.Mode == "anonymous") { Assert.Empty(lines); Assert.Empty(DemoChecks.Anonymous(answer)); }
        else
        {
            Assert.Empty(DemoChecks.Refusal(answer, trigger.Status, trigger.Code, trigger.Path));
            var line = Assert.Single(lines);
            Assert.Equal(json.RootElement.GetProperty("incidentId").GetString(), line.GetProperty("IncidentId").GetString());
            Assert.Equal(trigger.Code == "InternalError" ? "InvalidCatalog" : trigger.Code, line.GetProperty("Code").GetString());
        }
    }

    [Fact]
    public void T5_links_paths_settings_and_attribution_stay_inside_public_root()
    {
        var readme = Markdown;
        var config = File.ReadAllText(TestFiles.InRepo("DataGateway.Web/CONFIGURATION.md"));
        Assert.NotEmpty(ReadmeExamples.Parse(readme));
        foreach (var (text, folder) in new[] { (readme, TestFiles.InRepo("DataGateway.Web")), (config, TestFiles.InRepo("DataGateway.Web")),
            (LibraryMarkdown, TestFiles.RepoRoot),
            (File.ReadAllText(TestFiles.InRepo("CONFIGURATION.md")), TestFiles.RepoRoot),
            (File.ReadAllText(TestFiles.InRepo("CATALOG-READERS.md")), TestFiles.RepoRoot),
            (File.ReadAllText(TestFiles.InRepo("ENGINES.md")), TestFiles.RepoRoot),
            (File.ReadAllText(TestFiles.InRepo("DataGateway.Data.Sqlite/README.md")), TestFiles.InRepo("DataGateway.Data.Sqlite")) })
            foreach (Match match in Regex.Matches(text, @"\[[^\]]*\]\(([^)]+)\)"))
            {
                var link = match.Groups[1].Value;
                if (Uri.TryCreate(link, UriKind.Absolute, out _)) continue;
                var path = Path.GetFullPath(Path.Combine(folder, link.Split('#')[0]));
                Assert.StartsWith(TestFiles.RepoRoot + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(path), "Broken link: " + link);
            }
        foreach (Match setting in Regex.Matches(readme, @"`((?:DATAGATEWAY_|ASPNETCORE_|DataGateway:|DataGateway__|Serilog:)[^`]+)`"))
            Assert.Contains(setting.Groups[1].Value, config);
        foreach (Match path in Regex.Matches(readme, @"`((?:db/[\w.-]+\.sql|DataGateway\.[\w.]+|DataGateway\.slnx|LICENSE|NOTICE))`"))
            Assert.True(File.Exists(TestFiles.InRepo(path.Groups[1].Value)) || Directory.Exists(TestFiles.InRepo(path.Groups[1].Value)), "Missing path " + path.Groups[1].Value);
        Assert.Contains(Attribution, readme);
        Assert.Contains(Attribution, LibraryMarkdown);
    }
    internal const string Attribution = "Designed and directed by Maxim Konstantinovski. Built with AI models (Anthropic's Claude and OpenAI's GPT) under his direction and review.";

    [Fact]
    public void T9_T10_demo_is_independent_and_licence_bytes_are_canonical()
    {
        var Markdown = LibraryMarkdown;
        var project = XDocument.Load(TestFiles.InRepo("DataGateway.Demo/DataGateway.Demo.csproj"));
        Assert.DoesNotContain(project.Descendants(), e => e.Name.LocalName is "PackageReference" or "ProjectReference");
        foreach (var file in Directory.GetFiles(TestFiles.InRepo("DataGateway.Demo"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (var word in new[] { "GetEnvironmentVariable", "IConfiguration", "ConnectionString" }) Assert.DoesNotContain(word, text);
            Assert.DoesNotMatch(LeakScan.KeywordForm, text);
        }
        var license = File.ReadAllText(TestFiles.InRepo("LICENSE")).Replace("\r", "");
        Assert.Equal("cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(license))));
        Assert.Equal("DataGateway\n\n" + Attribution + "\n", File.ReadAllText(TestFiles.InRepo("NOTICE")).Replace("\r", ""));
        Assert.Contains("Apache License 2.0: [LICENSE](LICENSE), [NOTICE](NOTICE).", Markdown);
    }

    [Fact]
    public void T11_seed_preserves_auditing_and_ddl_comments_state_the_credential_rule()
    {
        var seed = File.ReadAllText(TestFiles.InRepo("db/catalog-seed-example.sql"));
        Assert.DoesNotMatch(@"(?i)\b(TRUNCATE|DELETE|BULK|OPENROWSET)\b|@@IDENTITY|NOT\s+MATCHED\s+BY\s+SOURCE|\bOUTPUT\b(?![\s\S]*\bINTO\b)", seed);
        foreach (var required in new[] { "MERGE [dbo].[DataSource]", "MERGE [dbo].[DataSourceColumn]", "SCOPE_IDENTITY()", "DATALENGTH", "THROW 50001" }) Assert.Contains(required, seed);
        Assert.Single(Regex.Matches(seed, Regex.Escape("'$(SourceConnectionString)'")));
        var line = File.ReadAllLines(TestFiles.InRepo("db/catalog-ddl.sql"))[18];
        Assert.Contains("credentials included", line);
        Assert.DoesNotContain("never a password", line);
    }

    [Theory]
    [InlineData("```json\n{}\n```", "marker")]
    [InlineData("<!-- example name=x kind=answer status=200 -->\n```json\n{}\n```", "request")]
    [InlineData("<!-- example name=x kind=request route=/query -->\n```json\n{}\n```\n<!-- example name=x kind=request route=/query -->\n```json\n{}\n```", "Duplicate")]
    public void Parser_rejects_unmarked_or_ambiguous_examples(string text, string problem) => Assert.Contains(problem, Assert.Throws<FormatException>(() => ReadmeExamples.Parse(text)).Message);
}

internal sealed class ReadmeWire : IAsyncDisposable
{
    internal LoggedHost ListHost { get; }
    internal LoggedHost SourceHost { get; }
    internal LoggedHost Anonymous { get; } = new(synthetic: false);

    internal ReadmeWire(JsonElement? listed = null, IReadOnlyDictionary<string, JsonElement>? described = null,
        DataGateway.Data.IDatabaseEngine? engine = null, string mode = "ok", IDictionary<string, string?>? settings = null)
    {
        var list = listed ?? ReadmeTests.Example("list");
        var describe = described ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase) { ["sales_by_line"] = ReadmeTests.Example("describe") };
        Task<IReadOnlyList<dynamic>> List(CancellationToken token) => Task.FromResult<IReadOnlyList<dynamic>>(
            list.GetProperty("sources").EnumerateArray().Select(s => (dynamic)SourceFields(s)).ToArray());
        Task<(IReadOnlyList<dynamic>, IReadOnlyList<dynamic>)> Detail(string name, CancellationToken token, bool off)
        {
            var answer = describe.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            IReadOnlyList<dynamic> rows = answer.Key is null ? [] : SourceRows(answer.Value, off);
            IReadOnlyList<dynamic> available = list.GetProperty("sources").EnumerateArray().Select(s => (dynamic)new Dictionary<string, object?> { ["name"] = s.GetProperty("name").GetString() }).ToArray();
            return Task.FromResult((rows, available));
        }
        HostCatalogState Catalog()
        {
            var catalog = HostCatalogState.Raw(token => { Throw(mode); return List(token); },
                (name, token) => { Throw(mode); return Detail(name, token, mode == "off"); });
            catalog.StartupList = async token => DataGateway.Web.Catalog.CatalogTableReader.MapList(await List(token));
            catalog.StartupDetail = async (name, token) =>
            {
                var rows = await Detail(name, token, false);
                return rows.Item1.Count == 0 ? null : DataGateway.Web.Catalog.CatalogTableReader.MapDetail(rows.Item1, "sqlserver", true);
            };
            return catalog;
        }
        engine ??= FakeRows.Engine(new SqlServerEngine(), FakeRows.From((_, compiled, connected, _) =>
        {
            connected();
            if (mode == "runner-io") throw new IOException();
            if (mode == "runner-timeout") throw new TimeoutException();
            IReadOnlyList<dynamic> rows = compiled.Sql.Contains("GROUP BY", StringComparison.Ordinal)
                ? ReadmeTests.Example("query").GetProperty("rows").EnumerateArray().Select(r => (dynamic)Fields(r)).ToArray() : [];
            return Task.FromResult(rows);
        }));
        var fault = new AnswerFault([typeof(DataGateway.Core.Catalog.SourceDescribeResponse)], () => new NotSupportedException("Synthetic answer fault."));
        if (mode == "unexpected") fault.ArmAlways();
        void Configure(Microsoft.AspNetCore.Builder.WebApplicationBuilder builder)
        {
            if (mode == "body") builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024);
            if (mode == "unexpected") builder.Services.AddSingleton(new global::DataGateway.GatewayJson(fault));
        }
        ListHost = new(Catalog(), settings: settings, configure: Configure);
        SourceHost = new(Catalog(), engine, settings, configure: Configure);
    }

    private static void Throw(string mode)
    {
        if (mode == "io") throw new IOException();
        if (mode == "timeout") throw new TimeoutException();
    }
    internal static Dictionary<string, object?> Fields(JsonElement element) => element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind switch
    {
        JsonValueKind.String => (object?)p.Value.GetString(), JsonValueKind.Number => p.Value.GetDecimal(),
        JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null, _ => throw new FormatException("Synthetic field is not scalar.")
    });
    private static Dictionary<string, object?> SourceFields(JsonElement source)
    {
        var row = Fields(source);
        row.Remove("rowCap");
        row["row_cap"] = source.GetProperty("rowCap").GetInt32();
        row["query_timeout_seconds"] = 1;
        return row;
    }

    internal static IReadOnlyList<dynamic> SourceRows(JsonElement describe, bool off = false) => describe.GetProperty("querySchema").GetProperty("columns").EnumerateArray().Select((c, i) =>
    {
        var row = SourceFields(describe.GetProperty("source"));
        row["id"] = 7319;
        row["object_name"] = "dbo.vSalesByLine";
        row["connection_string"] = ErrorLogTests.SourceConnection;
        row["column_name"] = c.GetProperty("name").GetString();
        row["ordinal"] = i + 1;
        foreach (var p in Fields(c).Where(p => p.Key != "name"))
        {
            var key = p.Key switch { "title" => "column_title", "description" => "column_description", "dataType" => "data_type", "displayFormat" => "display_format", _ => Regex.Replace(p.Key, "[A-Z]", m => "_" + m.Value.ToLowerInvariant()) };
            row[key] = off && key.StartsWith("can_", StringComparison.Ordinal) ? false : p.Value;
        }
        return (dynamic)row;
    }).ToArray();
    internal async Task Start() { await ListHost.Start(); await SourceHost.Start(); await Anonymous.Start(); }
    internal HttpClient Client(DelegatingHandler? mutation = null)
    {
        var route = new RouteHandler(ListHost.Client, SourceHost.Client);
        if (mutation is null) return new(route) { BaseAddress = SourceHost.Client.BaseAddress };
        mutation.InnerHandler = route;
        return new(mutation) { BaseAddress = SourceHost.Client.BaseAddress };
    }
    private sealed class RouteHandler(HttpClient list, HttpClient source) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var client = request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/sources/list" ? list : source;
            using var forwarded = new HttpRequestMessage(request.Method, new Uri(client.BaseAddress!, request.RequestUri!.PathAndQuery))
            { Content = request.Content is null ? null : new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(token)) };
            return await client.SendAsync(forwarded, token);
        }
    }
    internal static async Task<DemoAnswer> Send(HttpClient client, string path, string body)
    {
        using var response = await client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        LoggedHost.Scan.Response("README answer", response, text);
        return new((int)response.StatusCode, response.Content.Headers.ContentType?.ToString(), string.Join(", ", response.Headers.WwwAuthenticate), text);
    }
    public async ValueTask DisposeAsync() { await ListHost.DisposeAsync(); await SourceHost.DisposeAsync(); await Anonymous.DisposeAsync(); }
}
