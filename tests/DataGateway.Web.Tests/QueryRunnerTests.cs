using DataGateway.Failures;
using DataGateway.Data.SqlServer;
using System.Data;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DataGateway.Web.Configuration;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Core.Tests;
using DataGateway.Data;
using DataGateway.Data.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class QueryRunnerTests
{
    private const string S = "Server=tcp:127.0.0.1,1;Database=Synthetic;User ID=fake-sensitive-reader;Password=fake-sensitive-password;Application Name=synthetic-app;Encrypt=False;Pooling=False;Connect Timeout=90;ConnectRetryCount=9";
    private const string InvalidMessage = "The source's catalog connection string cannot be used. Ask the data steward to correct the source's catalog row.";
    private const string CancelMessage = "The query was cancelled because the caller ended the request. Submit a new request if still needed.";
    private const string UnavailableMessage = "The source's database could not run the query. If it keeps failing, the DataGateway administrator should check the source. Tell the user. Do not retry.";

    [Theory]
    [InlineData(1, true)]
    [InlineData(7, true)]
    [InlineData(1, false)]
    [InlineData(7, false)]
    public async Task T10_stored_text_reaches_the_seam_unchanged(int seconds, bool explicitSettings)
    {
        var stored = explicitSettings ? S : S[..S.IndexOf(";Connect Timeout", StringComparison.Ordinal)];
        var runner = FakeRows.Runner(new SqlServerEngine(), seconds, FakeRows.From((actual, _, _, _) =>
        {
            Assert.True(string.Equals(stored, actual, StringComparison.Ordinal), "Stored text changed.");
            return Task.FromResult<IReadOnlyList<dynamic>>([]);
        }));
        Assert.True((await runner.RunAsync(Source(stored), Compile(), CancellationToken.None)).IsSuccess);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Database=Synthetic;User ID=u;Password=fake-sensitive-password")]
    [InlineData("Server=fake-sensitive-host;Bogus_fake_marker=1")]
    [InlineData("Server=fake-sensitive-host;Connect Timeout=abc")]
    public async Task T11_invalid_connection_fails_before_io(string? stored)
    {
        var calls = 0;
        var runner = FakeRows.Runner(new SqlServerEngine(), 3, (_, _, _, _) => { calls++; throw new Exception(); });
        var result = await runner.RunAsync(Source(stored!), Compile(), CancellationToken.None);
        var failure = Failure(result, "InvalidCatalog", "connect", stored ?? "");
        Assert.Equal(InvalidMessage, failure.Message);
        Assert.Contains(failure.ExceptionType, new[] { "ArgumentException", "FormatException" });
        Assert.Null(failure.SqlNumber);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(S)]
    [InlineData("malformed-sensitive-marker")]
    public async Task T12_pre_cancel_touches_nothing(string stored)
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = FakeRows.Runner(new SqlServerEngine(), 3, (_, _, _, _) => { calls++; throw new Exception(); });
        var failure = Failure(await runner.RunAsync(Source(stored), Compile(), cancellation.Token),
            "QueryCancelled", null, stored);
        Assert.Null(failure.ExceptionType);
        Assert.Null(failure.SqlNumber);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("cancel")]
    [InlineData("rows")]
    public async Task T12_cancellation_wins_during_io_and_after_last_row(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var runner = FakeRows.Runner(new SqlServerEngine(), 3, FakeRows.From((_, _, connected, _) =>
        {
            calls++;
            if (kind != "cancel") connected();
            cancellation.Cancel();
            if (kind == "io") throw new IOException("fake-sensitive-password");
            if (kind == "cancel") throw new OperationCanceledException();
            return Task.FromResult<IReadOnlyList<dynamic>>(Rows());
        }));
        var failure = Failure(await runner.RunAsync(Source(), Compile(), cancellation.Token), "QueryCancelled",
            kind == "cancel" ? "connect" : "command");
        Assert.Equal(kind == "io" ? "IOException" : kind == "cancel" ? "OperationCanceledException" : null,
            failure.ExceptionType);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task T13_success_preserves_boundary_arguments_order_and_provider_values()
    {
        using var cancellation = new CancellationTokenSource();
        var source = Source();
        var validated = Validate();
        var compiled = new SqlServerEngine().Compile(validated);
        var rows = Rows();
        var calls = 0;
        var runner = FakeRows.Runner(new SqlServerEngine(), 3, FakeRows.From((connection, query, connected, token) =>
        {
            calls++;
            Prepared(S, connection);
            Assert.Same(compiled, query);
            DeadlineToken.Passed(cancellation.Token, token);
            connected();
            return Task.FromResult<IReadOnlyList<dynamic>>(rows);
        }));
        var result = await runner.RunAsync(source, compiled, cancellation.Token);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Failure);
        Assert.Equal(2, result.Value.RowCount);
        for (var i = 0; i < rows.Length; i++)
        {
            var expected = (IDictionary<string, object?>)rows[i];
            Assert.Same(expected, result.Value.Rows[i]);
            Assert.Equal(expected.Keys, result.Value.Rows[i].Keys);
            foreach (var key in expected.Keys)
            {
                Assert.Equal(expected[key], result.Value.Rows[i][key]);
                Assert.Equal(expected[key]?.GetType(), result.Value.Rows[i][key]?.GetType());
            }
        }
        Assert.Equal(2, result.Value.RowCount);
        cancellation.Cancel();
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.RowCount);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false, "invalid", "SourceUnavailable")]
    [InlineData(false, "disposed", "SourceUnavailable")]
    [InlineData(false, "io", "SourceUnavailable")]
    [InlineData(false, "timeout", "SourceUnavailable")]
    [InlineData(false, "argument", "InvalidCatalog")]
    [InlineData(false, "format", "InvalidCatalog")]
    [InlineData(true, "invalid", "SourceUnavailable")]
    [InlineData(true, "disposed", "SourceUnavailable")]
    [InlineData(true, "io", "SourceUnavailable")]
    [InlineData(true, "overflow", "SourceUnavailable")]
    [InlineData(true, "timeout", "QueryTimeout")]
    public async Task T14_expected_driver_failures_are_phase_specific(bool connected, string kind, string code)
    {
        var error = Error(kind);
        var calls = 0;
        var runner = FakeRows.Runner(new SqlServerEngine(), 3, (_, _, mark, _) =>
        {
            calls++;
            if (connected) mark();
            throw error;
        });
        var failure = Failure(await runner.RunAsync(Source(), Compile(), CancellationToken.None), code,
            connected ? "command" : "connect");
        Assert.Same(error, failure.DriverException);
        Assert.Equal(error.GetType().Name, failure.ExceptionType);
        Assert.Null(failure.SqlNumber);
        Assert.Equal(code switch
        {
            "InvalidCatalog" => InvalidMessage,
            "QueryTimeout" => "The query ran longer than the 3-second command timeout and was stopped. Narrow the filters, aggregate, or request fewer rows.",
            _ => UnavailableMessage
        }, failure.Message);
        Assert.Equal(1, calls);
    }

    public static IEnumerable<object[]> Bugs()
    {
        foreach (var connected in new[] { false, true })
        foreach (var cancelled in new[] { false, true })
        foreach (var kind in new[] { "null", "unsupported", "cast", "sqltype" })
            yield return [connected, cancelled, kind];
        yield return [true, false, "argument"];
        yield return [true, true, "argument"];
        yield return [false, false, "cancel"];
        yield return [true, false, "cancel"];
        yield return [false, false, "overflow"];
        yield return [true, false, "format"];
    }

    [Theory]
    [MemberData(nameof(Bugs))]
    public async Task T14_bugs_propagate_as_the_same_exception_even_on_cancel(bool connected, bool cancelled, string kind)
    {
        using var cancellation = new CancellationTokenSource();
        var error = Error(kind);
        var runner = FakeRows.Runner(new SqlServerEngine(), 3, (_, _, mark, _) =>
        {
            if (connected) mark();
            if (cancelled) cancellation.Cancel();
            throw error;
        });
        var caught = await Record.ExceptionAsync(() => runner.RunAsync(Source(), Compile(), cancellation.Token));
        Assert.Same(error, caught);
        Assert.Null(QueryRunner.Classify(error, connected, cancelled, timedOut: false, 3, new SqlServerEngine().RecognizeFailure(error)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T15_silent_server_obeys_connect_timeout_and_cancellation(bool cancel)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var connection = new SqlConnectionStringBuilder(S) { DataSource = $"tcp:127.0.0.1,{port}", ConnectTimeout = cancel ? 30 : 1, ConnectRetryCount = 0 }.ConnectionString;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var runner = new QueryRunner(new SqlServerEngine(), 30);
            var accepted = listener.AcceptTcpClientAsync();
            var elapsed = Stopwatch.StartNew();
            var pending = runner.RunAsync(Source(connection), Compile(), cancellation.Token);
            using var client = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            if (cancel) { cancellation.Cancel(); elapsed.Restart(); }
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(cancel ? 8 : 15));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(cancel ? 8 : 15));
            var failure = Failure(result, cancel ? "QueryCancelled" : "SourceUnavailable", "connect", connection);
            Assert.NotNull(failure.ExceptionType);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task T16_provider_message_reaches_the_line_unchanged()
    {
        var error = new InvalidOperationException("provider detail for fake-visible-host");
        var runner = FakeRows.Runner(new SqlServerEngine(), 3, (_, _, _, _) => throw error);
        var failure = Failure(await runner.RunAsync(Source(), Compile(), CancellationToken.None), "SourceUnavailable", "connect");
        Assert.Same(error, failure.DriverException);
        var line = new FailureLine("abc", "synthetic-user", "query", null, failure, null,
            driverMessage: failure.DriverException!.Message);
        Assert.Equal(error.Message, line.Single(p => p.Key == "DriverMessage").Value);
        Safe(line.Summary() + System.Text.Json.JsonSerializer.Serialize(line.ToDictionary(p => p.Key, p => p.Value)), S);
        Assert.Contains("SourceUnavailable", line.Summary());
    }

    [Fact]
    public void T17_binding_preserves_names_values_and_date_range()
    {
        var compiled = Compile("""{"filters":[{"col":"day","op":"gte","value":"0001-01-01"},{"col":"amount","op":"eq","value":12.5}],"limit":2}""");
        using var command = new SqlCommand();
        typeof(DatabaseEngine<SqlConnection>)
            .GetMethod("BindParameters", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!
            .Invoke(new SqlServerEngine(), [command, compiled]);
        Assert.Equal(compiled.Parameters.Keys, command.Parameters.Cast<SqlParameter>().Select(p => p.ParameterName));
        foreach (var (name, value) in compiled.Parameters)
        {
            var bound = command.Parameters[name];
            Assert.Same(value, bound.Value);
            if (value is DateTime) Assert.Equal(DbType.DateTime2, bound.DbType);
        }
    }

    [Theory]
    [InlineData(false, 30)]
    [InlineData(true, 41)]
    public async Task T18_host_registers_one_runner_with_configured_limits(bool configured, int command)
    {
        var saved = Environment.GetEnvironmentVariable(DataGatewayConfiguration.ConnectionSetting);
        Environment.SetEnvironmentVariable(DataGatewayConfiguration.ConnectionSetting,
            "Server=fake-host.invalid;Database=Catalog;User ID=fake-reader;Password=fake-pass;Encrypt=False");
        try
        {
            await using var app = Program.CreateApplication(new WebApplicationOptions
            {
                ContentRootPath = TestFiles.InRepo("DataGateway.Web"), EnvironmentName = "Production",
                ApplicationName = typeof(Program).Assembly.GetName().Name, Args = []
            }, builder =>
            {
                builder.Logging.ClearProviders();
                new HostCatalogState().Use(builder.Services);
                if (configured) builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataGateway:Limits:CommandTimeoutSeconds"] = "41"
                });
            });
            var runner = await HostCatalogState.NamedRunner(app.Services);
            Assert.Same(runner, await HostCatalogState.NamedRunner(app.Services));
            Assert.Equal(command, runner.CommandTimeoutSeconds);
            Assert.Equal("Catalog", Assert.Single(app.Configuration.GetSection("ConnectionStrings").GetChildren()).Key);
        }
        finally { Environment.SetEnvironmentVariable(DataGatewayConfiguration.ConnectionSetting, saved); }
    }

    [Fact]
    public async Task T18_exact_public_surface_and_argument_guards()
    {
        var constructor = Assert.Single(typeof(QueryRunner).GetConstructors());
        Assert.Equal(new[] { typeof(IDatabaseEngine), typeof(int) }, constructor.GetParameters().Select(p => p.ParameterType));
        Assert.Equal("engine", Assert.Throws<ArgumentNullException>(() => new QueryRunner(null!, 3)).ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryRunner(new SqlServerEngine(), 0));
        var runner = new QueryRunner(new SqlServerEngine(), 3);
        Assert.Equal("query", Assert.Throws<ArgumentNullException>(() => runner.Compile(null!)).ParamName);
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.RunAsync(null!, Compile(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.RunAsync(Source(), null!, CancellationToken.None));
        var token = Assert.Single(typeof(QueryRunner).GetMethod("RunAsync")!.GetParameters(), p => p.ParameterType == typeof(CancellationToken));
        Assert.False(token.IsOptional);
        Assert.False(token.HasDefaultValue);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public async Task T8_stream_rows_are_kept_without_copy_and_ordered_rows_serialize(int count, bool realRows)
    {
        var input = Enumerable.Range(0, count).Select(i => (IDictionary<string, object?>)
            new Dictionary<string, object?> { ["amount"] = i, ["day"] = null }).ToArray();
        if (realRows) input = input.Select(FakeRows.Ordered).ToArray();
        foreach (var row in input)
        {
            Assert.IsAssignableFrom<IDictionary<string, object?>>(row);
            if (realRows) Assert.IsType<OrderedDictionary<string, object?>>(row);
        }
        var runner = FakeRows.Runner(new SqlServerEngine(), 3, FakeRows.From((_, _, connected, _) =>
        { connected(); return Task.FromResult<IReadOnlyList<dynamic>>(input); }));
        var result = await runner.RunAsync(Source(), Compile("{\"limit\":3}"), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(Math.Min(count, 3), result.Value.RowCount);
        Assert.Equal(count > 3, result.Value.More);
        for (var i = 0; i < result.Value.RowCount; i++) Assert.Same(input[i], result.Value.Rows[i]);
        var validated = Validate("{\"limit\":3}");
        var answer = QueryAnswer.Map(Source(), validated, result.Value);
        var equal = result.Value.Rows.Select(row => (IDictionary<string, object?>)new Dictionary<string, object?>(row)).ToArray();
        var expected = new QueryResponse(answer.Source, answer.Columns, equal, answer.RowCount, answer.More) { Unordered = true };
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected, System.Text.Json.JsonSerializerOptions.Web),
            System.Text.Json.JsonSerializer.Serialize(answer, System.Text.Json.JsonSerializerOptions.Web));
    }

    [Fact]
    public async Task T9_probe_stops_enumeration_and_disposes()
    {
        var pulled = 0;
        var disposed = false;
        async IAsyncEnumerable<IDictionary<string, object?>> Stream()
        {
            try
            {
                await Task.Yield();
                for (var i = 0; i < 5; i++)
                {
                    if (++pulled > 4) throw new Exception("Read past probe");
                    yield return new Dictionary<string, object?> { ["amount"] = i };
                }
            }
            finally { disposed = true; }
        }
        var runner = FakeRows.Runner(new SqlServerEngine(), 3, (_, _, connected, _) => { connected(); return Stream(); });
        var result = await runner.RunAsync(Source(), Compile("{\"limit\":3}"), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.RowCount);
        Assert.True(result.Value.More);
        Assert.Equal(4, pulled);
        Assert.True(disposed);
    }

    [Theory]
    [InlineData("cancel", "QueryCancelled")]
    [InlineData("io", "SourceUnavailable")]
    [InlineData("timeout", "QueryTimeout")]
    public async Task T10_mid_read_fault_has_no_partial_answer_and_disposes(string kind, string code)
    {
        using var cancellation = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = false;
        async IAsyncEnumerable<IDictionary<string, object?>> Stream([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                yield return new Dictionary<string, object?> { ["amount"] = 1 };
                waiting.SetResult();
                if (kind == "cancel") await Task.Delay(Timeout.Infinite, token);
                throw kind == "timeout" ? new TimeoutException() : new IOException();
            }
            finally { disposed = true; }
        }
        var runner = FakeRows.Runner(new SqlServerEngine(), 3, (_, _, connected, token) => { connected(); return Stream(token); });
        var pending = runner.RunAsync(Source(), Compile(), cancellation.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (kind == "cancel") cancellation.Cancel();
        Failure(await pending.WaitAsync(TimeSpan.FromSeconds(5)), code, "command");
        Assert.True(disposed);
    }

    [Fact]
    public void T11_only_unbuffered_path_and_no_row_copy()
    {
        var text = File.ReadAllText(TestFiles.InRepo("DataGateway.Data/Queries/QueryRunner.cs"));
        Assert.Contains("engine.ReadSourceAsync", text);
        foreach (var forbidden in new[] { "Dapper", "SqlClient", "DbConnection", "DbDataReader", "DbContext", "session." })
            Assert.DoesNotContain(forbidden, text);
        var adapter = File.ReadAllText(TestFiles.InRepo("DataGateway.Data.SqlServer/SqlServerEngine.cs"))
            + File.ReadAllText(TestFiles.InRepo("DataGateway.Data.SqlServer/SqlServerEngine.Source.cs"));
        var shared = File.ReadAllText(TestFiles.InRepo("DataGateway.Data/DatabaseEngine.cs"));
        Assert.Contains("command.ExecuteReaderAsync(cancellationToken)", shared);
        Assert.Contains("command.CommandTimeout = commandTimeoutSeconds;", shared);
        Assert.Contains("reader!.ReadAsync(token)", shared);
        Assert.Contains("engine is ICappedQueryEngine capped", text);
        foreach (var forbidden in new[] { "ReadSourceAsync", "ExecuteReaderAsync", "DynamicParameters",
            "ScopedSourceRows", "DbContext<", " Bind(", " ParameterType(", "session.QueryAsync" })
            Assert.DoesNotContain(forbidden, adapter);
        Assert.DoesNotContain("session.QueryAsync", shared);
        Assert.False(File.Exists(TestFiles.InRepo("DataGateway.Data.SqlServer/ScopedSourceRows.cs")));
        Assert.DoesNotContain("session.QueryAsync", text);
        Assert.DoesNotContain(".ToArray()", text);
        foreach (var file in Directory.EnumerateFiles(TestFiles.RepoRoot, "*.cs", SearchOption.AllDirectories))
        {
            var parts = Path.GetRelativePath(TestFiles.RepoRoot, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts[0].Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                parts.Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                continue;
            Assert.DoesNotContain("RowCap." + "Apply", File.ReadAllText(file));
        }
    }

    private static SourceContract Source(string connection = S) => CatalogMapping.Map(new CatalogRows(
        new DataSourceRow("dbo.synthetic_view", connection)
        { Id = 7319,
            Name = "synthetic", Title = "Synthetic", Description = "Synthetic source", Grain = "One row", RowCap = 500, IsActive = true
        }, [new("amount", 1, "Amount", "Synthetic amount", "number", null, null, null, true, true, true, false, false),
            new("day", 2, "Day", "Synthetic day", "date", null, null, null, true, true, false, false, false)])).Value;

    private static ValidatedQuery Validate(string json = "{}")
    {
        var result = QueryBuilding.Validate(Source(), json, QueryLimits.ForRowCap(500));
        Assert.True(result.IsSuccess, result.Failure?.Message);
        return result.Value;
    }

    private static CompiledQuery Compile(string json = "{}") => new SqlServerEngine().Compile(Validate(json));

    private static dynamic[] Rows() =>
    [
        new Dictionary<string, object?> { ["amount"] = 12.5m, ["day"] = new DateTime(2024, 12, 31), ["flag"] = true, ["text"] = "text", ["empty"] = null },
        new Dictionary<string, object?> { ["amount"] = -1m, ["day"] = new DateTime(2025, 1, 1), ["flag"] = false, ["text"] = "second", ["empty"] = null }
    ];

    private static Exception Error(string kind) => kind switch
    {
        "invalid" => new InvalidOperationException(), "disposed" => new ObjectDisposedException("synthetic"),
        "io" => new IOException(), "timeout" => new TimeoutException(), "argument" => new ArgumentException(),
        "format" => new FormatException(), "overflow" => new OverflowException(), "null" => new NullReferenceException(),
        "unsupported" => new NotSupportedException(), "cast" => new InvalidCastException(),
        "sqltype" => new SqlTypeException(), "cancel" => new OperationCanceledException(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void Prepared(string stored, string prepared)
    {
        Assert.True(string.Equals(stored, prepared, StringComparison.Ordinal), "Stored text changed.");
    }

    private static CatalogFailure Failure(CatalogResult<QueryRows> result,
        string code, string? step, string connection = S)
    {
        Assert.False(result.IsSuccess);
        Assert.Throws<InvalidOperationException>(() => result.Value);
        var failure = Assert.IsType<CatalogFailure>(result.Failure);
        Assert.Equal(code, failure.Code);
        Assert.Equal(code is "QueryCancelled" or "QueryTimeout" ? "$.query" : "$.source", failure.Path);
        Assert.Equal(step, failure.Step);
        if (code == "QueryCancelled") Assert.Equal(CancelMessage, failure.Message);
        Safe(failure.ToString(), connection);
        Safe(failure.Message, connection);
        return failure;
    }

    private static void Safe(string text, string connection)
    {
        LeakScan.Synthetic(("S", S), ("connection under test", connection)).Text("query failure", text, Rule.DataGateway);
        foreach (var marker in new[] { "synthetic-app", "Bogus_fake_marker", "malformed-sensitive-marker" })
            Assert.True(!text.Contains(marker, StringComparison.OrdinalIgnoreCase), "Diagnostic marker found in query failure.");
    }

    private sealed class Recorder : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Lines.Add(formatter(state, exception));
        }
    }
}
