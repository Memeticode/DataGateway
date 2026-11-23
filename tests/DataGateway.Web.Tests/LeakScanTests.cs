using DataGateway.Data.SqlServer;
using System.Net;
using System.Text.Json;
using DataGateway.Web.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Sdk;

namespace DataGateway.Web.Tests;

/// <summary>Pins the <see cref="LeakScan"/> oracle itself: how it splits a connection string into parts, which text
/// each ownership rule rejects, and that its failures print labels, never values.</summary>
/// <remarks>Every leak test in the suite trusts this scan; if it missed a part or printed one, those tests would pass
/// while a secret leaked, or leak the secret into test output themselves.</remarks>
public sealed class LeakScanTests
{
    /// <summary>An invented connection with every part kind and some settings the scan must ignore.</summary>
    private const string Connection = "Server=tcp:fake-host,1433;UID=fake-user;PWD=fake-pass;Database=Catalog;Application Name=fake-app;Encrypt=False";
    /// <summary>A well-formed incident id.</summary>
    private const string Id = "0123456789abcdef0123456789abcdef";
    /// <summary>A synthetic scan over <see cref="Connection"/>.</summary>
    private static LeakScan Scan() => LeakScan.Synthetic(("fixture", Connection));
    /// <summary>Serializes a value to a JSON element.</summary>
    private static JsonElement Value(object value) => JsonSerializer.SerializeToElement(value);
    /// <summary>A clean failure log line with the given fields added or replaced.</summary>
    private static JsonElement Line(params (string Key, object? Value)[] extras)
    {
        var fields = new Dictionary<string, object?>
        {
            ["SourceContext"] = "DataGateway.Failure", ["IncidentId"] = Id, ["Operation"] = "query",
            ["Caller"] = "ordinary caller", ["Code"] = "InvalidJson", ["Message"] = "Correct the request.",
            ["Step"] = null, ["ExceptionType"] = null, ["SqlNumber"] = null
        };
        foreach (var (key, value) in extras) fields[key] = value;
        return Value(fields);
    }
    /// <summary>Asserts the scan fails, that its message names <paramref name="label"/>, and that it prints none of
    /// <paramref name="absent"/>.</summary>
    /// <returns>The failure message.</returns>
    private static string Rejected(Action action, string label, params string[] absent)
    {
        var error = Assert.Throws<XunitException>(action);
        Assert.True(error.Message.Contains(label, StringComparison.Ordinal), "Failure omitted its label.");
        foreach (var text in absent)
            Assert.True(!error.Message.Contains(text, StringComparison.Ordinal), "Failure printed a planted value.");
        return error.Message;
    }

