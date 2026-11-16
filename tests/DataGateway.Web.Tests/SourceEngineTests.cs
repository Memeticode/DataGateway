using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Runtime.CompilerServices;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Queries;
using DataGateway.Data.SqlServer;
using DataGateway.Failures;
using DataGateway.Operations;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class SourceEngineTests
{
    private const string Connection = "Server=synthetic.invalid;Database=Synthetic;User ID=synthetic;Password=synthetic;Encrypt=False;Connect Timeout=17;ConnectRetryCount=0";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static CompiledQuery Query(int keep = 3, bool matched = false) =>
        new("select @p0, @p1", new Dictionary<string, object> { ["@p0"] = keep + 1, ["@p1"] = "value" }, keep, matched, []);
    private static QueryRows Empty() => new([], false);
    private static async Task<QueryRows> Collect(IAsyncEnumerable<IDictionary<string, object?>> source)
    {
        var rows = new List<IDictionary<string, object?>>();
        await foreach (var row in source) rows.Add(row);
        return new(rows, false);
    }
    private static Task<QueryRows> Read(Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>>? consume = null,
        Action? opened = null, CancellationToken token = default, CompiledQuery? query = null, int timeout = 41) =>
        new LifecycleEngine().ReadSourceAsync(Connection, query ?? Query(), timeout, opened ?? (() => { }), consume ?? Collect, token);
    private static object? Hook(string name, params object?[] arguments)
    {
        var method = typeof(SqlServerEngine).GetMethod(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        try { return method.Invoke(new SqlServerEngine(), arguments); }
        catch (System.Reflection.TargetInvocationException error)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException!).Throw();
            throw;
        }
    }

    // The sealed SqlConnection has no probe subtype. Exercise the real base and invoke the leaf's real hooks.
    private sealed class LifecycleEngine() : DatabaseEngine<ProbeConnection>(
        text => new ProbeConnection { ConnectionString = text })
    {
        protected override SqlKata.Compilers.Compiler CreateCompiler() => throw new NotSupportedException();
        protected override string MatchedRowCountExpression => throw new NotSupportedException();
        protected override (string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern) =>
            throw new NotSupportedException();
        protected override void PrepareParameter(DbParameter parameter, object? value) =>
            Hook("PrepareParameter", parameter, value);
        protected override object ConvertCell(object value, OutputColumn? column) =>
            Hook("ConvertCell", value, column)!;
        public override void ValidateSourceConnection(string? connection) =>
            new SqlServerEngine().ValidateSourceConnection(connection);
        public override ProviderFailure? RecognizeFailure(Exception error) =>
            new SqlServerEngine().RecognizeFailure(error);
    }

    private static void Clean(SourceProbe probe, bool reader = true, bool command = true)
    {
        Assert.Equal(reader ? 1 : 0, probe.ReaderDisposals);
        Assert.Equal(command ? 1 : 0, probe.CommandDisposals);
        Assert.Equal(1, probe.ConnectionDisposals);
        Assert.All(probe.Connections, c => Assert.True(c.IsDisposed));
        Assert.Equal(0, probe.NextResults);
    }

    [Fact]
    public void T13_source_methods_are_inherited_and_sql_server_hooks_are_exact()
    {
        var type = typeof(SqlServerEngine);
        var baseType = typeof(DatabaseEngine<SqlConnection>);
        Assert.Equal(baseType, type.BaseType);
        Assert.True(type.IsSealed);
        Assert.True(typeof(SqlConnection).IsSealed);
        Assert.Empty(Assert.Single(type.GetConstructors()).GetParameters());
        Assert.True(typeof(IDatabaseEngine).IsAssignableFrom(type));
        Assert.Contains(type.GetInterfaces(), item => item.FullName == "DataGateway.Data.ICappedQueryEngine");
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.DeclaredOnly |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
        foreach (var name in new[] { "Compile", "ReadSourceAsync" })
            Assert.Equal(baseType, type.GetMethod(name)!.DeclaringType);
        foreach (var name in new[] { "Compile", "ReadSourceAsync", "Bind", "ParameterType" })
            Assert.DoesNotContain(type.GetMethods(flags), method => method.Name == name);
        Assert.Null(type.Assembly.GetType("DataGateway.Data.SqlServer.ScopedSourceRows"));
        var compiler = Assert.IsType<SqlKata.Compilers.SqlServerCompiler>(Hook("CreateCompiler"));
        Assert.True(compiler.UseLegacyPagination);
        Assert.NotSame(compiler, Hook("CreateCompiler"));
        Assert.Equal("COUNT_BIG(*) AS [__dg_matched_rows]", Hook("get_MatchedRowCountExpression"));
        Assert.Equal(("a[[][_]%", ""), Hook("PrepareLikePattern", "a[_%"));
    }

    [Fact]
    public void T13_parameter_and_cell_hooks_preserve_values()
    {
        object?[] values = [null, DBNull.Value, DateTime.MinValue,
            new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            4, 9007199254740993L, 1.2500m, true, false, "", "text", new object()];
        foreach (var value in values)
        {
            var parameter = new ProbeParameter { ParameterName = "@p", DbType = DbType.Guid };
            Hook("PrepareParameter", parameter, value);
            Assert.Same(value ?? DBNull.Value, parameter.Value);
            Assert.Equal(value is DateTime ? DbType.DateTime2 : DbType.Guid, parameter.DbType);
            Assert.Equal("@p", parameter.ParameterName);
            if (value is null or DBNull) continue;
            foreach (var column in new OutputColumn?[] { null, new("flag", "bool"), new("day", "date") })
                Assert.Same(value, Hook("ConvertCell", value, column));
        }
        Assert.Equal(4, (decimal.GetBits((decimal)values[6]!)[3] >> 16) & 0xff);
    }

    [Fact]
    public async Task T5_open_consumer_and_cleanup_are_awaited_in_order()
    {
        using var probe = new SourceProbe
        {
            OpenGate = SourceProbe.Gate(), OpenEntered = SourceProbe.Gate(),
            CleanupGate = SourceProbe.Gate(), CleanupEntered = SourceProbe.Gate()
        };
        var consumerEntered = SourceProbe.Gate();
        var consumerGate = SourceProbe.Gate();
        var opened = 0;
        var pending = Read(async rows =>
        {
            consumerEntered.SetResult();
            await consumerGate.Task.WaitAsync(Deadline);
            return await Collect(rows);
        }, () => { opened++; probe.Events.Add("opened"); });
        await probe.OpenEntered.Task.WaitAsync(Deadline);
        Assert.Equal(0, opened); Assert.Empty(probe.Commands);
        Assert.False(pending.IsCompleted);
        probe.OpenGate.SetResult();
        await consumerEntered.Task.WaitAsync(Deadline);
        Assert.Equal(1, opened);
        Assert.Equal(new[] { "open-start", "open-success", "opened", "command", "execute", "schema", "schema", "schema" }, probe.Events);
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, probe.ReaderDisposals);
        consumerGate.SetResult();
        await probe.CleanupEntered.Task.WaitAsync(Deadline);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, probe.ReaderDisposals); Assert.Equal(1, probe.CommandDisposals);
        probe.CleanupGate.SetResult();
        await pending.WaitAsync(Deadline);
        Clean(probe);
        Assert.Single(probe.Connections); // One execution connection from the factory.
        Assert.All(probe.AssignedConnections, s => Assert.Equal(Connection, s, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("opened")]
    [InlineData("execute")]
    [InlineData("schema")]
    [InlineData("read")]
    [InlineData("consumer-before")]
    [InlineData("consumer-after")]
    public async Task T9_primary_failure_survives_cleanup_with_identity_inner_and_frame(string stage)
    {
        using var probe = new SourceProbe { Values = [[1, "one", true]] };
        var original = new IOException("primary", new Exception("inner"));
        var later = new InvalidOperationException("cleanup");
        probe.ConnectionCleanupError = later;
        probe.ReaderCleanupError = later;
        // The base owns command cleanup even when reader acquisition fails.
        probe.CommandCleanupError = later;
        if (stage == "open") probe.OpenError = original;
        if (stage == "execute") probe.ExecuteError = original;
        if (stage == "schema") probe.SchemaError = original;
        if (stage == "read") { probe.ReadError = original; probe.FailReadAt = 1; }
        var caught = await Record.ExceptionAsync(() => Read(async rows =>
        {
            if (stage == "consumer-before") SourceProbe.ThrowAtProbe(original);
            var result = await Collect(rows);
            if (stage == "consumer-after") SourceProbe.ThrowAtProbe(original);
            return result;
        }, () => { if (stage == "opened") SourceProbe.ThrowAtProbe(original); }));
        Assert.Same(original, caught); Assert.Same(original.InnerException, caught!.InnerException);
        Assert.Contains(nameof(SourceProbe.ThrowAtProbe), caught.StackTrace);
        Clean(probe, stage is "schema" or "read" or "consumer-before" or "consumer-after", stage != "open" && stage != "opened");
    }

    [Theory]
    [InlineData("reader", false)]
    [InlineData("command", false)]
    [InlineData("connection", false)]
    [InlineData("reader", true)]
    [InlineData("command", true)]
    public async Task T9_cleanup_only_failure_surfaces_and_all_acquired_resources_are_attempted(string stage, bool alsoConnection)
    {
        using var probe = new SourceProbe();
        var first = new IOException("first cleanup", new Exception("inner"));
        if (stage == "reader") probe.ReaderCleanupError = first;
        if (stage == "command") probe.CommandCleanupError = first;
        if (stage == "connection") probe.ConnectionCleanupError = first;
        if (alsoConnection) probe.ConnectionCleanupError = new Exception("later connection");
        var caught = await Record.ExceptionAsync(() => Read());
        Assert.Same(first, caught); Assert.Same(first.InnerException, caught!.InnerException);
        Assert.Contains(nameof(SourceProbe.ThrowAtProbe), caught.StackTrace);
        Clean(probe);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T9_no_enumeration_and_abandoned_borrow_expire(bool abandon)
    {
        using var probe = new SourceProbe { Values = [[1, "a", true], [2, "b", false]] };
        IAsyncEnumerable<IDictionary<string, object?>>? saved = null;
        IAsyncEnumerator<IDictionary<string, object?>>? iterator = null;
        IDictionary<string, object?>? row = null;
        await Read(async rows =>
        {
            saved = rows;
            if (abandon)
            {
                iterator = rows.GetAsyncEnumerator();
                Assert.True(await iterator.MoveNextAsync()); row = iterator.Current;
            }
            return Empty();
        });
        Clean(probe);
        var attempts = probe.Reads;
        var borrowError = Assert.Throws<ObjectDisposedException>(() => saved!.GetAsyncEnumerator());
        Assert.Equal("DataGateway.Data.SourceRows", borrowError.ObjectName);
        if (iterator is not null)
        {
            var moveError = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await iterator.MoveNextAsync());
            Assert.Equal("DataGateway.Data.SourceRows", moveError.ObjectName);
            var currentError = Assert.Throws<ObjectDisposedException>(() => iterator.Current);
            Assert.Equal("DataGateway.Data.SourceRows", currentError.ObjectName);
            row!["z"] = "after disposal"; Assert.Equal("after disposal", row["z"]);
        }
        Assert.Equal(attempts, probe.Reads);
    }

    [Theory]
    [InlineData(0, 0, false, 1)]
    [InlineData(2, 2, false, 3)]
    [InlineData(3, 3, false, 4)]
    [InlineData(4, 3, true, 4)]
    [InlineData(12, 3, true, 4)]
    public async Task T7_actual_adapter_is_bounded_by_the_neutral_runner(int input, int kept, bool more, int attempts)
    {
        using var probe = new SourceProbe
        {
            Names = ["amount"], Values = Enumerable.Range(0, input).Select(i => new object?[] { i }).ToArray(),
            FailReadAt = 5, ReadError = new Exception("Fifth read is forbidden")
        };
        var materialized = new List<IDictionary<string, object?>>();
        var engine = new NeutralEngine
        {
            Execute = (connection, query, seconds, opened, consume, token) =>
                new LifecycleEngine().ReadSourceAsync(connection, query, seconds, opened,
                    rows => consume(RecordRows(rows, materialized)), token)
        };
        var result = await new QueryRunner(engine, 41).RunAsync(ErrorLogTests.Contract(), Query(), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(kept, result.Value.RowCount); Assert.Equal(more, result.Value.More);
        Assert.Equal(attempts, probe.Reads); Assert.Equal(Math.Min(input, 4), probe.SuccessfulReads);
        for (var i = 0; i < kept; i++) Assert.Same(materialized[i], result.Value.Rows[i]);
        Assert.Single(probe.Commands); Clean(probe);
    }
    private static async IAsyncEnumerable<IDictionary<string, object?>> RecordRows(
        IAsyncEnumerable<IDictionary<string, object?>> source, List<IDictionary<string, object?>> seen,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        await foreach (var row in source.WithCancellation(token)) { seen.Add(row); yield return row; }
    }

    [Fact]
    public async Task T8_materialized_rows_have_order_ordinal_comparison_independent_mutability_and_provider_values()
    {
        using var probe = new SourceProbe
        {
            Names = ["z", "a", "Z", "day", "amount", "flag", "text"],
            Values = [[DBNull.Value, DateTime.MinValue, 1, new DateTime(2024, 1, 2), 12.5m, true, "first"],
                      ["next", new DateTime(2024, 1, 3), 2, new DateTime(2024, 2, 3), -3m, false, "second"]]
        };
        IDictionary<string, object?>? first = null, second = null;
        await Read(async rows =>
        {
            await using var it = rows.GetAsyncEnumerator();
            Assert.Throws<InvalidOperationException>(() => it.Current);
            Assert.True(await it.MoveNextAsync()); first = it.Current;
            Assert.True(await it.MoveNextAsync()); second = it.Current;
            Assert.NotSame(first, second);
            Assert.Null(first["z"]); Assert.Equal(DateTime.MinValue, first["a"]);
            Assert.Equal(new[] { "z", "a", "Z", "day", "amount", "flag", "text" }, first.Keys);
            Assert.False(first.ContainsKey("DAY")); Assert.Equal(1, first["Z"]);
            Assert.IsType<decimal>(first["amount"]); Assert.IsType<bool>(first["flag"]);
            Assert.IsType<DateTime>(first["day"]); Assert.IsType<string>(first["text"]);
            first["z"] = "changed"; first.Remove("Z"); first.Add("new", 7);
            Assert.Equal("next", second["z"]); Assert.Equal(2, second["Z"]);
            Assert.Equal(new[] { "z", "a", "day", "amount", "flag", "text", "new" }, first.Keys);
            Assert.Throws<InvalidOperationException>(() => rows.GetAsyncEnumerator());
            return new([first, second], false);
        });
        first!["day"] = new DateOnly(2024, 1, 2); second!.Remove("text"); second.Add("after", false);
        Assert.Equal("first", first["text"]); Assert.IsType<DateTime>(second["day"]);
        Assert.False(first.ContainsKey("after")); Clean(probe);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task T8_duplicate_schema_is_checked_before_read_even_when_empty(bool distinct, bool hasRows)
    {
        using var probe = new SourceProbe { Names = distinct ? ["X", "x"] : ["x", "x"], Values = hasRows ? [[1, 2]] : [] };
        if (distinct)
        {
            var rows = await Read(); Assert.Equal(hasRows ? 1 : 0, rows.RowCount);
            if (hasRows) Assert.Equal(new[] { "X", "x" }, rows.Rows[0].Keys);
        }
        else
        {
            var error = await Assert.ThrowsAsync<DataGateway.Data.GatewayInvariantException>(() => Read());
            Assert.Equal("The source row has duplicate column names.", error.Message);
            Assert.Equal(0, probe.Reads);
        }
        Clean(probe);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(41)]
    public async Task T6_actual_command_has_sql_timeout_token_order_and_explicit_parameter_contract(int timeout)
    {
        using var probe = new SourceProbe(); using var caller = new CancellationTokenSource();
        var values = new Dictionary<string, object>
        {
            ["@p0"] = 4, ["@p1"] = "text", ["@p2"] = DateTime.MinValue,
            ["@p3"] = new DateTime(2024, 1, 2), ["@p4"] = 12.5m, ["@p5"] = true
        };
        var query = new CompiledQuery("select @p0, @p1, @p2, @p3, @p4, @p5", values, 3, false, []);
        await Read(token: caller.Token, query: query, timeout: timeout);
        var command = Assert.Single(probe.Commands);
        Assert.Equal(query.Sql, command.CommandText); Assert.Equal(timeout, command.CommandTimeout);
        Assert.Equal(CommandBehavior.Default, command.Behavior);
        Assert.Equal(caller.Token, probe.OpenToken); Assert.Equal(caller.Token, probe.ExecuteToken);
        Assert.All(probe.ReadTokens, token => Assert.Equal(caller.Token, token));
        var parameters = command.Parameters.Cast<DbParameter>().ToArray();
        Assert.Equal(values.Keys, parameters.Select(p => p.ParameterName));
        foreach (var parameter in parameters)
        {
            var value = values[parameter.ParameterName];
            Assert.Same(value, parameter.Value);
            if (value is DateTime) Assert.Equal(DbType.DateTime2, parameter.DbType);
        }
        Clean(probe);
    }

    [Fact]
    public async Task T6_calls_do_not_share_execution_state()
    {
        var engine = new SqlServerEngine();
        ProbeCommand first;
        using (var a = new SourceProbe { Names = ["value"], Values = [[1]] })
        using (var caller = new CancellationTokenSource())
        {
            await Read(token: caller.Token, timeout: 1); first = Assert.Single(a.Commands); Clean(a);
        }
        using (var b = new SourceProbe { Names = ["other"], Values = [["two"]] })
        using (var caller = new CancellationTokenSource())
        {
            var result = await Read(token: caller.Token, timeout: 41);
            Assert.NotSame(first, Assert.Single(b.Commands)); Assert.Equal(1, first.CommandTimeout);
            Assert.Equal("two", Assert.Single(result.Rows)["other"]); Clean(b);
        }
        Assert.Null(engine.RecognizeFailure(new IOException()));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("execute")]
    [InlineData("read")]
    public async Task T4_actual_driver_operations_receive_cancellation(string phase)
    {
        using var probe = new SourceProbe(); using var caller = new CancellationTokenSource();
        var gate = SourceProbe.Gate(); var entered = SourceProbe.Gate();
        if (phase == "open") { probe.OpenGate = gate; probe.OpenEntered = entered; }
        if (phase == "execute") { probe.ExecuteGate = gate; probe.ExecuteEntered = entered; }
        if (phase == "read") { probe.ReadGate = gate; probe.ReadEntered = entered; probe.GateReadAt = 2; probe.Values = [[1, "first", true]]; }
        var opened = 0;
        var pending = Read(opened: () => opened++, token: caller.Token);
        await entered.Task.WaitAsync(Deadline); caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
        Assert.Equal(caller.Token, probe.OpenToken);
        if (phase != "open") Assert.Equal(caller.Token, probe.ExecuteToken);
        if (phase == "read") { Assert.Equal(2, probe.ReadTokens.Count); Assert.All(probe.ReadTokens, token => Assert.Equal(caller.Token, token)); }
        Assert.Equal(phase == "open" ? 0 : 1, opened);
        Clean(probe, phase == "read", phase != "open");
    }

    [Fact]
    public async Task T4_separate_enumerator_token_is_linked_and_expires()
    {
        using var probe = new SourceProbe { ReadGate = SourceProbe.Gate(), ReadEntered = SourceProbe.Gate() };
        using var caller = new CancellationTokenSource(); using var enumeration = new CancellationTokenSource();
        CancellationToken linked = default;
        var pending = Read(async rows =>
        {
            await using var reader = rows.GetAsyncEnumerator(enumeration.Token);
            await reader.MoveNextAsync(); return Empty();
        }, token: caller.Token);
        await probe.ReadEntered.Task.WaitAsync(Deadline);
        linked = Assert.Single(probe.ReadTokens);
        Assert.NotEqual(caller.Token, linked); Assert.NotEqual(enumeration.Token, linked);
        enumeration.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
        Assert.True(linked.IsCancellationRequested); Assert.False(caller.IsCancellationRequested); Clean(probe);
        caller.Cancel(); // Disposed linked registrations must not access provider objects.
        Assert.Equal(1, probe.Reads);
    }

    [Fact]
    public async Task T4_pre_cancel_does_not_validate_read_recognize_or_construct_a_connection()
    {
        using var caller = new CancellationTokenSource(); caller.Cancel(); using var probe = new SourceProbe();
        var engine = new NeutralEngine();
        var result = await new QueryRunner(engine, 41).RunAsync(ErrorLogTests.Contract(), Query(), caller.Token);
        Assert.Equal("QueryCancelled", result.Failure?.Code);
        Assert.Null(result.Failure?.Step); Assert.Null(result.Failure?.DriverException); Assert.Null(result.Failure?.ExceptionType); Assert.Null(result.Failure?.SqlNumber);
        Assert.Equal(0, engine.Validations + engine.Reads + engine.Recognitions);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(token: caller.Token));
        Assert.Empty(probe.Connections);
    }

    [Fact]
    public async Task T4_post_scope_cancel_has_no_fabricated_driver_and_late_cancel_is_inert()
    {
        using var caller = new CancellationTokenSource();
        var engine = new NeutralEngine { Execute = async (_, _, _, opened, consume, _) =>
        {
            opened(); var rows = await consume(Sequence()); caller.Cancel(); return rows;
        } };
        var result = await new QueryRunner(engine, 41).RunAsync(ErrorLogTests.Contract(), Query(), caller.Token);
        Assert.Equal("QueryCancelled", result.Failure?.Code); Assert.Equal("command", result.Failure?.Step);
        Assert.Null(result.Failure?.DriverException); Assert.Equal(0, engine.Recognitions);
        using var late = new CancellationTokenSource();
        var success = await new QueryRunner(new NeutralEngine(), 41).RunAsync(ErrorLogTests.Contract(), Query(), late.Token);
        late.Cancel(); Assert.True(success.IsSuccess);
    }

    public static IEnumerable<object[]> Matrix()
    {
        foreach (var phase in new[] { false, true })
        foreach (var cancelled in new[] { false, true })
        foreach (var kind in new[] { "provider", "provider-timeout", "provider-no-number", "provider-timeout-no-number", "provider-number-minus2",
            "io", "invalid", "disposed", "timeout", "argument", "format", "overflow", "cancel", "task-cancel",
            "unknown", "null", "unsupported", "cast", "sqltype", "db" })
            yield return [phase, cancelled, kind];
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task T3_source_policy_matrix_is_derived_independently(bool connected, bool cancelled, string kind)
    {
        using var caller = new CancellationTokenSource();
        var inner = new Exception("inner");
        Exception error = kind switch
        {
            var k when k.StartsWith("provider", StringComparison.Ordinal) => new ProviderProbeException(k.Contains("timeout", StringComparison.Ordinal), k.Contains("no-number", StringComparison.Ordinal) ? null : k == "provider-number-minus2" ? -2 : 902, inner),
            "io" => new IOException("io", inner), "invalid" => new InvalidOperationException("invalid", inner),
            "disposed" => new ObjectDisposedException("probe"), "timeout" => new TimeoutException("timeout", inner),
            "argument" => new ArgumentException("argument", inner), "format" => new FormatException("format", inner),
            "overflow" => new OverflowException("overflow", inner), "cancel" => new OperationCanceledException("cancel", inner),
            "task-cancel" => new TaskCanceledException("task-cancel", inner), "unknown" => new Exception("unknown", inner),
            "null" => new NullReferenceException("null", inner), "unsupported" => new NotSupportedException("unsupported", inner),
            "cast" => new InvalidCastException("cast", inner), "sqltype" => new SqlTypeException("sqltype", inner),
            "db" => new UnrecognizedDbException(inner), _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var engine = new NeutralEngine { Execute = (_, _, _, opened, _, _) =>
        {
            if (connected) opened(); if (cancelled) caller.Cancel(); ThrowSourceFailure(error); return Task.FromResult(Empty());
        } };
        var eligible = kind.StartsWith("provider", StringComparison.Ordinal) || kind is "io" or "invalid" or "disposed" or "timeout" or "cancel" or "task-cancel" ||
            (connected ? kind == "overflow" : kind is "argument" or "format");
        var propagates = !eligible || (!cancelled && kind is "cancel" or "task-cancel");
        var source = ErrorLogTests.Contract();
        if (propagates)
        {
            var caught = await Record.ExceptionAsync(() => new QueryRunner(engine, 41).RunAsync(source, Query(), caller.Token));
            Assert.Same(error, caught); Assert.Same(error.InnerException, caught!.InnerException); Assert.Contains(nameof(ThrowSourceFailure), caught.StackTrace);
        }
        else
        {
            var result = await new QueryRunner(engine, 41).RunAsync(source, Query(), caller.Token);
            var failure = Assert.IsType<CatalogFailure>(result.Failure);
            var code = cancelled ? "QueryCancelled" : !connected && kind is "argument" or "format" ? "InvalidCatalog" :
                connected && (kind == "timeout" || kind.StartsWith("provider-timeout", StringComparison.Ordinal)) ? "QueryTimeout" : "SourceUnavailable";
            Assert.Equal(code, failure.Code); Assert.Equal(connected ? "command" : "connect", failure.Step);
            Assert.Equal(code is "QueryCancelled" or "QueryTimeout" ? "$.query" : "$.source", failure.Path);
            Assert.Equal(Message(code), failure.Message);
            Assert.Equal(source.Id, failure.SourceId); Assert.Equal(source.Source.Name, failure.SourceName);
            Assert.Same(error, failure.DriverException); Assert.Same(error.InnerException, failure.DriverException!.InnerException);
            Assert.Equal(error.GetType().Name, failure.ExceptionType);
            Assert.Equal((error as ProviderProbeException)?.Number, failure.SqlNumber);
            Assert.Contains(nameof(ThrowSourceFailure), failure.DriverException.StackTrace);
        }
        Assert.Equal(1, engine.Validations); Assert.Equal(1, engine.Reads); Assert.Equal(1, engine.Recognitions);
        Assert.Same(error, engine.Recognized);
        var sql = new SqlServerEngine();
        Assert.Null(sql.RecognizeFailure(error)); Assert.Null(sql.RecognizeFailure(new Exception("wrapper", error)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowSourceFailure(Exception error) => throw error;
    private static string Message(string code) => code switch
    {
        "QueryCancelled" => "The query was cancelled because the caller ended the request. Submit a new request if still needed.",
        "InvalidCatalog" => "The source's catalog connection string cannot be used. Ask the data steward to correct the source's catalog row.",
        "QueryTimeout" => "The query ran longer than the 41-second command timeout and was stopped. Narrow the filters, aggregate, or request fewer rows.",
        _ => "The source's database could not run the query. If it keeps failing, the DataGateway administrator should check the source. Tell the user. Do not retry."
    };

    [Theory]
    [InlineData("move")]
    [InlineData("unknown-move")]
    [InlineData("match")]
    [InlineData("dispose")]
    [InlineData("normal")]
    [InlineData("early")]
    public async Task T10_neutral_enumerator_preserves_primary_failure_over_disposal(string kind)
    {
        var original = kind == "unknown-move" ? new Exception("primary", new Exception("inner")) : new IOException("primary", new Exception("inner"));
        var cleanup = new IOException("dispose", new Exception("cleanup-inner"));
        var rows = new FaultSequence(kind, original, cleanup);
        var engine = new NeutralEngine { Execute = (_, _, _, opened, consume, _) => { opened(); return consume(rows); } };
        var runner = new QueryRunner(engine, 41);
        var caught = await Record.ExceptionAsync(async () =>
        {
            var result = await runner.RunAsync(ErrorLogTests.Contract(), Query(1, kind == "match"), CancellationToken.None);
            if (kind is "normal" or "early") { Assert.True(result.IsSuccess); Assert.Equal(kind == "early", result.Value.More); return; }
            var failure = Assert.IsType<CatalogFailure>(result.Failure); Assert.Equal("SourceUnavailable", failure.Code);
            var expected = kind == "dispose" ? cleanup : original;
            if (kind == "match") Assert.Equal("The query row is missing its matched-row count.", failure.DriverException!.Message);
            else
            {
                Assert.Same(expected, failure.DriverException); Assert.Same(expected.InnerException, failure.DriverException!.InnerException);
                Assert.Contains(nameof(ThrowSourceFailure), failure.DriverException.StackTrace);
            }
        });
        if (kind == "unknown-move")
        {
            Assert.Same(original, caught); Assert.Contains(nameof(ThrowSourceFailure), caught!.StackTrace);
        }
        else if (kind == "match")
            Assert.Equal("The query row is missing its matched-row count.", Assert.IsType<DataGateway.Data.GatewayInvariantException>(caught).Message);
        else Assert.Null(caught);
        Assert.Equal(1, rows.Disposals);
    }

    [Fact]
    public async Task T11_probe_match_evidence_is_not_examined_or_removed()
    {
        using var probe = new SourceProbe
        {
            Names = ["amount", CompiledQuery.MatchedRowCountColumn], Values = [[1, 2L], [2, DBNull.Value]]
        };
        var observed = new List<IDictionary<string, object?>>();
        var engine = new NeutralEngine { Execute = (connection, query, seconds, opened, consume, token) =>
            new LifecycleEngine().ReadSourceAsync(connection, query, seconds, opened, rows => consume(RecordRows(rows, observed)), token) };
        var result = await new QueryRunner(engine, 41).RunAsync(ErrorLogTests.Contract(), Query(1, true), CancellationToken.None);
        Assert.True(result.IsSuccess); Assert.True(result.Value.More); Assert.False(result.Value.NoMatchingRows);
        Assert.Same(observed[0], Assert.Single(result.Value.Rows));
        Assert.False(observed[0].ContainsKey(CompiledQuery.MatchedRowCountColumn));
        Assert.True(observed[1].ContainsKey(CompiledQuery.MatchedRowCountColumn));
        Assert.Null(observed[1][CompiledQuery.MatchedRowCountColumn]); Clean(probe);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Database=Synthetic")]
    [InlineData("Server=x;BadKeyword=planted-password-fragment")]
    [InlineData("Server=x;Connect Timeout=abc")]
    public async Task T2_validation_is_local_safe_and_prevents_execution(string? connection)
    {
        using var probe = new SourceProbe();
        var engine = new NeutralEngine { Validate = s => new SqlServerEngine().ValidateSourceConnection(s) };
        var error = Assert.Throws<ArgumentException>(() => engine.ValidateSourceConnection(connection));
        Assert.Null(error.InnerException);
        Assert.Contains(error.Message, new[] { "A source server is required.", "The source connection string cannot be parsed." });
        Assert.DoesNotContain("planted-password-fragment", error.Message);
        Assert.Empty(probe.Connections);
        var source = CatalogMapping.Map(new(new DataSourceRow("dbo.synthetic", connection!)
        { Id = 72, Name = "synthetic", Title = "Synthetic", Description = "Synthetic", Grain = "One row", RowCap = 500, IsActive = true },
        [new("amount", 1, "Amount", "Synthetic", "number", null, null, null, true, true, true, true, true)])).Value;
        var result = await new QueryRunner(engine, 41).RunAsync(source, Query(), CancellationToken.None);
        Assert.Equal("InvalidCatalog", result.Failure?.Code); Assert.Equal(0, engine.Reads);
        var direct = await Assert.ThrowsAsync<ArgumentException>(() =>
            new LifecycleEngine().ReadSourceAsync(connection!, Query(), 41,
                () => throw new InvalidOperationException("Opened a refused source."),
                Collect, CancellationToken.None));
        Assert.Equal(error.Message, direct.Message);
        Assert.Null(direct.InnerException);
        Assert.Empty(probe.Connections);
    }

    [Fact]
    public void T2_valid_source_text_with_omitted_explicit_and_whitespace_server_parses_without_io()
    {
        using var probe = new SourceProbe(); var engine = new SqlServerEngine();
        foreach (var text in new[] { Connection, "Server=synthetic.invalid", "Data Source=\"   \";Database=Synthetic" })
            engine.ValidateSourceConnection(text);
        Assert.Empty(probe.Connections);
        Assert.Throws<ArgumentNullException>(() => engine.RecognizeFailure(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(913)]
    public async Task T12_selected_engine_executes_operation_and_emits_one_edge_record(int? number)
    {
        using var settings = new ErrorLogSettingsScope();
        var error = new ProviderProbeException(false, number, new Exception("synthetic inner"));
        var engine = new NeutralEngine { Execute = (_, _, _, opened, _, _) => { opened(); ThrowSourceFailure(error); return Task.FromResult(Empty()); } };
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), configure: b => b.Services.AddSingleton<IDatabaseEngine>(engine));
        var runner = await HostCatalogState.NamedRunner(host.App.Services);
        var trail = new RequestTrail();
        using var caller = new CancellationTokenSource();
        var result = await QuerySource.RunAsync(Encoding.UTF8.GetBytes(ErrorLogTests.Body("query")), (_, _) => Task.FromResult(
                CatalogResult<QuerySourceContext>.Success(new(ErrorLogTests.Contract(), runner))),
            QueryLimits.ForRowCap(500), trail, caller.Token);
        Assert.Equal("SourceUnavailable", result.Failure?.Code);
        Assert.Same(trail.Query, engine.ReadQuery); Assert.Same(engine.Output, engine.ReadQuery);
        Assert.Equal(trail.Source!.ConnectionString, engine.Connection); Assert.Equal(1, engine.Timeout);
        DeadlineToken.Passed(caller.Token, engine.Token); Assert.Same(error, engine.Recognized);
        await host.Start();
        using var response = await host.Client.PostAsync("/query", new StringContent(ErrorLogTests.Body("query"), Encoding.UTF8, "application/json"));
        Assert.Equal(503, (int)response.StatusCode);
        var line = Assert.Single(host.Lines());
        Assert.Equal("SourceUnavailable", line.GetProperty("Code").GetString());
        if (number is { } n) Assert.Equal(n, line.GetProperty("SqlNumber").GetInt32());
        else Assert.Equal(JsonValueKind.Null, line.GetProperty("SqlNumber").ValueKind);
        Assert.Equal(2, engine.Validations); Assert.Equal(2, engine.Reads); Assert.Equal(2, engine.Recognitions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T2_actual_connection_assignments_preserve_every_character(bool explicitSettings)
    {
        using var probe = new SourceProbe();
        var stored = explicitSettings ? Connection + ";Application Name=synthetic-test;" : "Server=synthetic.invalid;Database=Synthetic;";
        new SqlServerEngine().ValidateSourceConnection(stored);
        Assert.Empty(probe.Connections);
        await new LifecycleEngine().ReadSourceAsync(stored, Query(), 41, () => { }, Collect, CancellationToken.None);
        Assert.Single(probe.AssignedConnections);
        Assert.All(probe.AssignedConnections, actual => Assert.Equal(stored, actual, StringComparer.Ordinal));
        Clean(probe);
    }

    [Fact]
    public async Task T3_genuine_silent_listener_timeout_is_recognized_but_its_wrapper_is_not()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await using var connection = new SqlConnection($"Server=tcp:127.0.0.1,{port};Database=Synthetic;User ID=synthetic;Password=synthetic;Encrypt=False;Pooling=False;Connect Timeout=1;ConnectRetryCount=0");
            var accept = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            var pending = connection.OpenAsync(timeout.Token);
            using var client = await accept.WaitAsync(Deadline);
            var error = await Assert.ThrowsAsync<SqlException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            var engine = new SqlServerEngine(); var recognized = Assert.IsType<ProviderFailure>(engine.RecognizeFailure(error));
            Assert.True(recognized.IsTimeout); Assert.Equal(-2, recognized.Number);
            Assert.Null(engine.RecognizeFailure(new Exception("wrapper", error)));
        }
        finally { listener.Stop(); }
    }

    private sealed class ProviderProbeException(bool timeout, int? number, Exception inner) : Exception("synthetic provider", inner)
    { internal bool Timeout => timeout; internal int? Number => number; }
    private sealed class UnrecognizedDbException(Exception inner) : DbException("synthetic unrecognized", inner);

    private sealed class NeutralEngine : IDatabaseEngine
    {
        string IDatabaseEngine.ProviderName => "synthetic";
        internal int Validations, Reads, Recognitions, Timeout;
        internal string? Connection;
        internal CancellationToken Token;
        internal CompiledQuery? Output, ReadQuery;
        internal Exception? Recognized;
        internal Action<string?> Validate = s => ArgumentException.ThrowIfNullOrWhiteSpace(s);
        internal Func<string, CompiledQuery, int, Action, Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>>, CancellationToken, Task<QueryRows>> Execute =
            (_, _, _, opened, consume, token) => { token.ThrowIfCancellationRequested(); opened(); return consume(Sequence()); };
        public CompiledQuery Compile(ValidatedQuery query) => Output = new SqlServerEngine().Compile(query);
        public void ValidateSourceConnection(string? connection) { Validations++; Validate(connection); }
        public ProviderFailure? RecognizeFailure(Exception error)
        {
            Recognitions++; Recognized = error;
            return error is ProviderProbeException p ? new(p.Timeout, p.Number) : null;
        }
        public Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int commandTimeoutSeconds, Action opened,
            Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken cancellationToken)
        {
            Reads++; Connection = connection; ReadQuery = query; Timeout = commandTimeoutSeconds; Token = cancellationToken;
            return Execute(connection, query, commandTimeoutSeconds, opened, consume, cancellationToken);
        }
    }
    private static async IAsyncEnumerable<IDictionary<string, object?>> Sequence() { await Task.CompletedTask; yield break; }
    private sealed class FaultSequence(string kind, Exception primary, Exception cleanup) : IAsyncEnumerable<IDictionary<string, object?>>, IAsyncEnumerator<IDictionary<string, object?>>
    {
        private int reads;
        internal int Disposals;
        public IAsyncEnumerator<IDictionary<string, object?>> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public IDictionary<string, object?> Current => new Dictionary<string, object?> { ["amount"] = 1 };
        public ValueTask<bool> MoveNextAsync()
        {
            if (kind is "move" or "unknown-move") ThrowSourceFailure(primary);
            return ValueTask.FromResult(++reads <= (kind == "early" ? 2 : kind is "normal" or "match" ? 1 : 0));
        }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            if (kind is not "normal" and not "early") ThrowSourceFailure(cleanup);
            return ValueTask.CompletedTask;
        }
    }
}
