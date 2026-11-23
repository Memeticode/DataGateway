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
    /// <summary>The three operations, in the order the wire tests call them.</summary>
    private static readonly string[] Operations = ["list", "describe", "query"];

    /// <summary>A host's own JSON settings never change DataGateway's wire, over HTTP or MCP, answer or error.</summary>
    /// <remarks>The gateway serializes with its own options; a host that sets a naming policy, null handling or encoder
    /// for its other endpoints must not change what callers of the gateway receive.</remarks>
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

    /// <summary>Calls every operation and an unknown-source describe over both HTTP and MCP, and returns the answers
    /// with incident ids masked.</summary>
    /// <param name="configureHostJson">The host's own JSON setup, or <see langword="null"/> for none.</param>
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

    /// <summary>The API document describes the wire the replies actually use, whatever the host's own JSON settings.</summary>
    /// <remarks>A host's number handling is left out on purpose: it changes only how loosely a number is described.</remarks>
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

    /// <summary>Serves the API page in a host with the given JSON settings and returns its OpenAPI document.</summary>
    /// <param name="hostJson">Changes to the host's JSON options, or <see langword="null"/> for none.</param>
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

    /// <summary>An app that configures the MCP SDK's HTTP transport itself, in any of four ways, is refused when the
    /// gateway is mapped.</summary>
    /// <remarks>The MCP SDK shares one set of HTTP transport options per process, so an app that configures them for its
    /// own MCP server is refused at startup instead of silently changing either server.</remarks>
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

    /// <summary>An app that adds the MCP server without transport settings, or configures only a differently named
    /// options instance, still maps the gateway and answers.</summary>
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

    /// <summary>The source timeout is one deadline for the whole query, even when every single read is fast.</summary>
    [Fact]
    public async Task T4_slow_rows_that_each_beat_the_timeout_still_end_in_QueryTimeout()
    {
        var engine = new PacedEngine(beforeOpen: TimeSpan.Zero, perRow: TimeSpan.FromMilliseconds(300), rows: 100);
        var watch = Stopwatch.StartNew();
        var result = await new QueryRunner(engine).RunAsync(ErrorLogTests.Contract(queryTimeoutSeconds: 1), Query(keep: 50), CancellationToken.None);
        Assert.Equal("QueryTimeout", result.Failure?.Code);
        Assert.Equal("command", result.Failure!.Step);
        Assert.Equal("The query ran longer than the 1-second command timeout and was stopped. Narrow the query once (filters, an aggregate or fewer rows) and say what you narrowed; if it times out again, tell the user, give the incidentId and stop.", result.Failure.Message);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(10));
        Assert.InRange(engine.RowsServed, 1, 5);
    }

    /// <summary>The query deadline starts once the connection is open: a slow connect is bounded by the connection
    /// string's own connect timeout, not by the query timeout.</summary>
    [Fact]
    public async Task T5_connecting_is_governed_by_the_connection_string_not_the_query_deadline()
    {
        var engine = new PacedEngine(beforeOpen: TimeSpan.FromMilliseconds(1500), perRow: TimeSpan.Zero, rows: 2);
        var result = await new QueryRunner(engine).RunAsync(ErrorLogTests.Contract(queryTimeoutSeconds: 1), Query(keep: 3), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.RowCount);
    }

    /// <summary>A caller who cancels before the deadline gets QueryCancelled, not QueryTimeout, and the read stops promptly.</summary>
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

    /// <summary>Rows that a provider which ignores cancellation delivers after the deadline are refused with
    /// QueryTimeout instead of being returned.</summary>
    [Theory]
    [InlineData(1, 3, 1500)]
    [InlineData(2, 1, 800)]
    public async Task T6b_rows_finished_after_the_deadline_by_a_provider_that_ignores_cancel_are_refused(int rows, int keep, int perRowMs)
    {
        var engine = new PacedEngine(beforeOpen: TimeSpan.Zero, perRow: TimeSpan.FromMilliseconds(perRowMs), rows: rows, honoursCancel: false);
        var result = await new QueryRunner(engine).RunAsync(ErrorLogTests.Contract(queryTimeoutSeconds: 1), Query(keep: keep), CancellationToken.None);
        Assert.Equal("QueryTimeout", result.Failure?.Code);
    }

    /// <summary>A driver that reports the deadline's cancellation as an I/O error still produces QueryTimeout, with the
    /// driver's exception kept.</summary>
    [Fact]
    public async Task T6c_a_cancel_the_engine_reports_as_an_io_error_after_the_deadline_is_QueryTimeout()
    {
        var result = await new QueryRunner(new CancelAsIoEngine()).RunAsync(ErrorLogTests.Contract(queryTimeoutSeconds: 1), Query(keep: 3), CancellationToken.None);
        Assert.Equal("QueryTimeout", result.Failure?.Code);
        Assert.IsType<IOException>(result.Failure!.DriverException);
    }

    /// <summary>A source timeout longer than a cancellation timer can represent is clamped to the longest deadline the
    /// timer allows, instead of meaning no deadline or throwing.</summary>
    [Fact]
    public void T6d_a_timeout_longer_than_the_timer_allows_gets_the_longest_deadline_not_none()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), QueryRunner.Deadline(30));
        Assert.Equal(QueryRunner.LongestDeadline, QueryRunner.Deadline(int.MaxValue));
        using var timer = new CancellationTokenSource();
        timer.CancelAfter(QueryRunner.Deadline(int.MaxValue));
        Assert.False(timer.IsCancellationRequested);
    }

    /// <summary>DataGateway's own broken assumptions are unexpected errors; real provider faults stay SourceUnavailable.</summary>
    [Fact]
    public void T7_only_provider_faults_are_classified_as_source_failures()
    {
        Assert.Null(QueryRunner.Classify(new GatewayInvariantException("synthetic"), connected: true, cancelled: false, timedOut: false, 30, provider: null));
        Assert.Null(QueryRunner.Classify(new GatewayInvariantException("synthetic"), connected: true, cancelled: false, timedOut: true, 30, provider: null));
        Assert.Null(QueryRunner.Classify(new GatewayInvariantException("synthetic"), connected: true, cancelled: false, timedOut: false, 30, provider: new ProviderFailure(false, 1)));
        Assert.Equal("SourceUnavailable", QueryRunner.Classify(new InvalidOperationException("pool"), connected: true, cancelled: false, timedOut: false, 30, provider: null)?.Code);
    }

    /// <summary>The SourceUnavailable message gives one instruction: tell the user, do not retry.</summary>
    [Fact]
    public void T8_source_unavailable_message_does_not_also_say_try_again()
    {
        var failure = QueryRunner.Classify(new IOException("synthetic"), connected: true, cancelled: false, timedOut: false, 30, provider: null)!;
        Assert.Equal("SourceUnavailable", failure.Code);
        Assert.DoesNotContain("Try again", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(" Tell the user, give the incidentId and stop; do not retry.", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Describe refuses the same null fields list refuses.</summary>
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

    /// <summary>A logging provider that throws never turns a refusal into a 500 or escapes the boundary.</summary>
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

    /// <summary>Calls each operation once with a refused request and returns status and body, incident ids masked.</summary>
    /// <param name="throwing">A logging provider that throws, or <see langword="null"/> for none.</param>
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

    /// <summary>One keep loop serves <see cref="DatabaseEngine{TConnection}"/> and third-party engines alike.</summary>
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

    /// <summary>The catch-all needs its failure log; there is no fallback that cannot resolve.</summary>
    [Fact]
    public void T12_catch_all_has_no_unused_entry_and_a_required_log()
    {
        var methods = typeof(DataGateway.Routes.CatchAll).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(methods, method => method.Name == "Use");
        var answer = Assert.Single(methods, method => method.Name == "Answer");
        Assert.False(answer.GetParameters().Single(parameter => parameter.Name == "failures").IsOptional);
    }

    /// <summary>A trivial compiled query that keeps <paramref name="keep"/> rows.</summary>
    private static CompiledQuery Query(int keep) => new("select 1", new Dictionary<string, object>(), keep, false, []);

    /// <summary>An engine whose connect and rows take measured time; its rows honour the token unless told not to.</summary>
    private sealed class PacedEngine(TimeSpan beforeOpen, TimeSpan perRow, int rows, bool honoursCancel = true) : IDatabaseEngine
    {
        /// <summary>How many rows the engine produced before the read ended.</summary>
        internal int RowsServed;
        /// <summary>The provider name, <c>synthetic</c>.</summary>
        public string ProviderName => "synthetic";
        /// <summary>Not used: the tests pass compiled queries directly.</summary>
        public CompiledQuery Compile(ValidatedQuery query) => throw new NotSupportedException();
        /// <summary>Accepts any connection.</summary>
        public void ValidateSourceConnection(string? connection) { }
        /// <summary>Recognizes no failure.</summary>
        public ProviderFailure? RecognizeFailure(Exception error) => null;

        /// <summary>Waits before reporting the connection opened, then streams the paced rows to the consumer.</summary>
        public async Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int commandTimeoutSeconds, Action opened,
            Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken cancellationToken)
        {
            await Task.Delay(beforeOpen, cancellationToken);
            opened();
            return await consume(Rows(cancellationToken));
        }

        /// <summary>Yields the rows one by one, each after its delay.</summary>
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
        /// <summary>Turns off stateless mode, as an app with its own MCP server might.</summary>
        public void Configure(HttpServerTransportOptions options) => options.Stateless = false;
    }

    /// <summary>An engine whose wait is cut by the deadline and which reports that cancel as an I/O error, as some drivers do.</summary>
    private sealed class CancelAsIoEngine : IDatabaseEngine
    {
        /// <summary>The provider name, <c>synthetic</c>.</summary>
        public string ProviderName => "synthetic";
        /// <summary>Not used: the tests pass compiled queries directly.</summary>
        public CompiledQuery Compile(ValidatedQuery query) => throw new NotSupportedException();
        /// <summary>Accepts any connection.</summary>
        public void ValidateSourceConnection(string? connection) { }
        /// <summary>Recognizes no failure.</summary>
        public ProviderFailure? RecognizeFailure(Exception error) => null;

        /// <summary>Waits until cancelled, then throws an <see cref="IOException"/> in place of the cancellation.</summary>
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
        /// <summary>A throwing logger for the failure category, a null logger for every other.</summary>
        public ILogger CreateLogger(string categoryName) =>
            categoryName == DataGatewayExtensions.FailureLogCategory ? new Throwing(scopeThrowsOnEnd) : NullLogger.Instance;
        /// <summary>Nothing to release.</summary>
        public void Dispose() { }

        /// <summary>A scope whose end throws.</summary>
        private sealed class EndThrows : IDisposable
        {
            /// <summary>Throws, as a failing sink would.</summary>
            public void Dispose() => throw new IOException("synthetic sink is down");
        }

        /// <summary>The failure logger that throws from every write and from its scope.</summary>
        private sealed class Throwing(bool scopeThrowsOnEnd) : ILogger
        {
            /// <summary>Throws now, or returns a scope that throws when it ends.</summary>
            public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
                scopeThrowsOnEnd ? new EndThrows() : throw new IOException("synthetic sink is down");
            /// <summary>Every level is enabled, so every write reaches <see cref="Log"/>.</summary>
            public bool IsEnabled(LogLevel logLevel) => true;
            /// <summary>Throws.</summary>
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                throw new IOException("synthetic sink is down");
        }
    }
}
