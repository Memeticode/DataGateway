using DataGateway.Failures;
using DataGateway.Data.SqlServer;
using System.Collections.Concurrent;
using System.Net;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DataGateway.Web.Configuration;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Core.Tests;
using DataGateway.Data.Catalog;
using DataGateway.Data.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlKata.JsonQuery;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class ErrorLogTests
{
    internal const string SourceConnection = "Server=tcp:127.0.0.1,1;Database=Synthetic;User ID=fake-log-source-user;Password=fake-log-source-password;Encrypt=false;Pooling=false;Connect Timeout=1;ConnectRetryCount=0";
    internal static readonly string[] Fields = ["IncidentId", "Caller", "Operation", "Request", "RequestBytes", "RequestCut", "Sql", "SqlParameters", "Code", "Path", "Message", "ExceptionType", "SqlNumber", "Step", "ReturnedCode", "SourceId", "SourceName", "DriverMessage", "ExceptionText"];
    private static readonly ConcurrentDictionary<string, byte> Incidents = new(StringComparer.Ordinal);

    [Theory]
    [InlineData("list", "array", "[]", "InvalidStructure", 400)]
    [InlineData("list", "json", "not json", "InvalidJson", 400)]
    [InlineData("list", "io", "{}", "CatalogUnavailable", 503)]
    [InlineData("list", "timeout", "{}", "CatalogUnavailable", 503)]
    [InlineData("list", "shape", "{}", "CatalogUnavailable", 503)]
    [InlineData("list", "cancel", "{}", "CatalogCancelled", 500)]
    [InlineData("describe", "member", "{\"source\":1}", "InvalidStructure", 400)]
    [InlineData("describe", "unknown", "{\"source\":\"absent\"}", "UnknownSource", 404)]
    [InlineData("describe", "off", "{\"source\":\"synthetic\"}", "InvalidCatalog", 500)]
    [InlineData("describe", "io", "{\"source\":\"synthetic\"}", "CatalogUnavailable", 503)]
    [InlineData("query", "member", "{}", "InvalidStructure", 400)]
    [InlineData("query", "utf8", "", "InvalidJson", 400)]
    [InlineData("query", "column", "{\"source\":\"synthetic\",\"query\":{\"select\":[\"absent\"]}}", "UnknownColumn", 400)]
    [InlineData("query", "seed", "{\"source\":\"synthetic\",\"query\":{}}", "InvalidCatalog", 500)]
    [InlineData("query", "stored", "{\"source\":\"synthetic\",\"query\":{}}", "InvalidCatalog", 500)]
    [InlineData("query", "connect", "{\"source\":\"synthetic\",\"query\":{}}", "SourceUnavailable", 503)]
    [InlineData("query", "timeout", "{\"source\":\"synthetic\",\"query\":{\"filters\":[{\"col\":\"text\",\"op\":\"eq\",\"value\":\"Password=caller-typed\"},{\"col\":\"number\",\"op\":\"gte\",\"value\":12.5},{\"col\":\"day\",\"op\":\"gte\",\"value\":\"2024-01-02\"}],\"limit\":1}}", "QueryTimeout", 504)]
    [InlineData("query", "io", "{\"source\":\"synthetic\",\"query\":{}}", "SourceUnavailable", 503)]
    [InlineData("query", "unknown", "{\"source\":\"absent\",\"query\":{}}", "UnknownSource", 404)]
    public async Task T4_T5_T11_every_failure_has_one_complete_record(string operation, string kind, string body, string code, int status)
    {
        using var settings = new ErrorLogSettingsScope();
        var runner = kind == "connect" ? new QueryRunner(new SqlServerEngine()) : Runner(kind);
        await using var host = new LoggedHost(Catalog(operation == "query" && kind == "io" ? "ok" : kind, operation == "list"),
            kind == "connect" ? new SqlServerEngine() : Engine(kind));
        await host.Start();
        var catalog = host.App.Services.GetRequiredService<GatewayRegistry>().Find("DataGateway.Web")!.CreateCatalog(host.App.Services);
        byte[] bytes = kind == "utf8" ? [255, 254] : Encoding.UTF8.GetBytes(body);
        using var response = await host.Client.PostAsync(Route(operation), new ByteArrayContent(bytes));
        Assert.Equal(status, (int)response.StatusCode);
        using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var line = Assert.Single(host.Lines());
        Check(line, answer.RootElement, operation, bytes, code);
        Assert.Equal("Error", line.GetProperty("@l").GetString());
        var runs = operation == "query" && kind is "stored" or "connect" or "timeout" or "io";
        if (runs)
        {
            using var request = JsonDocument.Parse(bytes);
            var source = Contract(kind);
            var expected = new SqlServerEngine().Compile(QueryBuilding.Validate(source,
                request.RootElement.GetProperty("query").GetRawText(), host.App.Services.GetRequiredService<JsonQueryLimits>() with { MaxLimit = Contract().Source.RowCap }).Value);
            AssertSql(line, expected);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, line.GetProperty("Sql").ValueKind);
            Assert.Equal(JsonValueKind.Null, line.GetProperty("SqlParameters").ValueKind);
        }
        if (status != 500)
            foreach (var field in new[] { "code", "path", "message" })
                Assert.Equal(answer.RootElement.GetProperty(field).GetString(), line.GetProperty(char.ToUpperInvariant(field[0]) + field[1..]).GetString());
        else Assert.Equal("InternalError", answer.RootElement.GetProperty("code").GetString());

        CatalogFailure expectedFailure;
        if (operation == "list" && kind is "io" or "timeout" or "shape" or "cancel")
            expectedFailure = (await catalog.ListAsync(CancellationToken.None)).Failure!;
        else if (operation != "list" && kind is "unknown" or "off" || operation == "describe" && kind == "io")
            expectedFailure = (await catalog.SourceAsync(kind == "unknown" ? "absent" : "synthetic", CancellationToken.None)).Failure!;
        else if (runs)
        {
            using var request = JsonDocument.Parse(bytes);
            var source = Contract(kind);
            var query = new SqlServerEngine().Compile(QueryBuilding.Validate(source, request.RootElement.GetProperty("query").GetRawText(), host.App.Services.GetRequiredService<JsonQueryLimits>() with { MaxLimit = Contract().Source.RowCap }).Value);
            expectedFailure = (await runner.RunAsync(source, query, CancellationToken.None)).Failure!;
        }
        else if (kind == "seed")
            expectedFailure = QueryBuilding.Validate(Contract("seed"), "{}", host.App.Services.GetRequiredService<JsonQueryLimits>() with { MaxLimit = Contract().Source.RowCap }).Failure!;
        else if (kind == "column")
            expectedFailure = QueryAnswer.RootPath(QueryBuilding.Validate(Contract(), "{\"select\":[\"absent\"]}", host.App.Services.GetRequiredService<JsonQueryLimits>() with { MaxLimit = Contract().Source.RowCap }).Failure!);
        else expectedFailure = new(code, answer.RootElement.GetProperty("path").GetString()!, answer.RootElement.GetProperty("message").GetString()!)
            { Step = "request", ExceptionType = kind is "json" or "utf8" ? "JsonException" : null };
        if (operation == "list" && kind is "io" or "timeout" or "shape")
            expectedFailure = new("CatalogUnavailable", "$", "Cannot read the catalog. Check catalog availability and the configured catalog connection. Tell the user, give the incidentId and stop; do not retry.")
            { Step = "catalog", ExceptionType = kind == "io" ? "IOException" : kind == "timeout" ? "TimeoutException" : "InvalidCastException" };
        else if (operation != "list" && kind == "unknown")
            expectedFailure = expectedFailure with { SourceName = "absent", Step = "catalog" };
        foreach (var field in new[] { "Code", "Path", "Message", "ExceptionType", "SqlNumber", "Step" })
            {
            var expected = typeof(CatalogFailure).GetProperty(field)!.GetValue(expectedFailure);
            var actual = line.GetProperty(field);
            if (expected is null) Assert.Equal(JsonValueKind.Null, actual.ValueKind);
            else if (expected is int number) Assert.Equal(number, actual.GetInt32());
            else Assert.Equal((string)expected, actual.GetString());
        }
        if (kind == "connect")
        {
            Assert.Equal("connect", line.GetProperty("Step").GetString());
            Assert.Equal("SqlException", line.GetProperty("ExceptionType").GetString());
        }
        Safe(string.Join("\n", Directory.GetFiles(host.DirectoryPath, "*.jsonl").Select(LoggedHost.ReadText)));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("describe")]
    [InlineData("query")]
    public async Task T6_aborted_operation_logs_warning(string operation)
    {
        using var settings = new ErrorLogSettingsScope();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = Catalog();
        if (operation == "list") catalog.List = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return []; };
        if (operation == "describe") catalog.Detail = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return null; };
        var runner = FakeRows.Engine(new SqlServerEngine(), FakeRows.From(async (_, _, connected, token) =>
        { connected(); entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return []; }));
        await using var host = new LoggedHost(catalog, runner);
        await host.Start();
        using var abort = new CancellationTokenSource();
        var body = Body(operation);
        var pending = host.Client.PostAsync(Route(operation), new StringContent(body), abort.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (host.Lines().Length == 0) await Task.Delay(20, timeout.Token);
        var line = Assert.Single(host.Lines());
        Assert.Equal("Warning", line.GetProperty("@l").GetString());
        Assert.Equal(operation == "query" ? "QueryCancelled" : "CatalogCancelled", line.GetProperty("Code").GetString());
        Assert.Equal(body, line.GetProperty("Request").GetString());
        Assert.Equal(operation == "query" ? JsonValueKind.String : JsonValueKind.Null, line.GetProperty("Sql").ValueKind);
    }

    [Fact]
    public async Task T7_T8_request_text_bounds_bom_and_injection()
    {
        using var settings = new ErrorLogSettingsScope();
        await using (var host = new LoggedHost(Catalog(list: true), Engine()))
        {
            await host.Start();
            using var bom = await host.Client.PostAsync(Route("list"), new ByteArrayContent([239, 187, 191, 123, 125]));
            Assert.Equal(200, (int)bom.StatusCode);
            Assert.Empty(host.Lines());
        }
        await using (var host = new LoggedHost(Catalog(), Engine()))
        {
            await host.Start();
            var malicious = "\n\r\"</script><img src=x>\u00e9 Server=caller-typed {\"@t\":\"2000-01-01T00:00:00Z\",\"@mt\":\"forged\"}";
            foreach (var query in new[] { new Dictionary<string, object> { [malicious] = malicious }, new Dictionary<string, object> { ["filters"] = new[] { new Dictionary<string, object> { ["col"] = "text", ["op"] = "eq", ["value"] = "x", [malicious] = malicious } } } })
            {
                var body = JsonSerializer.Serialize(new { source = "synthetic", query }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                using var response = await host.Client.PostAsync(Route("query"), new StringContent(body));
                Assert.Equal(400, (int)response.StatusCode);
                var line = host.Lines().Last();
                Assert.Equal(body, line.GetProperty("Request").GetString());
                if (query.ContainsKey("filters")) Assert.Contains(malicious, line.GetProperty("Path").GetString());
                else Assert.Equal("$.query", line.GetProperty("Path").GetString());
                Assert.False(line.GetProperty("RequestCut").GetBoolean());
                foreach (var field in line.EnumerateObject().Where(p => p.Name is not ("Request" or "Path")))
                    Assert.DoesNotContain("forged", field.Value.GetRawText());
            }
            Assert.Equal(2, host.Lines().Length);
            var failuresBefore = host.Lines().Length;
            using var huge = await host.Client.PostAsync(Route("query"), new StringContent(new string('x', 3 * 1024 * 1024)));
            Assert.Equal(400, (int)huge.StatusCode);
            using var answer = JsonDocument.Parse(await huge.Content.ReadAsStringAsync());
            Assert.Equal("InvalidJson", answer.RootElement.GetProperty("code").GetString());
            var failuresAfter = host.Lines();
            Assert.Equal(failuresBefore + 1, failuresAfter.Length);
            var cut = failuresAfter.Last();
            Assert.Equal("InvalidJson", cut.GetProperty("Code").GetString());
            Assert.Equal(66560, cut.GetProperty("Request").GetString()!.Length);
            Assert.Equal(3 * 1024 * 1024, cut.GetProperty("RequestBytes").GetInt32());
            Assert.True(cut.GetProperty("RequestCut").GetBoolean());
            Assert.True(cut.GetRawText().Length < 70000);
        }
        await using (var host = new LoggedHost(Catalog(), settings: new Dictionary<string, string?> { ["DataGateway:Limits:MaxJsonLength"] = "40" }))
        {
            await host.Start();
            using var response = await host.Client.PostAsync(Route("query"), new StringContent(new string('x', 5000)));
            var line = Assert.Single(host.Lines());
            Assert.Equal(1064, line.GetProperty("Request").GetString()!.Length);
            Assert.Equal(5000, line.GetProperty("RequestBytes").GetInt32());
            Assert.True(line.GetProperty("RequestCut").GetBoolean());
        }
    }

    [Theory]
    [InlineData("%%%", "/sources/list", "list")]
    [InlineData("bm90LWEtdG9rZW4=", "/sources/list", "list")]
    [InlineData("%%%", "/not-a-route", null)]
    public async Task T5_T9_failed_sign_in_has_id_and_no_body_or_token(string token, string route, string? operation)
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(synthetic: false);
        await host.Start();
        using (var anonymous = await host.Client.PostAsync(route, new StringContent("{}")))
        {
            Assert.Equal(401, (int)anonymous.StatusCode);
            Assert.Equal("{\"code\":\"NotAuthenticated\",\"path\":\"$\",\"message\":\"Sign in with Windows to call DataGateway: send the request with Windows credentials (HTTP Negotiate).\"}", await anonymous.Content.ReadAsStringAsync());
            Assert.Empty(host.Lines());
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = new StringContent("secret-body") };
        request.Headers.TryAddWithoutValidation("Authorization", "Negotiate " + token);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(401, (int)response.StatusCode);
        using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var line = Assert.Single(host.Lines());
        Check(line, answer.RootElement, operation, null, "NotAuthenticated", "anonymous");
        Assert.Equal("sign-in", line.GetProperty("Step").GetString());
        Assert.False(string.IsNullOrEmpty(line.GetProperty("ExceptionType").GetString()));
        foreach (var text in Directory.GetFiles(host.DirectoryPath).Select(LoggedHost.ReadText))
            foreach (var hidden in new[] { token, "not-a-token", "secret-body" }) Assert.DoesNotContain(hidden, text);
    }

    [Fact]
    public async Task T9_handshake_continuation_leaves_no_file_failure()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(synthetic: false, configure: SyntheticSignIn.UseHandshakeLeg);
        await host.Start();
        using var response = await host.Client.PostAsync("/sources/list", new StringContent("{}"));
        Assert.Equal(401, (int)response.StatusCode);
        Assert.Equal("Negotiate synthetic-leg", Assert.Single(response.Headers.GetValues("WWW-Authenticate")));
        Assert.Equal("{\"code\":\"NotAuthenticated\",\"path\":\"$\",\"message\":\"Sign in with Windows to call DataGateway: send the request with Windows credentials (HTTP Negotiate).\"}", await response.Content.ReadAsStringAsync());
        Assert.Empty(host.Lines());
    }

    [Fact]
    public async Task T10_eight_concurrent_requests_keep_their_own_context()
    {
        using var settings = new ErrorLogSettingsScope();
        var arrived = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = FakeRows.Engine(new SqlServerEngine(), FakeRows.From(async (_, _, connected, token) =>
        {
            connected();
            if (Interlocked.Increment(ref arrived) == 8) barrier.SetResult();
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            throw new IOException("fake-driver-secret");
        }));
        await using var host = new LoggedHost(Catalog(), runner);
        await host.Start();
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            var body = JsonSerializer.Serialize(new { source = "synthetic", query = new { filters = new[] { new { col = "text", op = "eq", value = "marker-" + i } } } });
            using var request = new HttpRequestMessage(HttpMethod.Post, "/query") { Content = new StringContent(body) };
            request.Headers.Add("X-Synthetic-User", "caller-" + i);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(503, (int)response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (Caller: "caller-" + i, Body: body, Answer: json.RootElement.Clone());
        }));
        var lines = host.Lines();
        Assert.Equal(8, lines.Length);
        foreach (var reply in replies)
            Check(Assert.Single(lines, l => l.GetProperty("Caller").GetString() == reply.Caller), reply.Answer, "query", Encoding.UTF8.GetBytes(reply.Body), "SourceUnavailable", reply.Caller);
        Assert.Equal(8, lines.Select(l => l.GetProperty("IncidentId").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task T12_successes_leave_no_failure_record()
    {
        using var settings = new ErrorLogSettingsScope();
        foreach (var operation in new[] { "list", "describe", "query" })
        {
            await using var host = new LoggedHost(Catalog(list: operation == "list"), Engine());
            await host.Start();
            using var response = await host.Client.PostAsync(Route(operation), new StringContent(Body(operation)));
            Assert.Equal(200, (int)response.StatusCode);
            Assert.Empty(host.Lines());
        }
    }

    [Fact]
    public async Task T13_settings_create_directories_roll_and_require_a_sink()
    {
        using var settings = new ErrorLogSettingsScope();
        using var shipped = JsonDocument.Parse(File.ReadAllText(TestFiles.InRepo("DataGateway.Web/appsettings.json")));
        var serilog = shipped.RootElement.GetProperty("Serilog");
        Assert.Equal(new[] { "Console", "File" }, serilog.GetProperty("WriteTo").EnumerateObject().Select(p => p.Name));
        var args = serilog.GetProperty("WriteTo").GetProperty("File").GetProperty("Args");
        Assert.Equal("logs/datagateway-.jsonl", args.GetProperty("path").GetString());
        Assert.Equal("Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact", args.GetProperty("formatter").GetString());
        Assert.Equal("Warning", args.GetProperty("restrictedToMinimumLevel").GetString());
        Assert.Equal("Day", args.GetProperty("rollingInterval").GetString());
        Assert.Equal(1073741824, args.GetProperty("fileSizeLimitBytes").GetInt64());
        Assert.True(args.GetProperty("rollOnFileSizeLimit").GetBoolean());
        Assert.Equal(31, args.GetProperty("retainedFileCountLimit").GetInt32());
        Assert.Equal(7, args.EnumerateObject().Count());
        Assert.Equal("[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}", serilog.GetProperty("WriteTo").GetProperty("Console").GetProperty("Args").GetProperty("outputTemplate").GetString());
        Assert.Equal(new[] { "Serilog.Sinks.Console", "Serilog.Sinks.File" }, serilog.GetProperty("Using").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("Information", serilog.GetProperty("MinimumLevel").GetProperty("Default").GetString());
        await using var host = new LoggedHost(Catalog(), settings: new Dictionary<string, string?> { ["Serilog:WriteTo:File:Args:fileSizeLimitBytes"] = "2000" });
        await host.Start();
        Assert.Equal("Catalog", Assert.Single(host.App.Configuration.GetSection("ConnectionStrings").GetChildren()).Key);
        for (var i = 0; i < 8; i++)
        {
            using var response = await host.Client.PostAsync("/query", new StringContent("not json " + i));
            Assert.Equal(400, (int)response.StatusCode);
        }
        Assert.Equal(8, host.Lines().Length);
        Assert.Contains(Directory.GetFiles(host.DirectoryPath), p => p.Contains("_001", StringComparison.Ordinal));
        var missing = Path.Combine(host.DirectoryPath, "empty-root");
        Directory.CreateDirectory(missing);
        var error = Assert.Throws<OptionsValidationException>(() => Program.CreateApplication(new WebApplicationOptions
        { ContentRootPath = missing, EnvironmentName = "Production", ApplicationName = typeof(Program).Assembly.GetName().Name, Args = [] }));
        Assert.Equal(DataGatewayLog.MissingSink, error.Message);
        var directConfig = new ConfigurationBuilder().Build();
        Assert.Equal(DataGatewayLog.MissingSink, Assert.Throws<OptionsValidationException>(() => DataGatewayLog.Create(directConfig)).Message);
    }

    [Fact]
    public async Task T13_absolute_path_creates_nested_missing_directories()
    {
        using var settings = new ErrorLogSettingsScope();
        var scratch = TestFiles.InRepo("tests/DataGateway.Web.Tests/obj/error-log/" + Guid.NewGuid());
        var nested = Path.Combine(scratch, "missing", "nested");
        Assert.False(Directory.Exists(scratch));
        try
        {
            await using var host = new LoggedHost(Catalog(), settings: new Dictionary<string, string?>
                { ["Serilog:WriteTo:File:Args:path"] = Path.Combine(nested, "datagateway-.jsonl") });
            await host.Start();
            using var response = await host.Client.PostAsync("/query", new StringContent("{}"));
            Assert.Equal(400, (int)response.StatusCode);
            using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(answer.RootElement.GetProperty("incidentId").GetString(), Assert.Single(LoggedHost.ReadLines(nested)).GetProperty("IncidentId").GetString());
            Assert.False(Directory.Exists(host.DirectoryPath));
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
    }

    [Fact]
    public async Task T17_clearing_providers_drops_serilog_and_preserves_all_fields()
    {
        using var settings = new ErrorLogSettingsScope();
        var memory = new MemoryLogs();
        await using var host = new LoggedHost(Catalog(), configure: b => { b.Logging.ClearProviders(); b.Logging.AddProvider(memory); });
        await host.Start();
        using var response = await host.Client.PostAsync("/query", new StringContent("{}"));
        using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = Assert.Single(memory.Entries, e => e.Fields.ContainsKey("IncidentId"));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(Fields.Append("{OriginalFormat}"), entry.Fields.Keys);
        Assert.Equal(answer.RootElement.GetProperty("incidentId").GetString(), entry.Fields["IncidentId"]);
        Assert.False(Directory.Exists(host.DirectoryPath));
    }

    [Fact]
    public void T18_pure_rules_and_structured_state()
    {
        Assert.Equal(new RequestText("{}", 2, false), RequestText.From("{}"u8, 10));
        Assert.Equal(new RequestText("xxxxxxxxxx", 11, true), RequestText.From("xxxxxxxxxxx"u8, 10));
        Assert.Equal(new RequestText(new string('\u00e9', 10), 20, false), RequestText.From(Encoding.UTF8.GetBytes(new string('\u00e9', 10)), 10));
        Assert.Equal(new RequestText("", 4, true), RequestText.From(Encoding.UTF8.GetBytes("\U0001f600"), 1));
        Assert.Equal(new RequestText("\ufffd\ufffd", 2, false), RequestText.From([255, 254], 10));
        Assert.Equal(new RequestText("", 0, false), RequestText.From([], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RequestText.From([], 0));
        using var factory = LoggerFactory.Create(_ => { });
        foreach (var (input, output) in new[] { (40, 1064), (65536, 66560), (int.MaxValue, int.MaxValue) })
            Assert.Equal(output, new FailureLog(factory, new JsonQueryLimits { MaxJsonLength = input }).RequestCharacters);
        Assert.Equal(LogLevel.Warning, FailureLog.LevelOf(true));
        Assert.Equal(LogLevel.Error, FailureLog.LevelOf(false));
        Assert.Equal("anonymous", FailureLog.CallerOf(new ClaimsPrincipal()));
        Assert.Equal("DOMAIN\\name", FailureLog.CallerOf(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "DOMAIN\\name")], "test"))));
        var line = new FailureLine("abc", "DOMAIN\\name", "query", null, new("SourceUnavailable", "$.source", "Fix it.") { SqlNumber = 208 }, null);
        Assert.Equal(Fields.Append("{OriginalFormat}"), line.Select(p => p.Key));
        Assert.Equal(FailureLog.Template, line.Last().Value);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal("Failure abc: query for DOMAIN\\name: SourceUnavailable: Fix it. (null, null, 208)", line.Summary());
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public async Task T12_like_logs_original_request_and_executed_parameter()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(Catalog(), Engine("io"));
        await host.Start();
        const string body = """{ "source":"synthetic", "query":{"filters":[{"col":"text","op":"like","value":"a_b"}]}}""";
        using var response = await host.Client.PostAsync("/query", new StringContent(body));
        var route = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var client = await McpWire.Connect(host.Client);
        var tool = await McpWire.Call(client, "query_source", body);
        Assert.True(tool.IsError);
        McpWire.SameDocument(route, McpWire.Text(tool));
        foreach (var output in new[] { route, McpWire.Text(tool) })
        {
            Assert.DoesNotContain("a_b", output);
            Assert.DoesNotContain("a[_]b", output);
        }
        var lines = host.Lines();
        Assert.Equal(2, lines.Length);
        Assert.Equal(body, lines[0].GetProperty("Request").GetString());
        foreach (var line in lines)
        {
            var request = line.GetProperty("Request").GetString()!;
            Assert.Contains("a_b", request);
            Assert.DoesNotContain("a[_]b", request);
            Assert.Equal("a[_]b", line.GetProperty("SqlParameters").GetProperty("@p1").GetString());
            using var parsed = JsonDocument.Parse(request);
            AssertSql(line, new SqlServerEngine().Compile(QueryBuilding.Validate(Contract(),
                parsed.RootElement.GetProperty("query").GetRawText(), QueryLimits.ForRowCap(500)).Value));
        }
    }
    internal static void Check(JsonElement line, JsonElement answer, string? operation, byte[]? body, string code, string caller = "synthetic-user")
    {
        Assert.Equal(new[] { "code", "path", "message", "incidentId" }, answer.EnumerateObject().Select(p => p.Name));
        var incident = answer.GetProperty("incidentId").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", incident);
        Assert.Equal('4', incident[12]);
        Assert.Contains(incident[16], "89ab");
        Assert.True(Incidents.TryAdd(incident, 0), "Duplicate incident ID.");
        Assert.Equal(incident, line.GetProperty("IncidentId").GetString());
        Assert.Equal(caller, line.GetProperty("Caller").GetString());
        Assert.Equal(operation, line.GetProperty("Operation").GetString());
        Assert.Equal(code, line.GetProperty("Code").GetString());
        Assert.Equal(FailureLog.Template, line.GetProperty("@mt").GetString());
        Assert.True(DateTimeOffset.TryParse(line.GetProperty("@t").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _));
        Assert.Equal("DataGateway.Web", line.GetProperty("Gateway").GetString());
        var context = new[] { "SourceContext", "RequestId", "RequestPath", "ConnectionId", "TraceId", "SpanId", "ParentId", "Scope", "Gateway" };
        foreach (var property in line.EnumerateObject()) Assert.True(Fields.Contains(property.Name) || context.Contains(property.Name) || property.Name.StartsWith('@'), "Unexpected property: " + property.Name);
        foreach (var field in Fields) Assert.True(line.TryGetProperty(field, out _), "Missing property: " + field);
        if (body is null)
            foreach (var field in new[] { "Request", "RequestBytes", "RequestCut" }) Assert.Equal(JsonValueKind.Null, line.GetProperty(field).ValueKind);
        else
        {
            Assert.Equal(Encoding.UTF8.GetString(body), line.GetProperty("Request").GetString());
            Assert.Equal(body.Length, line.GetProperty("RequestBytes").GetInt32());
            Assert.False(line.GetProperty("RequestCut").GetBoolean());
        }
    }

    internal static void AssertSql(JsonElement line, CompiledQuery query)
    {
        Assert.Equal(query.Sql, line.GetProperty("Sql").GetString());
        var parameters = line.GetProperty("SqlParameters");
        Assert.Equal(query.Parameters.Keys.Order(), parameters.EnumerateObject().Select(p => p.Name).Order());
        foreach (var (name, value) in query.Parameters)
        {
            var actual = parameters.GetProperty(name);
            if (value is DateTime date) Assert.Equal(date.ToString("O", CultureInfo.InvariantCulture), actual.GetString());
            else if (value is string text) Assert.Equal(text, actual.GetString());
            else Assert.Equal(Convert.ToDecimal(value, CultureInfo.InvariantCulture), actual.GetDecimal());
        }
    }

    internal static string Route(string operation) => operation == "query" ? "/query" : "/sources/" + operation;
    internal static string Body(string operation) => operation switch { "list" => "{}", "describe" => "{\"source\":\"synthetic\"}", _ => "{\"source\":\"synthetic\",\"query\":{}}" };
    internal static HostCatalogState Catalog(string kind = "ok", bool list = false) => HostCatalogState.Raw(
        _ => kind switch
        {
            "io" when list => throw new IOException("fake-driver-secret"),
            "timeout" when list => throw new TimeoutException("fake-driver-secret"),
            "cancel" when list => throw new OperationCanceledException("fake-driver-secret"),
            "shape" when list => Task.FromResult<IReadOnlyList<dynamic>>([new Dictionary<string, object?>()]),
            _ => Task.FromResult<IReadOnlyList<dynamic>>([new Dictionary<string, object?> { ["name"] = "synthetic", ["title"] = "Synthetic", ["description"] = "Synthetic", ["grain"] = "One row", ["row_cap"] = 500 }])
        },
        (_, _) => kind switch
        {
            "unknown" => Task.FromResult<(IReadOnlyList<dynamic>, IReadOnlyList<dynamic>)>(([], [new Dictionary<string, object?> { ["name"] = "synthetic" }])),
            "io" => throw new IOException("fake-driver-secret"),
            _ => Task.FromResult<(IReadOnlyList<dynamic>, IReadOnlyList<dynamic>)>((Rows(kind), []))
        });
    internal static DataGateway.Data.IDatabaseEngine Engine(string kind = "ok") => FakeRows.Engine(new SqlServerEngine(), FakeRows.From((_, _, connected, _) =>
    {
        connected();
        return kind switch
        {
            "io" => throw new IOException("fake-driver-secret"), "timeout" => throw new TimeoutException("fake-driver-secret"),
            _ => Task.FromResult<IReadOnlyList<dynamic>>([])
        };
    }));

    internal static QueryRunner Runner(string kind = "ok") =>FakeRows.Runner(new SqlServerEngine(), FakeRows.From((_, _, connected, _) =>
    {
        connected();
        return kind switch
        {
            "io" => throw new IOException("fake-driver-secret"), "timeout" => throw new TimeoutException("fake-driver-secret"),
            _ => Task.FromResult<IReadOnlyList<dynamic>>([])
        };
    }));
    internal static Dictionary<string, object?>[] Rows(string kind = "ok") => Columns().Select(c => new Dictionary<string, object?>
    {
        ["id"] = 7319, ["object_name"] = kind == "seed" ? "dbo.not a view" : "hidden_schema.synthetic_view", ["connection_string"] = kind == "stored" ? "malformed-secret-connection" : SourceConnection,
        ["name"] = "synthetic", ["title"] = "Synthetic", ["description"] = "Synthetic", ["grain"] = "One row", ["row_cap"] = 500, ["query_timeout_seconds"] = 1,
        ["column_name"] = c.ColumnName, ["ordinal"] = c.Ordinal, ["column_title"] = c.Title, ["column_description"] = c.Description,
        ["data_type"] = c.DataType, ["unit"] = null, ["magnitude"] = null, ["display_format"] = null,
        ["can_select"] = kind != "off", ["can_filter"] = kind != "off", ["can_sort"] = kind != "off", ["can_group"] = kind != "off", ["can_aggregate"] = kind != "off"
    }).ToArray();
    private static DataSourceColumnRow[] Columns() => [new("text", 1, "Text", "Synthetic text", "text", null, null, null, true, true, true, true, true),
        new("number", 2, "Number", "Synthetic number", "number", null, null, null, true, true, true, true, true), new("day", 3, "Day", "Synthetic day", "date", null, null, null, true, true, true, true, true)];
    internal static SourceContract Contract(string kind = "ok", int queryTimeoutSeconds = 1) => CatalogMapping.Map(new(new DataSourceRow(kind == "seed" ? "dbo.not a view" : "hidden_schema.synthetic_view", kind == "stored" ? "malformed-secret-connection" : SourceConnection)
        { QueryTimeoutSeconds = queryTimeoutSeconds, Id = 7319, Name = "synthetic", Title = "Synthetic", Description = "Synthetic", Grain = "One row", RowCap = 500, IsActive = true }, Columns())).Value;
    [Fact]
    public async Task Catalog_driver_message_is_suppressed()
    {
        using var settings = new ErrorLogSettingsScope();
        const string planted = "planted catalog driver detail";
        var catalog = new HostCatalogState { List = _ => throw new IOException(planted) };
        await using var host = new LoggedHost(catalog);
        await host.Start();
        using var response = await host.Client.PostAsync("/sources/list", new StringContent("{}"));
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var line = Assert.Single(host.Lines());
        Assert.Equal("catalog", line.GetProperty("Step").GetString());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("DriverMessage").ValueKind);
        Assert.DoesNotContain(planted, string.Join("\n", host.AllLines().Select(l => l.GetRawText())));
        Safe(string.Join("\n", host.AllLines().Select(l => l.GetRawText())));
    }

    internal static void Safe(string text)
    {
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var json = JsonDocument.Parse(raw);
            LoggedHost.Scan.LogLine("driver diagnostics", json.RootElement);
        }
    }
    private sealed class MemoryLogs : ILoggerProvider
    {
        internal ConcurrentQueue<(LogLevel Level, Dictionary<string, object?> Fields)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Recorder(this);
        public void Dispose() { }
        private sealed class Recorder(MemoryLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            { if (state is IEnumerable<KeyValuePair<string, object?>> pairs) owner.Entries.Enqueue((logLevel, pairs.ToDictionary(p => p.Key, p => p.Value))); }
        }
    }
}