    [Fact]
    public void T4_parts_are_exactly_the_five_kinds_and_ignore_other_settings()
    {
        var scan = Scan();
        Assert.Equal(Enum.GetValues<PartKind>(), scan.Parts.Select(p => p.Kind));
        Assert.True(scan.Parts.Single(p => p.Kind == PartKind.Host).Value == "fake-host", "Host extraction differs.");
        foreach (var ordinary in new[] { "Catalog", "fake-app", "False", "1433" }) Assert.Empty(scan.Found(ordinary, Rule.DataGateway));
        var unknown = LeakScan.Synthetic(("unknown", "Server=fake-host;UID=fake-user;PWD=fake-pass;Unknown=anything"));
        Assert.Equal(5, unknown.Parts.Count);
        Assert.Single(LeakScan.Synthetic(("short", "Server=abc;UID=xy;PWD=z")).Parts);
        Rejected(() => LeakScan.Real(("short", "Server=abc;UID=xy;PWD=z")), "short user", "xy", "PWD=z");
        Rejected(() => LeakScan.Real(("short password", "Server=abc;PWD=z")), "short password password", "PWD=z");
        Assert.Single(LeakScan.Real(("short", "Server=abc;Integrated Security=true")).Parts);
        Assert.Single(LeakScan.Synthetic(("broken", "unparsable-fake-connection")).Parts);
        Rejected(() => LeakScan.Real(("broken", "unparsable-fake-connection")), "broken", "unparsable-fake-connection");
        var loop = LeakScan.Synthetic(("loop", "Server=tcp:127.0.0.1,1433;UID=fake-user;PWD=fake-pass"));
        Assert.False(loop.Parts.Single(p => p.Kind == PartKind.Host).Distinctive);
        Assert.True(loop.Parts.Single(p => p.Kind == PartKind.Server).Distinctive);
        foreach (var host in new[] { "localhost", "127.0.0.1" })
            Assert.All(LeakScan.Synthetic(("loop", "Server=" + host)).Parts.Where(p => p.Kind != PartKind.Connection), p => Assert.False(p.Distinctive));
    }
    /// <summary>A password too short to scan for in ordinary text refuses a real scan, yet is still found in driver
    /// text, where its exact value is what a driver would quote.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void T4_short_passwords_refuse_real_construction_and_remain_driver_detectable(int length)
    {
        var password = new string('z', length);
        var connection = "Server=plain-host;PWD=" + password;
        Rejected(() => LeakScan.Real(("short fixture", connection)), "short fixture password", connection);
        var scan = LeakScan.Synthetic(("short fixture", connection));
        Rejected(() => scan.LogLine("short driver", Line(("DriverMessage", "quotes " + password))),
            "short fixture password", connection, "quotes " + password);
        Assert.Contains("short fixture password", scan.Found("quotes " + password, Rule.Driver));
    }
    [Theory]
    [InlineData("Data Source", "User", "Password")]
    [InlineData("Address", "User ID", "PWD")]
    [InlineData("Addr", "UID", "Password")]
    [InlineData("Network Address", "User  ID", "PWD")]
    public void T4_synonyms_and_normalized_keys(string server, string user, string password)
    {
        var scan = LeakScan.Synthetic(("synonym", $" {server} =fake-host;{user}=fake-user;{password}=fake-pass"));
        Assert.Equal(Enum.GetValues<PartKind>(), scan.Parts.Select(p => p.Kind));
    }
    [Theory]
    [InlineData("tcp:")]
    [InlineData("NP:")]
    [InlineData("lpc:")]
    [InlineData("ADMIN:")]
    public void T4_prefixes_ports_and_instances(string prefix)
    {
        foreach (var suffix in new[] { ",1433", "\\SQL01", "" })
        {
            var scan = LeakScan.Synthetic(("prefix", "Server=" + prefix + "fake-host" + suffix));
            Assert.True(scan.Parts.Single(p => p.Kind == PartKind.Host).Value == "fake-host", "Host extraction differs.");
        }
    }
    [Theory]
    [InlineData("data  source =", true)] [InlineData("PASSWORD=", true)]
    [InlineData("uSeR  Id =", true)] [InlineData("Network address =", true)]
    [InlineData("initial catalog =", true)] [InlineData("Address=", true)]
    [InlineData("addr=", true)] [InlineData("UID=", true)] [InlineData("user=", true)]
    [InlineData("PWD=", true)] [InlineData("database=", true)] [InlineData("Server\t=", true)]
    [InlineData("DEFAULT_DATABASE = [master]", false)] [InlineData("charset=utf-8", false)]
    [InlineData("Microsoft.AspNetCore.Server.Kestrel", false)] [InlineData("the server refused", false)]
    [InlineData("dotnet user-secrets set", false)] [InlineData("ServerName=", false)] [InlineData("superuser=x", false)]
    public void T5_keyword_forms(string text, bool found)
    {
        Assert.Equal(found, LeakScan.KeywordForm.IsMatch(text));
        Assert.Equal(found, Scan().Found(text, Rule.DataGateway).Contains("a connection keyword form"));
        Assert.Empty(Scan().Found(text, Rule.Caller));
        Assert.DoesNotMatch(LeakScan.KeywordForm, SqlServerEngine.Unparseable);
    }
    /// <summary>Matching ignores case, sees through JSON escapes, never matches inside numbers, and names a leaking
    /// property by its parent's place, not by the leaking name.</summary>
    [Fact]
    public void T6_matching_decodes_json_and_never_uses_numbers_or_unsafe_property_locations()
    {
        var scan = Scan();
        Rejected(() => scan.Text("case", "FAKE-PASS", Rule.DataGateway), "fixture password", "FAKE-PASS");
        using var escaped = JsonDocument.Parse("{\"text\":\"\\u0066ake-pass\"}");
        Rejected(() => scan.Json("escaped", escaped.RootElement, Rule.DataGateway), "escaped.text", "fake-pass");
        var quoted = LeakScan.Synthetic(("quoted", "PWD='fake-\\\\\"password'"));
        var part = quoted.Parts.Single(p => p.Kind == PartKind.Password);
        Rejected(() => quoted.Json("quoted", Value(part.Value), Rule.DataGateway), "quoted password", part.Value);
        var numeric = LeakScan.Real(("numeric", "PWD=4821"));
        numeric.Json("number", Value(124821), Rule.DataGateway);
        var message = Rejected(() => scan.Json("parent", Value(new Dictionary<string, string> { ["fake-pass"] = "ordinary" }), Rule.DataGateway), "parent", "fake-pass");
        Assert.True(message.StartsWith("Connection text in parent:", StringComparison.Ordinal), "Property failure used an unsafe location.");
    }
    /// <summary>A failure, a collection assertion and the scan's own display print labels only, never part values.</summary>
    [Fact]
    public void T7_failures_and_display_are_labels_only()
    {
        var scan = Scan();
        Rejected(() => scan.Text("fixed surface", "fake-pass", Rule.DataGateway), "fixture password", "fake-pass");
        Rejected(() => scan.Text("fixed surface", "Password=private-payload", Rule.DataGateway), "a connection keyword form", "private-payload", "Password=");
        Assert.All(scan.Parts, p => Assert.Equal(p.Label, p.ToString()));
        var other = LeakScan.Synthetic(("other", "PWD=another-fake-pass"));
        var error = Record.Exception(() => Assert.Equal(scan.Parts, other.Parts));
        Assert.NotNull(error);
        foreach (var part in scan.Parts.Concat(other.Parts))
            Assert.True(!error.Message.Contains(part.Value, StringComparison.Ordinal), "Collection assertion printed a value.");
        foreach (var part in scan.Parts) Assert.True(!($"{scan}").Contains(part.Value, StringComparison.Ordinal), "Scan display printed a value.");
        Rejected(() => scan.LogLine("pin", Line(("RequestBytes", "fake-pass"))), "RequestBytes", "fake-pass");
    }
    [Fact]
    public void T8_error_and_success_bodies_follow_ownership()
    {
        var scan = Scan();
        Rejected(() => scan.Body("error", JsonSerializer.Serialize(new { code = "UnexpectedError", message = "fake-pass" })), "error.message", "fake-pass");
        scan.Body("refusal", JsonSerializer.Serialize(new { code = "InvalidJson", path = "$.query.Password=caller-member" }));
        Rejected(() => scan.Body("unknown", JsonSerializer.Serialize(new { code = "UnknownSource", path = "$.Password=caller-member" })), "unknown.path", "caller-member");
        var real = LeakScan.Real(("made up", "Server=fake-host;PWD=beef"));
        real.Body("incident", JsonSerializer.Serialize(new { code = "UnexpectedError", incidentId = "0123456789abcdef0123456789abbeef" }));
        Rejected(() => real.Body("incident", "{\"code\":\"UnexpectedError\",\"incidentId\":\"private-invalid-id\"}"), "incidentId", "private-invalid-id");
        var date = LeakScan.Real(("date", "PWD=2024"));
        date.Body("live rows", "{\"rows\":[{\"day\":\"2024-01-02\"}]}");
        Rejected(() => real.Body("rows", JsonSerializer.Serialize(new { rows = new[] { Connection } })), "a connection keyword form", Connection);
        Rejected(() => scan.Body("columns", "{\"columns\":[{\"title\":\"fake-pass\"}]}"), "columns.columns[0].title", "fake-pass");
        Rejected(() => scan.Body("member", "{\"fake-pass\":\"x\"}"), "member", "fake-pass");
        Rejected(() => scan.Body("error member", "{\"code\":\"UnexpectedError\",\"extra\":\"fake-pass\"}"), "error member.extra", "fake-pass");
    }
    [Fact]
    public void T8_guidance_keeps_only_its_incident_id_out_of_the_match()
    {
        var real = LeakScan.Real(("made up", "Server=fake-host;PWD=beef"));
        real.Guidance("guidance", string.Format(DataGateway.Mcp.ToolWire.Guidance, "0123456789abcdef0123456789abbeef"));
        Rejected(() => real.Guidance("guidance", string.Format(DataGateway.Mcp.ToolWire.Guidance, "beef0123")), "made up password", "beef0123");
        Rejected(() => real.Guidance("guidance", "Tell beef; report incident ID 0123456789abcdef0123456789abcdef."), "made up password");
        Rejected(() => real.Guidance("guidance", "report incident ID 0123456789abcdef0123456789abcdefbeef."), "made up password");
        Rejected(() => Scan().Guidance("guidance", string.Format(DataGateway.Mcp.ToolWire.Guidance, Id) + " Password=private-payload"),
            "a connection keyword form", "private-payload");
    }
    [Fact]
    public void T8_headers_follow_ownership()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "fake-pass");
        Rejected(() => Scan().Response("response", response, "{}"), "fixture password", "fake-pass");
        response.Content.Headers.Remove("Content-Type");
        response.Headers.TryAddWithoutValidation("Date", "2024-01-02");
        LeakScan.Real(("date", "PWD=2024")).Response("date", response, "{}");
        response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Password=private-payload");
        Rejected(() => Scan().Response("response", response, "{}"), "a connection keyword form", "private-payload");
        response.Headers.Remove("WWW-Authenticate");
        response.TrailingHeaders.TryAddWithoutValidation("trailer", "PWD=private-payload");
        Rejected(() => Scan().Response("trailing", response, "{}"), "a connection keyword form", "private-payload");
    }
    [Fact]
    public void T8_failure_and_framework_fields_follow_ownership()
    {
        var scan = Scan();
        scan.LogLine("file", Line(("Request", "not json Server=caller-typed"), ("SqlParameters", new Dictionary<string, string> { ["@p1"] = "Password=caller-typed" }), ("Path", "$.Password=caller-member")));
        Rejected(() => scan.LogLine("file", Line(("Request", "fake-pass"))), "file.Request", "fake-pass");
        Rejected(() => scan.LogLine("file", Line(("ExceptionText", "plain fake-pass"))), "file.ExceptionText", "fake-pass");
        var real = LeakScan.Real(("made up", "Server=fake-host;PWD=4821"));
        real.LogLine("caller", Line(("Caller", "fake-host\\someone")));
        Rejected(() => real.LogLine("caller", Line(("Caller", "Server=fake-host;PWD=4821"))), "caller.Caller", "fake-host");
        Rejected(() => scan.LogLine("file", Line(("extra", "fake-pass"))), "file.extra", "fake-pass");
        Rejected(() => scan.LogLine("file", Line(("Code", "UnknownSource"), ("Path", "Password=caller-member"))), "file.Path", "caller-member");
        foreach (var field in new[] { "RequestBytes", "SqlNumber", "SourceId", "RequestCut", "IncidentId" })
            Rejected(() => scan.LogLine("file", Line((field, "fake-pass"))), field, "fake-pass");
        real.LogLine("framework", Value(new { SourceContext = "Microsoft.Test", elapsed = "12.4821ms" }));
        Rejected(() => real.LogLine("framework", Value(new Dictionary<string, string> { ["SourceContext"] = "Microsoft.Test", ["@x"] = "12.4821ms" })), "framework.@x", "12.4821ms");
        real.Entry("entry", "Microsoft.Test", "12.4821ms", null, null);
        Rejected(() => real.Entry("entry", "Microsoft.Test", "ordinary", null, "12.4821ms"), "entry exception", "12.4821ms");
        var line = Line();
        var fields = JsonSerializer.Deserialize<Dictionary<string, object?>>(line.GetRawText())!;
        scan.Entry("entry", "DataGateway.Failure", LeakScan.Summary(line), fields, null);
        Rejected(() => scan.Entry("entry", "DataGateway.Failure", "private-mismatch", fields, null), "rendered summary", "private-mismatch");
    }
    [Fact]
    public void T9_console_and_stderr_are_pinned_or_scanned()
    {
        var scan = Scan();
        var line = Line();
        var summary = "[00:00:00 ERR] DataGateway.Failure: " + LeakScan.Summary(line);
        Assert.True(scan.Console("console", [summary], [line]).SetEquals([Id]));
        Rejected(() => scan.Console("console", [summary + "x"], [line]), "summary");
        Rejected(() => scan.Console("console", [summary.Replace(Id, new string('a', 32))], [line]), "summary file");
        Rejected(() => scan.Console("console", [summary, summary], [line]), "summary uniqueness");
        Rejected(() => scan.Console("console", [], [line]), "summary completeness");
        scan.Console("framework", ["[..] Microsoft.Hosting.Lifetime: Now listening on: http://127.0.0.1:5000"], []);
        Rejected(() => scan.Console("framework", ["continued fake-user"], []), "fixture user", "fake-user");
        var notice = "DataGateway.Failure: incident " + Id + ": the failure line could not be written (System.Exception).";
        scan.Notice("stderr", notice);
        Rejected(() => scan.Notice("stderr", notice + " fake-pass"), "fixture password", "fake-pass");
        Rejected(() => scan.Notice("stderr", "Serilog Password=private-payload"), "a connection keyword form", "private-payload");
        Rejected(() => scan.Notice("stderr", "DataGateway.Failure: incident private-invalid: detail"), "notice incident", "private-invalid");
        Assert.EndsWith("(null, null, null)", LeakScan.Summary(line), StringComparison.Ordinal);
        Assert.EndsWith("(null, null, 208)", LeakScan.Summary(Line(("SqlNumber", 208))), StringComparison.Ordinal);
    }
    [Fact]
    public void T10_armed_proves_every_eligible_part_is_found()
    {
        Scan().Armed();
        Rejected(() => LeakScan.Synthetic(("blank", " "), ("null", null)).Armed(), "holds no connection part");
        var integrated = LeakScan.Real(("integrated", "Server=fake-host;Integrated Security=true"));
        integrated.Armed();
        Assert.DoesNotContain(integrated.Parts, p => p.Kind is PartKind.User or PartKind.Password);
    }
    /// <summary>A framework warning written through the real host's file log is scanned when the lines are read: a
    /// secret in its exception or a keyword form in its properties fails, ordinary loopback and timing text passes.</summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task T11_framework_warning_channels_are_judged_by_the_real_file_reader(int kind)
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(ErrorLogTests.Catalog());
        await host.Start();
        var logger = host.App.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
        if (kind == 1) logger.LogWarning(new InvalidOperationException("quotes fake-log-source-password"), "Framework warning");
        if (kind == 2) logger.LogWarning("Framework warning {Property}", "Password=fake");
        if (kind == 3) logger.LogWarning("Framework warning {Host} {Timing}", "127.0.0.1", "12.4821ms");
        if (kind == 3) Assert.Empty(host.Lines());
        else Rejected(() => host.Lines(), kind == 1 ? "log source password" : "a connection keyword form", "fake-log-source-password", "Password=fake");
    }

    [Theory]
    [InlineData("fake-host", "fixture host")]
    [InlineData("fake&#45;host", "fixture host")]
    [InlineData("password=planted", "a connection keyword form")]
    [InlineData("password&#61;planted", "a connection keyword form")]
    public void Page_T10_text_judges_plain_and_encoded_html_once(string planted, string label)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
        Rejected(() => Scan().TextResponse("page", response, "<p>" + planted + "</p>"), label, "fake-host", "planted");
        Scan().TextResponse("clean page", response, "<p>DataGateway</p>");
        Scan().TextResponse("empty response", response, "");
    }

    [Fact]
    public void Page_T10_asset_identity_and_headers_print_labels_only()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        byte[] package = [0, 1, 2];
        Scan().Asset("asset", response, [0, 1, 2], package);
        Rejected(() => Scan().Asset("asset", response, [0, 1, 3], package), "package asset");
        response.Headers.Add("X-Probe", "fake-host");
        Rejected(() => Scan().Asset("asset", response, package, package), "fixture host", "fake-host");
        Rejected(() => Scan().TextResponse("page", response, "<p>clean</p>"), "fixture host", "fake-host");
        response.Headers.Remove("X-Probe");
        response.Headers.Add("X-Probe", "password=planted");
        Rejected(() => Scan().Asset("asset", response, package, package), "a connection keyword form", "planted");
        Rejected(() => Scan().TextResponse("page", response, ""), "a connection keyword form", "planted");
    }

    /// <summary>A real connection with no server, user or password keywords is still found whole, in rows too, so a
    /// connection that the keyword form would miss cannot leak through data.</summary>
    [Fact]
    public void Page_T11_real_whole_connection_is_distinctive_without_a_keyword_form()
    {
        const string bare = "Integrated Security=true;Encrypt=false";
        var scan = LeakScan.Real(("bare", bare));
        var part = Assert.Single(scan.Parts);
        Assert.Equal(PartKind.Connection, part.Kind);
        Assert.True(part.Distinctive);
        Assert.Contains("bare connection", scan.Found(bare, Rule.Foreign));
        var message = Rejected(() => scan.Body("rows", JsonSerializer.Serialize(new { rows = new[] { new { value = bare } } })), "bare connection", bare);
        Assert.DoesNotContain("a connection keyword form", message);
        var made = LeakScan.Real(("made up", "Server=fake-host;PWD=beef"));
        Rejected(() => made.Body("rows", JsonSerializer.Serialize(new { rows = new[] { new { value = "Server=fake-host;PWD=beef" } } })), "made up connection", "fake-host", "beef");
    }
}
