using DataGateway.Data.SqlServer;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DataGateway.Failures;
using DataGateway.Routes;
using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using DataGateway.Core.Queries;
using DataGateway.Data.Catalog;
using DataGateway.Data.Queries;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlKata.JsonQuery;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class UnexpectedErrorTests
{
    internal const string Message = "An unexpected error stopped DataGateway from completing the request. Tell the user, give the incidentId and stop; do not retry.";

    [Theory]
    [InlineData("list", false)]
    [InlineData("describe", false)]
    [InlineData("query", false)]
    [InlineData("list", true)]
    [InlineData("describe", true)]
    [InlineData("query", true)]
    public async Task T2_shell_and_answer_faults_keep_request_and_query(string operation, bool answerFault)
    {
        using var settings = new ErrorLogSettingsScope();
        var fault = new AnswerFault([AnswerType(operation)], () => new SyntheticFault("synthetic fault " + operation));
        if (answerFault) fault.Arm();
        var catalog = ErrorLogTests.Catalog(list: operation == "list");
        if (!answerFault && operation == "list") catalog.List = _ => throw new SyntheticFault("synthetic fault list");
        if (!answerFault && operation == "describe") catalog.Detail = (_, _) => throw new SyntheticFault("synthetic fault describe");
        await using var host = new LoggedHost(catalog, answerFault ? ErrorLogTests.Engine() : FaultEngine(new SyntheticFault("synthetic fault " + operation)),
            configure: b => b.Services.AddSingleton(new global::DataGateway.GatewayJson(fault)));
        await host.Start();
        var request = ErrorLogTests.Body(operation);
        using var response = await host.Client.PostAsync(ErrorLogTests.Route(operation), new StringContent(request));
        var catalogFault = !answerFault && operation != "query";
        var body = catalogFault ? JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()) : await Exact(response);
        var line = Assert.Single(host.Lines());
        if (catalogFault)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            ErrorLogTests.Check(line, body, operation, Encoding.UTF8.GetBytes(request), "CatalogUnavailable");
            Assert.Equal(operation == "list" ? "$" : "$.source", body.GetProperty("path").GetString());
            Assert.Equal("Cannot read the catalog. Check catalog availability and the configured catalog connection. Tell the user, give the incidentId and stop; do not retry.", body.GetProperty("message").GetString());
            Nulls(line, "ExceptionText", "DriverMessage", "SqlNumber");
        }
        else
        {
            Check(host, line, body, operation, request);
            var outer = line.GetProperty("ExceptionText").GetString()!;
            Assert.Contains(typeof(SyntheticFault).FullName!, outer);
            Assert.Contains("synthetic fault " + operation, outer);
            Assert.Contains("   at ", outer);
        }
        Assert.Equal("SyntheticFault", line.GetProperty("ExceptionType").GetString());
        if (operation == "query") ErrorLogTests.AssertSql(line, Compile(host));
        else Nulls(line, "Sql", "SqlParameters");
        using var healthy = await host.Client.PostAsync(ErrorLogTests.Route(operation), new StringContent(request));
        if (answerFault) { Assert.Equal(HttpStatusCode.OK, healthy.StatusCode); Assert.Single(host.Lines()); }
        NoFrameworkError(host);
    }

    [Fact]
    public async Task T3_route_mapping_fault_keeps_datagateway_message()
    {
        using var settings = new ErrorLogSettingsScope();
        var runner = FakeRows.Engine(new SqlServerEngine(), FakeRows.From((_, _, connected, _) =>
        { connected(); return Task.FromResult<IReadOnlyList<dynamic>>([new Dictionary<string, object?> { ["text"] = "only one" }]); }));
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), runner);
        await host.Start();
        using var response = await host.Client.PostAsync("/query", new StringContent(ErrorLogTests.Body("query")));
        var body = await Exact(response);
        var line = Assert.Single(host.Lines());
        Check(host, line, body, "query", ErrorLogTests.Body("query"));
        Assert.Contains("System.InvalidOperationException", line.GetProperty("ExceptionText").GetString());
        Assert.Contains("The query row does not match its output columns.", line.GetProperty("ExceptionText").GetString());
        ErrorLogTests.AssertSql(line, Compile(host));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("describe")]
    [InlineData("query")]
    public async Task T4_body_fault_has_endpoint_but_no_body(string operation)
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), configure: b =>
            b.Services.AddSingleton<IStartupFilter>(new BodyFault(new SyntheticFault("synthetic read fault"))));
        await host.Start();
        using var response = await host.Client.PostAsync(ErrorLogTests.Route(operation), new StringContent(ErrorLogTests.Body(operation)));
        var body = await Exact(response);
        var line = Assert.Single(host.Lines());
        Check(host, line, body, operation, null);
        Nulls(line, "Sql", "SqlParameters");
    }

    [Theory]
    [InlineData("/sources/list", "list")]
    [InlineData("/not-a-route", null)]
    public async Task T5_sign_in_fault_is_inside_routing(string route, string? operation)
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(configure: SyntheticSignIn.UseFaulting);
        await host.Start();
        using var response = await host.Client.PostAsync(route, new StringContent("{}"));
        var body = await Exact(response);
        var line = Assert.Single(host.Lines());
        Check(host, line, body, operation, null, "anonymous");
    }

    [Fact]
    public async Task T6_started_answer_is_aborted_and_logged_once()
    {
        using var settings = new ErrorLogSettingsScope();
        var runner = FakeRows.Engine(new SqlServerEngine(), FakeRows.From((_, _, connected, _) =>
        {
            connected();
            return Task.FromResult<IReadOnlyList<dynamic>>(Enumerable.Range(0, 500).Select(i => (dynamic)new Dictionary<string, object?>
            { ["text"] = i == 499 ? new ExplodingValue() : new string('x', 400), ["number"] = i, ["day"] = new DateTime(2024, 1, 1) }).ToArray());
        }));
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), runner);
        await host.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/query") { Content = new StringContent(ErrorLogTests.Body("query")) };
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            await stream.CopyToAsync(Stream.Null);
        });
        var line = Assert.Single(host.Lines());
        Assert.Equal("response", line.GetProperty("Step").GetString());
        Assert.Equal("Error", line.GetProperty("@l").GetString());
        Assert.Contains(typeof(SyntheticFault).FullName!, line.GetProperty("ExceptionText").GetString());
        NoFrameworkError(host);
    }

    [Fact]
    public async Task T7_exception_tree_is_logged_whole()
    {
        using var settings = new ErrorLogSettingsScope();
        const string sql = "SELECT [text] FROM [hidden_schema].[synthetic_view]";
        var error = new SyntheticFault("query fault at elsewhere-host", new AggregateException(
            new InvalidOperationException("plain detail 7"), new InvalidOperationException("login failed for fake-log-source-user"),
            new InvalidOperationException("cannot reach fake-log-catalog-host"), new InvalidOperationException("route to 127.0.0.1 refused"),
            new InvalidOperationException(sql)));
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), FaultEngine(error));
        await host.Start();
        using var response = await host.Client.PostAsync("/query", new StringContent(ErrorLogTests.Body("query")));
        var body = await Exact(response);
        var line = Assert.Single(host.Lines());
        Check(host, line, body, "query", ErrorLogTests.Body("query"));
        var detail = line.GetProperty("ExceptionText").GetString()!;
        foreach (var part in new[] { error.Message, "plain detail 7", "login failed for fake-log-source-user",
            "cannot reach fake-log-catalog-host", "route to 127.0.0.1 refused", sql, "AggregateException" })
            Assert.Contains(part, detail);
        Assert.Contains("   at ", detail);
        var text = string.Join("\n", Directory.GetFiles(host.DirectoryPath).Select(LoggedHost.ReadText));
        LoggedHost.Scan.Text("T7 exception", detail, Rule.Driver);
        ErrorLogTests.Safe(text);
    }

    [Fact]
    public async Task T8_live_caller_driver_cancellation_is_unexpected()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), FaultEngine(new OperationCanceledException("synthetic driver cancel")));
        await host.Start();
        using var response = await host.Client.PostAsync("/query", new StringContent(ErrorLogTests.Body("query")));
        var body = await Exact(response);
        var line = Assert.Single(host.Lines());
        Check(host, line, body, "query", ErrorLogTests.Body("query"));
        Assert.Contains("System.OperationCanceledException", line.GetProperty("ExceptionText").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T9_caller_gone_is_one_warning_without_reply(bool signIn)
    {
        using var settings = new ErrorLogSettingsScope();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = FakeRows.Engine(new SqlServerEngine(), FakeRows.From(async (_, _, connected, token) =>
        {
            connected(); entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { throw new SyntheticFault("synthetic fault after caller left"); }
            return [];
        }));
        var replies = 0;
        var fault = new AnswerFault([typeof(ErrorBody)], () => { Interlocked.Increment(ref replies); return new SyntheticFault("unexpected write"); });
        fault.ArmAlways();
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), runner, configure: b =>
        {
            if (signIn) SyntheticSignIn.UseStalling(b, entered);
            b.Services.AddSingleton(new global::DataGateway.GatewayJson(fault));
        });
        await host.Start();
        using var abort = new CancellationTokenSource();
        var pending = host.Client.PostAsync("/query", new StringContent(ErrorLogTests.Body("query")), abort.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (host.Lines().Length == 0) await Task.Delay(20, timeout.Token);
        var line = Assert.Single(host.Lines());
        Assert.Equal("Warning", line.GetProperty("@l").GetString());
        Assert.Equal("UnexpectedError", line.GetProperty("Code").GetString());
        Assert.Equal(signIn ? "anonymous" : "synthetic-user", line.GetProperty("Caller").GetString());
        if (signIn) Nulls(line, "Request", "Sql");
        else
        {
            Assert.Equal(ErrorLogTests.Body("query"), line.GetProperty("Request").GetString());
            ErrorLogTests.AssertSql(line, Compile(host));
            Assert.Contains(typeof(SyntheticFault).FullName!, line.GetProperty("ExceptionText").GetString());
        }
        Assert.Equal(0, replies);
        NoFrameworkError(host);
    }

    [Fact]
    public async Task T10_concurrent_trails_and_ids_are_independent()
    {
        using var settings = new ErrorLogSettingsScope();
        var entered = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = FakeRows.Engine(new SqlServerEngine(), FakeRows.From(async (_, query, connected, _) =>
        {
            connected(); if (Interlocked.Increment(ref entered) == 8) barrier.SetResult();
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5));
            throw new SyntheticFault("fault for " + query.Parameters["@p1"]);
        }));
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), runner);
        await host.Start();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            var marker = "marker-" + i;
            var body = JsonSerializer.Serialize(new { source = "synthetic", query = new { filters = new[] { new { col = "text", op = "eq", value = marker } } } });
            using var request = new HttpRequestMessage(HttpMethod.Post, "/query") { Content = new StringContent(body) };
            request.Headers.Add("X-Synthetic-User", "user-" + i);
            using var response = await host.Client.SendAsync(request);
            return (Body: body, Answer: await Exact(response), Marker: marker, Caller: "user-" + i);
        }));
        var lines = host.Lines();
        Assert.Equal(8, lines.Length);
        Assert.Equal(8, lines.Select(l => l.GetProperty("IncidentId").GetString()).Distinct().Count());
        foreach (var result in results)
        {
            var line = Assert.Single(lines, l => l.GetProperty("IncidentId").GetString() == result.Answer.GetProperty("incidentId").GetString());
            Check(host, line, result.Answer, "query", result.Body, result.Caller);
            Assert.Equal(result.Marker, line.GetProperty("SqlParameters").GetProperty("@p1").GetString());
            Assert.Contains("fault for " + result.Marker, line.GetProperty("ExceptionText").GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T11_development_has_the_same_json(bool bodyFault)
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), FaultEngine(), environment: "Development", configure: b =>
        { if (bodyFault) b.Services.AddSingleton<IStartupFilter>(new BodyFault(new SyntheticFault("synthetic body fault"))); });
        await host.Start();
        using var response = await host.Client.PostAsync("/query", new StringContent(ErrorLogTests.Body("query")));
        await Exact(response);
        Assert.Single(host.Lines());
        NoFrameworkError(host);
    }

    [Theory]
    [InlineData("list", false)]
    [InlineData("describe", false)]
    [InlineData("query", false)]
    [InlineData("list", true)]
    [InlineData("describe", true)]
    [InlineData("query", true)]
    public async Task T13_body_refusals_are_known_request_errors(string operation, bool unreadable)
    {
        using var settings = new ErrorLogSettingsScope();
        var reads = 0;
        var reader = new HostCatalogState { List = _ => { Interlocked.Increment(ref reads); throw new SyntheticFault("catalog must not run"); },
            Detail = (_, _) => { Interlocked.Increment(ref reads); throw new SyntheticFault("catalog must not run"); } };
        await using var host = new LoggedHost(reader, configure: b =>
        {
            b.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024);
            if (unreadable) b.Services.AddSingleton<IStartupFilter>(new BodyFault(new BadHttpRequestException("synthetic", 400)));
        });
        await host.Start();
        using var response = await host.Client.PostAsync(ErrorLogTests.Route(operation), new StringContent(new string('x', 4096)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = json.RootElement;
        var expected = unreadable ? RouteRequest.Unreadable : RouteRequest.TooLarge;
        Assert.Equal(expected.Code, body.GetProperty("code").GetString());
        Assert.Equal("$", body.GetProperty("path").GetString());
        Assert.Equal(expected.Message, body.GetProperty("message").GetString());
        var line = Assert.Single(host.Lines());
        ErrorLogTests.Check(line, body, operation, null, expected.Code);
        Assert.Equal(expected.Message, line.GetProperty("Message").GetString());
        Assert.Equal("$", line.GetProperty("Path").GetString());
        Assert.Equal("request", line.GetProperty("Step").GetString());
        Assert.Equal("BadHttpRequestException", line.GetProperty("ExceptionType").GetString());
        Assert.Equal("Error", line.GetProperty("@l").GetString());
        Nulls(line, "ExceptionText", "DriverMessage", "Sql", "SqlParameters");
        Assert.Equal(0, reads);
        NoFrameworkError(host);
    }

    [Fact]
    public void T16_failure_line_keeps_whole_exception_text()
    {
        var error = new SyntheticFault("outer", new AggregateException(new Exception("first", new Exception("nested")), new Exception("second")));
        var whole = error.ToString();
        foreach (var value in new[] { "outer", "first", "nested", "second", "AggregateException" }) Assert.Contains(value, whole);
        var line = new FailureLine("abc", "caller", "query", null, ErrorEnvelope.Unexpected, null, exceptionText: whole);
        Assert.Equal(whole, line.Single(p => p.Key == "ExceptionText").Value);
        Assert.Equal(ErrorLogTests.Fields.Append("{OriginalFormat}"), line.Select(p => p.Key));
        Assert.Equal($"Failure abc: query for caller: UnexpectedError: {Message} (null, null, null)", line.Summary());
    }

    [Fact]
    public async Task T7_server_and_user_remain_visible_without_password()
    {
        using var settings = new ErrorLogSettingsScope();
        var error = new SyntheticFault("cannot reach fake-log-catalog-host for fake-log-source-user", new AggregateException(
            new IOException("first nested detail"), new InvalidOperationException("second nested detail")));
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), FaultEngine(error));
        await host.Start();
        using var response = await host.Client.PostAsync("/query", new StringContent(ErrorLogTests.Body("query")));
        await Exact(response);
        var detail = Assert.Single(host.Lines()).GetProperty("ExceptionText").GetString()!;
        foreach (var part in new[] { "fake-log-catalog-host", "fake-log-source-user", "first nested detail", "second nested detail", "   at " })
            Assert.True(detail.Contains(part, StringComparison.Ordinal), "Unexpected detail missing.");
    }

    [Fact]
    public async Task T17_product_contains_no_fault_seams_and_healthy_host_has_no_converters()
    {
        foreach (var assembly in new[] { typeof(Program).Assembly, typeof(QueryRunner).Assembly, typeof(ErrorEnvelope).Assembly, typeof(FailureLog).Assembly })
            Assert.DoesNotContain(assembly.GetTypes(), t => typeof(JsonConverter).IsAssignableFrom(t) || typeof(IStartupFilter).IsAssignableFrom(t));
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), ErrorLogTests.Engine());
        Assert.Empty(host.App.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions.Converters);
        await host.Start();
        using var response = await host.Client.PostAsync("/query", new StringContent(ErrorLogTests.Body("query")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(host.Lines());
        NoFrameworkError(host);
    }

    private static Type AnswerType(string operation) => operation switch
    { "list" => typeof(SourceListResponse), "describe" => typeof(SourceDescribeResponse), _ => typeof(QueryResponse) };

    private static CompiledQuery Compile(LoggedHost host) => new SqlServerEngine().Compile(QueryBuilding.Validate(ErrorLogTests.Contract(),
        "{}", host.App.Services.GetRequiredService<JsonQueryLimits>() with { MaxLimit = ErrorLogTests.Contract().Source.RowCap }).Value);

    internal static void Nulls(JsonElement line, params string[] names)
    { foreach (var name in names) Assert.Equal(JsonValueKind.Null, line.GetProperty(name).ValueKind); }

    private static void Check(LoggedHost host, JsonElement line, JsonElement body, string? operation, string? request, string caller = "synthetic-user")
    {
        ErrorLogTests.Check(line, body, operation, request is null ? null : Encoding.UTF8.GetBytes(request), "UnexpectedError", caller);
        Assert.Equal("$", line.GetProperty("Path").GetString());
        Assert.Equal(Message, line.GetProperty("Message").GetString());
        Assert.Equal("Error", line.GetProperty("@l").GetString());
        Assert.Equal(JsonValueKind.String, line.GetProperty("ExceptionText").ValueKind);
        Nulls(line, "Step", "SqlNumber");
        NoFrameworkError(host);
    }

    [Theory]
    [InlineData("Logging:Serilog:LogLevel:DataGateway.Failure")]
    [InlineData("Logging:Serilog:LogLevel:DataGateway")]
    [InlineData("Logging:Serilog.Extensions.Logging.SerilogLoggerProvider:LogLevel:DataGateway.Failure")]
    [InlineData("Logging:Serilog:LogLevel:Default")]
    [InlineData("Logging:LogLevel:DataGateway.Failure")]
    public async Task T14_provider_rules_cannot_hide_known_or_unexpected_failures(string key)
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), FaultEngine(),
            settings: new Dictionary<string, string?> { [key] = "None" });
        await host.Start();
        for (var pass = 0; pass < 2; pass++)
        {
            using var known = await host.Client.PostAsync("/query", new StringContent("{}"));
            using var unexpected = await host.Client.PostAsync("/query", new StringContent(ErrorLogTests.Body("query")));
            Assert.Equal(400, (int)known.StatusCode);
            await Exact(unexpected);
            var lines = host.Lines();
            Assert.Equal((pass + 1) * 2, lines.Length);
            Assert.All(lines, l => Assert.Equal("Error", l.GetProperty("@l").GetString()));
            if (pass == 0)
            {
                var configuration = host.App.Services.GetRequiredService<IConfiguration>();
                configuration[key] = "None";
                ((IConfigurationRoot)configuration).Reload();
            }
        }
        NoFrameworkError(host);
    }

    internal static DataGateway.Data.IDatabaseEngine FaultEngine(Exception? failure = null) => FakeRows.Engine(new SqlServerEngine(), (_, _, connected, _) =>
    { connected(); throw failure ?? new SyntheticFault("synthetic query fault"); });

    internal static QueryRunner FaultRunner(Exception? failure = null) =>FakeRows.Runner(new SqlServerEngine(), (_, _, connected, _) =>
    { connected(); throw failure ?? new SyntheticFault("synthetic query fault"); });

    internal static async Task<JsonElement> Exact(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        var raw = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(raw);
        var body = json.RootElement.Clone();
        Assert.Equal(new[] { "code", "path", "message", "incidentId" }, body.EnumerateObject().Select(p => p.Name));
        Assert.Equal("UnexpectedError", body.GetProperty("code").GetString());
        Assert.Equal("$", body.GetProperty("path").GetString());
        Assert.Equal(Message, body.GetProperty("message").GetString());
        var id = body.GetProperty("incidentId").GetString();
        Assert.Matches("^[0-9a-f]{32}$", id!);
        Assert.Equal($"{{\"code\":\"UnexpectedError\",\"path\":\"$\",\"message\":\"{Message}\",\"incidentId\":\"{id}\"}}", raw);
        return body;
    }

    internal static void NoFrameworkError(LoggedHost host) =>
        Assert.DoesNotContain(host.AllLines(), l => l.GetProperty("SourceContext").GetString() != "DataGateway.Failure" &&
            l.TryGetProperty("@l", out var level) && level.GetString() is "Error" or "Fatal");
}
