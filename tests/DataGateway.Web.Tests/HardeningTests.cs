using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using Xunit;
using static DataGateway.Web.Tests.NamedGatewayMcpWire;

namespace DataGateway.Web.Tests;

/// <summary>Pins how DataGateway behaves inside a host that configures things its own way, and its query and failure edges.</summary>
public sealed class HardeningTests
{
    private static readonly string[] Operations = ["list", "describe", "query"];

    // a host's own JSON settings never change DataGateway's wire, over HTTP or MCP, answer or error.
    [Fact]
    public async Task T1_host_json_settings_never_change_the_wire()
    {
        var plain = await Replies(configureHostJson: null);
        var hostile = await Replies(services => services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = null;
            options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.SerializerOptions.Encoder = JavaScriptEncoder.Default;
        }));
        Assert.Equal(plain, hostile);
        Assert.Contains("\"sources\":[", plain[0]);
        Assert.Contains("\"rowCount\":", plain[4]);
        Assert.Contains("\"code\":\"", plain[^1]);
    }

    private static async Task<string[]> Replies(Action<IServiceCollection>? configureHostJson)
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        await using var host = new NamedGatewayHost(builder =>
        {
            configureHostJson?.Invoke(builder.Services);
            NamedGatewayHost.Register(builder.Services, "a", state);
        });
        host.App.MapDataGateway("a", "/a");
        await host.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var client = await Connect(host.Client, "/a/mcp", null, deadline.Token);
        var replies = new List<string>();
        foreach (var operation in Operations)
        {
            replies.Add((await host.Post("/a", operation)).Text);
            replies.Add(McpWire.Text(await McpWire.Call(client, Tool(operation), NamedGatewayHost.Body(operation), deadline.Token)));
        }
        var missing = NamedGatewayHost.Body("describe", "no_such_source");
        replies.Add((await host.Post("/a", "describe", missing)).Text);
        replies.Add(McpWire.Text(await McpWire.Call(client, Tool("describe"), missing, deadline.Token)));
        return [.. replies.Select(reply => Regex.Replace(reply, "\"incidentId\":\"[0-9a-f]{32}\"", "\"incidentId\":\"*\""))];
    }

    // the MCP SDK shares one set of HTTP transport options per process, so an app that configures them
    // for its own MCP server is refused at startup instead of silently changing either server.
    // The API document describes the wire the replies actually use, whatever the host's own JSON settings.
    // A host's number handling is left out on purpose: it changes only how loosely a number is described.
    [Theory]
    [InlineData("snake")]
    [InlineData("pascal")]
    [InlineData("read-only-ignored")]
    public async Task T1b_host_json_settings_never_change_the_api_document(string how)
    {
        var plain = await ApiDocument(null);
        var hostile = await ApiDocument(options =>
        {
            if (how == "snake") options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            if (how == "pascal") options.PropertyNamingPolicy = null;
            if (how == "read-only-ignored") options.IgnoreReadOnlyProperties = true;
        });
        Assert.Equal(plain, hostile);
        Assert.Contains("\"rowCount\"", plain);
    }

    private static async Task<string> ApiDocument(Action<JsonSerializerOptions>? hostJson)
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        await using var host = new NamedGatewayHost(builder =>
        {
            if (hostJson is not null) builder.Services.ConfigureHttpJsonOptions(options => hostJson(options.SerializerOptions));
            NamedGatewayHost.Register(builder.Services, "a", state);
        });
        host.App.MapDataGateway("a", "/a");
        host.App.MapDataGatewayApiPage("a", "/docs");
        await host.Start();
        return await host.Client.GetStringAsync("/docs/openapi/v1.json");
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("own-class")]
    [InlineData("post-configure")]
    public async Task T2_an_app_that_configures_the_mcp_transport_itself_is_refused(string how)
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        await using var host = new NamedGatewayHost(builder =>
        {
            if (how == "before") builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = false);
            NamedGatewayHost.Register(builder.Services, "a", state);
            if (how == "after") builder.Services.AddMcpServer().WithHttpTransport(options => options.ConfigureSessionOptions = (_, _, _) => Task.CompletedTask);
            if (how == "own-class") builder.Services.AddSingleton<IConfigureOptions<HttpServerTransportOptions>, AppTransportSetup>();
            if (how == "post-configure") builder.Services.PostConfigure<HttpServerTransportOptions>(options => options.Stateless = false);
        });
        var error = Assert.Throws<InvalidOperationException>(() => host.App.MapDataGateway("a", "/a"));
        Assert.StartsWith("Gateway 'a'. This app also configures the MCP SDK's HTTP transport (HttpServerTransportOptions)", error.Message);
    }

    [Theory]
    [InlineData("bare")]
    [InlineData("bare-transport")]
    [InlineData("other-name")]
    [InlineData("other-name-with-dependency")]
    public async Task T3_an_app_without_its_own_transport_settings_still_maps_and_answers(string how)
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        await using var host = new NamedGatewayHost(builder =>
        {
            // A transport added without settings changes nothing: SDK 2.x already defaults to stateless.
            if (how == "bare-transport") builder.Services.AddMcpServer().WithHttpTransport();
            else builder.Services.AddMcpServer();
            if (how == "other-name-with-dependency")
                builder.Services.AddOptions<HttpServerTransportOptions>("other")
                    .Configure<NamedGatewayDependency>((options, _) => options.Stateless = false);
            if (how == "other-name")
            {
                builder.Services.Configure<HttpServerTransportOptions>("other", options => options.Stateless = false);
                builder.Services.PostConfigure<HttpServerTransportOptions>("other", options => options.Stateless = false);
            }
            NamedGatewayHost.Register(builder.Services, "a", state);
        });
        host.App.MapDataGateway("a", "/a");
        await host.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var client = await Connect(host.Client, "/a/mcp", null, deadline.Token);
        var result = await McpWire.Call(client, Tool("list"), NamedGatewayHost.Body("list"), deadline.Token);
        Assert.NotEqual(true, result.IsError);
    }

    // The source timeout is one deadline for the whole query, even when every single read is fast.
    [Fact]
    public async Task T4_slow_rows_that_each_beat_the_timeout_still_end_in_QueryTimeout()
    {
        var engine = new PacedEngine(beforeOpen: TimeSpan.Zero, perRow: TimeSpan.FromMilliseconds(300), rows: 100);
        var watch = Stopwatch.StartNew();
        var result = await new QueryRunner(engine).RunAsync(ErrorLogTests.Contract(queryTimeoutSeconds: 1), Query(keep: 50), CancellationToken.None);
        Assert.Equal("QueryTimeout", result.Failure?.Code);
        Assert.Equal("command", result.Failure!.Step);
        Assert.Equal("The query ran longer than the 1-second command timeout and was stopped. Narrow the filters, aggregate, or request fewer rows.", result.Failure.Message);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(10));
        Assert.InRange(engine.RowsServed, 1, 5);
    }

    [Fact]
    public async Task T5_connecting_is_governed_by_the_connection_string_not_the_query_deadline()
    {
        var engine = new PacedEngine(beforeOpen: TimeSpan.FromMilliseconds(1500), perRow: TimeSpan.Zero, rows: 2);
        var result = await new QueryRunner(engine).RunAsync(ErrorLogTests.Contract(queryTimeoutSeconds: 1), Query(keep: 3), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.RowCount);
    }

    [Fact]
    public async Task T6_a_caller_cancel_inside_the_deadline_is_still_QueryCancelled()
    {
        var engine = new PacedEngine(beforeOpen: TimeSpan.Zero, perRow: TimeSpan.FromMilliseconds(200), rows: 100);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();
        var result = await new QueryRunner(engine).RunAsync(ErrorLogTests.Contract(queryTimeoutSeconds: 30), Query(keep: 50), caller.Token);
        Assert.Equal("QueryCancelled", result.Failure?.Code);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(4));
        Assert.InRange(engine.RowsServed, 0, 5);
    }

    [Theory]
    [InlineData(1, 3, 1500)]
    [InlineData(2, 1, 800)]
    public async Task T6b_rows_finished_after_the_deadline_by_a_provider_that_ignores_cancel_are_refused(int rows, int keep, int perRowMs)
    {
        var engine = new PacedEngine(beforeOpen: TimeSpan.Zero, perRow: TimeSpan.FromMilliseconds(perRowMs), rows: rows, honoursCancel: false);
        var result = await new QueryRunner(engine).RunAsync(ErrorLogTests.Contract(queryTimeoutSeconds: 1), Query(keep: keep), CancellationToken.None);
        Assert.Equal("QueryTimeout", result.Failure?.Code);
    }

    [Fact]
    public async Task T6c_a_cancel_the_engine_reports_as_an_io_error_after_the_deadline_is_QueryTimeout()
    {
        var result = await new QueryRunner(new CancelAsIoEngine()).RunAsync(ErrorLogTests.Contract(queryTimeoutSeconds: 1), Query(keep: 3), CancellationToken.None);
        Assert.Equal("QueryTimeout", result.Failure?.Code);
        Assert.IsType<IOException>(result.Failure!.DriverException);
    }

    [Fact]
    public void T6d_a_timeout_longer_than_the_timer_allows_gets_the_longest_deadline_not_none()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), QueryRunner.Deadline(30));
        Assert.Equal(QueryRunner.LongestDeadline, QueryRunner.Deadline(int.MaxValue));
        using var timer = new CancellationTokenSource();
        timer.CancelAfter(QueryRunner.Deadline(int.MaxValue));
        Assert.False(timer.IsCancellationRequested);
    }

    // DataGateway's own broken assumptions are unexpected errors; real provider faults stay SourceUnavailable.
    [Fact]
    public void T7_only_provider_faults_are_classified_as_source_failures()
    {
        Assert.Null(QueryRunner.Classify(new GatewayInvariantException("synthetic"), connected: true, cancelled: false, timedOut: false, 30, provider: null));
        Assert.Null(QueryRunner.Classify(new GatewayInvariantException("synthetic"), connected: true, cancelled: false, timedOut: true, 30, provider: null));
        Assert.Null(QueryRunner.Classify(new GatewayInvariantException("synthetic"), connected: true, cancelled: false, timedOut: false, 30, provider: new ProviderFailure(false, 1)));
        Assert.Equal("SourceUnavailable", QueryRunner.Classify(new InvalidOperationException("pool"), connected: true, cancelled: false, timedOut: false, 30, provider: null)?.Code);
    }

    // the SourceUnavailable message gives one instruction: tell the user, do not retry.
    [Fact]
    public void T8_source_unavailable_message_does_not_also_say_try_again()
    {
        var failure = QueryRunner.Classify(new IOException("synthetic"), connected: true, cancelled: false, timedOut: false, 30, provider: null)!;
        Assert.Equal("SourceUnavailable", failure.Code);
        Assert.DoesNotContain("Try again", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(" Tell the user. Do not retry.", failure.Message, StringComparison.Ordinal);
    }

    // describe refuses the same null fields list refuses.
    [Fact]
    public void T9_detail_rows_get_the_same_null_checks_as_list_rows()
    {
        var source = new DataSourceRow("dbo.synthetic_view", "Server=synthetic")
        { QueryTimeoutSeconds = 30,
            Name = "synthetic", Title = null!, Description = null!, Grain = null!, IsActive = true, RowCap = 500
        };
        var failures = CatalogValidator.ValidateDetail("synthetic", new CatalogDetail("sqlserver", new CatalogRows(source, [])));
        Assert.Equal(
            ["$.detail.rows.source.title", "$.detail.rows.source.description", "$.detail.rows.source.grain"],
            failures.Select(failure => failure.Path));
    }

    // a logging provider that throws never turns a refusal into a 500 or escapes the boundary.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T10_a_throwing_failure_logger_changes_no_answer(bool scopeThrowsOnEnd)
    {
        var expected = await Answers(throwing: null);
        var actual = await Answers(new ThrowingFailureLogs(scopeThrowsOnEnd));
        Assert.Equal(expected, actual);
        Assert.Contains(expected, answer => answer.StartsWith("404 ", StringComparison.Ordinal));
    }

    private static async Task<string[]> Answers(ThrowingFailureLogs? throwing)
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        await using var host = new NamedGatewayHost(builder =>
        {
            if (throwing is not null) builder.Logging.AddProvider(throwing);
            NamedGatewayHost.Register(builder.Services, "a", state);
        });
        host.App.MapDataGateway("a", "/a");
        await host.Start();
        var answers = new List<string>();
        foreach (var (operation, body) in new[] { ("list", (string?)null), ("describe", NamedGatewayHost.Body("describe", "no_such_source")), ("query", "{") })
        {
            var reply = await host.Post("/a", operation, body);
            answers.Add($"{reply.Status} {Regex.Replace(reply.Text, "\"incidentId\":\"[0-9a-f]{32}\"", "\"incidentId\":\"*\"")}");
        }
        return [.. answers];
    }

    // one keep loop serves DatabaseEngine and third-party engines alike.
    [Fact]
    public void T11_the_keep_loop_has_one_home()
    {
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
        var owners = typeof(QueryRunner).Assembly.GetTypes()
            .Where(type => type.GetMethods(all).Any(method => method.Name == "KeepAsync"))
            .Select(type => type.Name).ToArray();
        Assert.Equal([nameof(KeptRows)], owners);
    }

    // the catch-all needs its failure log; there is no fallback that cannot resolve.
    [Fact]
    public void T12_catch_all_has_no_unused_entry_and_a_required_log()
    {
        var methods = typeof(DataGateway.Routes.CatchAll).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(methods, method => method.Name == "Use");
        var answer = Assert.Single(methods, method => method.Name == "Answer");
        Assert.False(answer.GetParameters().Single(parameter => parameter.Name == "failures").IsOptional);
    }

    private static CompiledQuery Query(int keep) => new("select 1", new Dictionary<string, object>(), keep, false, []);

    /// <summary>An engine whose connect and rows take measured time; its rows honour the token unless told not to.</summary>
    private sealed class PacedEngine(TimeSpan beforeOpen, TimeSpan perRow, int rows, bool honoursCancel = true) : IDatabaseEngine
    {
        internal int RowsServed;
        public string ProviderName => "synthetic";
        public CompiledQuery Compile(ValidatedQuery query) => throw new NotSupportedException();
        public void ValidateSourceConnection(string? connection) { }
        public ProviderFailure? RecognizeFailure(Exception error) => null;

        public async Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int commandTimeoutSeconds, Action opened,
            Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken cancellationToken)
        {
            await Task.Delay(beforeOpen, cancellationToken);
            opened();
            return await consume(Rows(cancellationToken));
        }

        private async IAsyncEnumerable<IDictionary<string, object?>> Rows([EnumeratorCancellation] CancellationToken token = default)
        {
            for (var i = 0; i < rows; i++)
            {
                await Task.Delay(perRow, honoursCancel ? token : CancellationToken.None);
                RowsServed++;
                yield return new Dictionary<string, object?> { ["n"] = (long)i };
            }
        }
    }

    /// <summary>An app's own options class for the MCP SDK's HTTP transport.</summary>
    private sealed class AppTransportSetup : IConfigureOptions<HttpServerTransportOptions>
    {
        public void Configure(HttpServerTransportOptions options) => options.Stateless = false;
    }

    /// <summary>An engine whose wait is cut by the deadline and which reports that cancel as an I/O error, as some drivers do.</summary>
    private sealed class CancelAsIoEngine : IDatabaseEngine
    {
        public string ProviderName => "synthetic";
        public CompiledQuery Compile(ValidatedQuery query) => throw new NotSupportedException();
        public void ValidateSourceConnection(string? connection) { }
        public ProviderFailure? RecognizeFailure(Exception error) => null;

        public async Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int commandTimeoutSeconds, Action opened,
            Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken cancellationToken)
        {
            opened();
            try { await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken); }
            catch (OperationCanceledException) { throw new IOException("synthetic read cancelled"); }
            return await consume(AsyncEnumerable.Empty<IDictionary<string, object?>>());
        }
    }

    /// <summary>A logging provider whose DataGateway.Failure logger throws from Log, and from BeginScope or the scope's end.</summary>
    private sealed class ThrowingFailureLogs(bool scopeThrowsOnEnd) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            categoryName == DataGatewayExtensions.FailureLogCategory ? new Throwing(scopeThrowsOnEnd) : NullLogger.Instance;
        public void Dispose() { }

        private sealed class EndThrows : IDisposable
        {
            public void Dispose() => throw new IOException("synthetic sink is down");
        }

        private sealed class Throwing(bool scopeThrowsOnEnd) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
                scopeThrowsOnEnd ? new EndThrows() : throw new IOException("synthetic sink is down");
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                throw new IOException("synthetic sink is down");
        }
    }
}
