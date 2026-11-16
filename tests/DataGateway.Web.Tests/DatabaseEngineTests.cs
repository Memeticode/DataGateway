using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Core.Tests;
using DataGateway.Data;
using DataGateway.Data.Queries;
using DataGateway.Data.Sqlite;
using Microsoft.Data.Sqlite;
using SqlKata.Compilers;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class DatabaseEngineTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static CompiledQuery Raw(string sql = "SELECT id FROM proof_items ORDER BY id", int keep = 3,
        bool matched = false, params OutputColumn[] columns) => new(sql, new Dictionary<string, object>(), keep, matched, columns);
    private static Task<QueryRows> Read(DatabaseEngine<SqliteConnection> engine, SqliteFixture fixture,
        CompiledQuery? query = null, Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>>? consume = null,
        Action? opened = null, CancellationToken token = default, int timeout = 7) =>
        engine.ReadSourceAsync(fixture.Connection, query ?? Raw(), timeout, opened ?? (() => { }), consume ?? SqliteEngineTests.Collect, token);
    private static ValidatedQuery Validate(SourceContract source, string json = SqliteFixture.Detail) =>
        QueryBuilding.Validate(source, json, QueryLimits.ForRowCap(3)).Value;

    [Fact]
    public async Task T1_exact_extension_surface_and_private_dispatch()
    {
        var type = typeof(DatabaseEngine<>);
        const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        Assert.True(type.IsPublic && type.IsAbstract);
        var generic = Assert.Single(type.GetGenericArguments());
        Assert.Equal(typeof(DbConnection), Assert.Single(generic.GetGenericParameterConstraints()));
        Assert.Equal(GenericParameterAttributes.DefaultConstructorConstraint,
            generic.GenericParameterAttributes & GenericParameterAttributes.SpecialConstraintMask);
        Assert.Equal(typeof(DatabaseEngine<SqliteConnection>), typeof(SqliteEngine).BaseType);
        var constructor = Assert.Single(type.GetConstructors(flags));
        Assert.True(constructor.IsFamily);
        Assert.Equal(typeof(Func<,>).MakeGenericType(typeof(string), generic), Assert.Single(constructor.GetParameters()).ParameterType);
        Assert.All(type.GetFields(flags), field => Assert.True(field.IsPrivate));
        Assert.All(type.GetProperties(flags).SelectMany(property => property.GetAccessors(true)), accessor => Assert.True(accessor.IsAbstract));
        var hooks = type.GetMethods(flags).Where(method => method.IsVirtual && !method.IsFinal).ToArray();
        Assert.Equal(new[] { "ConvertCell", "CreateCompiler", "PrepareLikePattern", "PrepareParameter", "RecognizeFailure", "ValidateSourceConnection", "get_MatchedRowCountExpression" }, hooks.Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal));
        var parameterHook = hooks.Single(method => method.Name == "PrepareParameter");
        Assert.True(parameterHook.IsFamily);
        Assert.Equal(typeof(void), parameterHook.ReturnType);
        Assert.Equal(new[] { typeof(DbParameter), typeof(object) }, parameterHook.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.All(hooks, method => Assert.True(method.IsAbstract));
        foreach (var method in type.GetMethods(flags).Except(hooks))
        {
            if (method.Name is "Compile" or "ReadSourceAsync") Assert.True(method.IsPublic && !method.IsVirtual);
            else Assert.True(method.IsPrivate, method.Name);
        }
        var capability = type.Assembly.GetType("DataGateway.Data.ICappedQueryEngine", true)!;
        var rowsType = type.Assembly.GetType("DataGateway.Data.SourceRows", true)!;
        Assert.True(capability.IsNotPublic && capability.IsInterface);
        Assert.True(rowsType.IsNotPublic && rowsType.IsSealed);
        var map = typeof(DatabaseEngine<SqliteConnection>).GetInterfaceMap(capability);
        Assert.Equal(2, map.TargetMethods.Length);
        Assert.All(map.TargetMethods, method => Assert.True(method.IsPrivate && method.IsFinal && method.IsVirtual));
        var sourceText = File.ReadAllText(TestFiles.InRepo("DataGateway.Data/DatabaseEngine.cs"));
        Assert.Equal(1, sourceText.Split(".FromFactory(", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, sourceText.Split(".BatchAsync(", StringSplitOptions.None).Length - 1);
        Assert.Contains("var context = DbContext<TConnection>.FromFactory(() => createConnection(connection));", sourceText);
        Assert.True(sourceText.IndexOf(".FromFactory(", StringComparison.Ordinal) < sourceText.IndexOf(".BatchAsync(", StringComparison.Ordinal));
        Assert.DoesNotContain(type.GetFields(flags), field => field.FieldType.Name.StartsWith("DbContext", StringComparison.Ordinal));
        using var fixture = new SqliteFixture();
        var source = await fixture.Source();
        var history = new History();
        var engine = new HidingEngine(text => new HistoryConnection(text, history));
        var runner = new QueryRunner(engine, 7);
        var compiled = runner.Compile(Validate(source));
        var result = await runner.RunAsync(source, compiled, default);
        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(3, result.Value.RowCount);
        Assert.True(result.Value.More);
        Assert.Equal(4, history.TrueReads);
        Clean(history);
        Assert.Throws<SentinelException>(() => ((IDatabaseEngine)engine).Compile(Validate(source)));
        await Assert.ThrowsAsync<SentinelException>(() => ((IDatabaseEngine)engine).ReadSourceAsync(fixture.Connection, compiled, 7, () => { }, SqliteEngineTests.Collect, default));
    }

    [Fact]
    public async Task T2_compilation_clones_prepares_before_compiler_and_execution_order()
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source();
        var history = new History();
        var engine = new TestEngine(text => { history.Hit("factory"); return new HistoryConnection(text, history); }) { Trace = history.Hit };
        var validated = Validate(source, """{"select":["id"],"filters":[{"or":[{"col":"label","op":"like","value":"%_%"},{"col":"id","op":"gte","value":2}]}],"limit":3}""");
        var before = new SqliteEngine().Compile(validated);
        var compiled = engine.Compile(validated);
        Assert.Equal(before.Sql, compiled.Sql);
        Assert.Equal(before.Parameters, compiled.Parameters);
        Assert.Equal(4, Assert.IsType<int>(compiled.Parameters["@p2"]));
        Assert.True(history.Events.IndexOf("like") < history.Events.IndexOf("compiler"));
        var aggregate = engine.Compile(Validate(source, """{"aggregates":[{"fn":"count","col":"flag"}]}"""));
        Assert.True(aggregate.HasMatchedRowCount);
        Assert.Contains("COUNT(*) AS \"__dg_matched_rows\"", aggregate.Sql);
        Assert.Equal(new SqliteEngine().Compile(validated).Parameters, compiled.Parameters);
        history.Events.Clear();
        using var token = new CancellationTokenSource();
        await Read(engine, fixture, compiled, async rows =>
        {
            var result = await SqliteEngineTests.Collect(rows);
            history.Hit("consumer-complete");
            return result;
        }, () => history.Hit("notified"), token.Token);
        var ordered = new[] { "validate", "factory", "open", "opened", "notified", "command", "execute", "schema", "convert", "consumer-complete", "reader-disposed", "command-disposed", "connection-disposed" };
        for (var i = 1; i < ordered.Length; i++) Assert.True(history.Events.IndexOf(ordered[i - 1]) < history.Events.IndexOf(ordered[i]), string.Join(",", history.Events));
        Assert.Equal(token.Token, history.OpenToken);
        Assert.Equal(token.Token, history.ExecuteToken);
        Assert.Equal(token.Token, history.ReadToken);
        Assert.Equal(7, history.Timeout);
        Assert.Equal(new[] { "@p0", "@p1", "@p2" }, history.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(@"%\_%", history.Parameters[0].Value);
        Assert.Equal(2m, Assert.IsType<decimal>(history.Parameters[1].Value));
        Assert.Equal(4, Assert.IsType<int>(history.Parameters[2].Value));
        Clean(history);
    }

    [Fact]
    public async Task T2_serial_and_overlapping_calls_have_independent_resources_tokens_and_timeouts()
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source();
        var histories = new List<History>();
        var engine = new TestEngine(text =>
        {
            var history = new History { Original = text };
            histories.Add(history);
            return new HistoryConnection(text, history);
        });
        await Read(engine, fixture);
        await Read(engine, fixture);
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var one = Read(engine, fixture, Raw("SELECT 7 AS n"), async rows =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(Bound);
            return await SqliteEngineTests.Collect(rows);
        }, token: first.Token, timeout: 7);
        try
        {
            await entered.Task.WaitAsync(Bound);
            var two = await new QueryRunner(engine, 41).RunAsync(source, Raw("SELECT 41 AS n"), second.Token);
            Assert.True(two.IsSuccess);
            Assert.Equal(41L, two.Value.Rows[0]["n"]);
            Assert.False(one.IsCompleted);
            Assert.Equal(7, histories[2].Timeout);
            Assert.Equal(41, histories[3].Timeout);
            Assert.Equal(first.Token, histories[2].ExecuteToken);
            DeadlineToken.Passed(second.Token, histories[3].ExecuteToken);
        }
        finally { release.TrySetResult(); await one.WaitAsync(Bound); }
        Assert.Equal(4, histories.Count);
        Assert.Equal(4, histories.Select(history => history.Connection).Distinct().Count());
        Assert.All(histories, history => { Assert.Equal(fixture.Connection, history.Original); Clean(history); });
        Assert.Equal(7L, (await one).Rows[0]["n"]);
    }

    public static IEnumerable<object[]> CapCases()
    {
        foreach (var leaf in new[] { false, true })
        foreach (var keep in new[] { 1, 3 })
        foreach (var available in new[] { 0, keep, keep + 1, 5 }.Distinct()) yield return [leaf, keep, available];
    }

    [Theory]
    [MemberData(nameof(CapCases))]
    public async Task T3_cap_and_eof_read_counts(bool leaf, int keep, int available)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source();
        var probe = new SqliteProbe();
        IDatabaseEngine engine = leaf ? new SqliteEngine(probe.Create) : new TestEngine(probe.Create);
        var runner = new QueryRunner(engine, 7);
        var validated = Validate(source, $$"""{"select":["id"],"filters":[{"col":"id","op":"lte","value":{{available}}}],"limit":{{keep}}}""");
        var query = runner.Compile(validated);
        Assert.Equal(keep + 1, Assert.IsType<int>(query.Parameters.Last().Value));
        var result = await runner.RunAsync(source, query, default);
        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(Math.Min(available, keep), result.Value.RowCount);
        Assert.Equal(available > keep, result.Value.More);
        Assert.False(result.Value.NoMatchingRows);
        Assert.Equal(Math.Min(available, keep + 1), probe.TrueReads);
        Assert.Equal(Math.Min(available, keep) + 1, probe.Reads);
        Assert.Equal(available <= keep, probe.Events.Contains("eof"));
        SqliteEngineTests.Clean(probe);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T3_raw_callback_is_uncapped_and_detached_rows_survive(bool leaf)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source();
        var engine = leaf ? (IDatabaseEngine)new SqliteEngine() : new TestEngine(text => new SqliteConnection(text));
        var query = Raw();
        var capped = await new QueryRunner(engine, 7).RunAsync(source, query, default);
        Assert.True(capped.IsSuccess);
        Assert.Equal(3, capped.Value.RowCount);
        Assert.True(capped.Value.More);
        var raw = await engine.ReadSourceAsync(fixture.Connection, query, 7, () => { }, async rows =>
        {
            var collected = await SqliteEngineTests.Collect(rows);
            Assert.Equal(5, collected.RowCount);
            return collected;
        }, default);
        Assert.Equal(5, raw.RowCount);
        Assert.False(raw.More);
        raw.Rows[0]["id"] = "changed";
        Assert.Equal(2L, raw.Rows[1]["id"]);
        Assert.Equal(1L, capped.Value.Rows[0]["id"]);
        // Invoke the real private keep loop on a captured raw sequence to prove dictionary identity.
        var baseEngine = leaf ? (DatabaseEngine<SqliteConnection>)engine : (TestEngine)engine;
        List<IDictionary<string, object?>> seen = [];
        var kept = await Read(baseEngine, fixture, consume: async rows =>
        {
            async IAsyncEnumerable<IDictionary<string, object?>> Capture()
            {
                await foreach (var row in rows) { seen.Add(row); yield return row; }
            }
            return await DataGateway.Data.Queries.KeptRows.KeepAsync(Capture(), query, CancellationToken.None);
        });
        Assert.Equal(4, seen.Count);
        for (var i = 0; i < 3; i++) Assert.Same(seen[i], kept.Rows[i]);
        kept.Rows[0]["new"] = true;
        Assert.True((bool)seen[0]["new"]!);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(41)]
    public async Task T4_timeout_and_protected_int32_binding(int timeout)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source();
        var history = new History();
        var observed = new List<object?>();
        var engine = new TestEngine(text => new HistoryConnection(text, history))
        {
            Parameter = value => { observed.Add(value); return value is int ? (999L, DbType.Int64) : value is bool flag ? (flag ? 1L : 0L, DbType.Int64) : (value, null); }
        };
        var runner = new QueryRunner(engine, timeout);
        var query = runner.Compile(Validate(source, """{"select":["id"],"filters":[{"col":"id","op":"gte","value":2},{"col":"label","op":"like","value":"%_%"},{"col":"flag","op":"in","value":[true,false]}],"limit":3}"""));
        Assert.True((await runner.RunAsync(source, query, default)).IsSuccess);
        Assert.Equal(timeout, history.Timeout);
        Assert.DoesNotContain(observed, value => value is int);
        Assert.Equal(new object?[] { 2m, @"%\_%", true, false }, observed);
        Assert.Equal(new[] { "@p0", "@p1", "@p2", "@p3", "@p4" }, history.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(2m, Assert.IsType<decimal>(history.Parameters[0].Value));
        Assert.Equal(@"%\_%", Assert.IsType<string>(history.Parameters[1].Value));
        Assert.Equal(1L, Assert.IsType<long>(history.Parameters[2].Value));
        Assert.Equal(DbType.Int64, history.Parameters[2].Type);
        Assert.Equal(0L, Assert.IsType<long>(history.Parameters[3].Value));
        Assert.Equal(DbType.Int64, history.Parameters[3].Type);
        var fetch = history.Parameters[4];
        Assert.Equal(4, Assert.IsType<int>(fetch.Value));
        using var fresh = new SqliteCommand();
        var parameter = fresh.CreateParameter();
        parameter.Value = 4;
        Assert.Equal(parameter.DbType, fetch.Type);
        Clean(history);
    }

    [Theory]
    [InlineData("open", "SourceUnavailable", "$.source", "connect")]
    [InlineData("execute", "QueryTimeout", "$.query", "command")]
    public async Task T4_timeout_identity_and_phase(string stage, string code, string path, string step)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source();
        var error = new TimeoutException("synthetic timeout");
        var history = new History();
        history.Failures.Add(stage, error);
        var result = await new QueryRunner(new TestEngine(text => new HistoryConnection(text, history)), 41).RunAsync(source, Raw(), default);
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.Failure!.Code);
        Assert.Equal(path, result.Failure!.Path);
        Assert.Equal(step, result.Failure!.Step);
        Assert.Same(error, result.Failure!.DriverException);
        if (stage == "execute") Assert.Contains("41-second command timeout", result.Failure!.Message);
        Clean(history, stage == "open" ? 0 : 1, 0);
    }

    [Fact]
    public async Task T5_lazy_factory_and_argument_refusals()
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source();
        var count = 0;
        var engine = new TestEngine(text => { count++; return new SqliteConnection(text); });
        Assert.Equal(0, count);
        engine.Compile(Validate(source));
        engine.ValidateSourceConnection(fixture.Connection);
        Assert.Equal(0, count);
        Assert.Throws<ArgumentNullException>(() => new TestEngine(null!));
        Assert.Throws<ArgumentNullException>(() => engine.Compile(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.ReadSourceAsync(fixture.Connection, null!, 7, () => { }, SqliteEngineTests.Collect, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.ReadSourceAsync(fixture.Connection, Raw(), 7, null!, SqliteEngineTests.Collect, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.ReadSourceAsync(fixture.Connection, Raw(), 7, () => { }, null!, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Read(engine, fixture, Raw(" ")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Read(engine, fixture, timeout: 0));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.ReadSourceAsync("", Raw(), 7, () => { }, SqliteEngineTests.Collect, default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(engine, fixture, token: cancelled.Token));
        Assert.Equal(0, count);
        await Read(engine, fixture);
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("argument", "InvalidCatalog")]
    [InlineData("format", "InvalidCatalog")]
    [InlineData("io", "SourceUnavailable")]
    [InlineData("operation", "SourceUnavailable")]
    [InlineData("unexpected", null)]
    public async Task T5_factory_failures_preserve_identity(string kind, string? code)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source();
        Exception error = kind switch { "argument" => new ArgumentException("synthetic"), "format" => new FormatException("synthetic"), "io" => new IOException("synthetic"), "operation" => new InvalidOperationException("synthetic"), _ => new SentinelException() };
        var calls = 0;
        var engine = new TestEngine(text => { Assert.Equal(fixture.Connection, text); calls++; ThrowNamedFrame(error); return null!; });
        var runner = new QueryRunner(engine, 7);
        if (code is null) Assert.Same(error, await Assert.ThrowsAsync<SentinelException>(() => runner.RunAsync(source, Raw(), default)));
        else
        {
            var result = await runner.RunAsync(source, Raw(), default);
            Assert.Equal(code, result.Failure!.Code);
            Assert.Equal("connect", result.Failure!.Step);
            Assert.Equal("$.source", result.Failure!.Path);
            Assert.Same(error, result.Failure!.DriverException);
        }
        Assert.Equal(1, calls);
        Assert.Contains(nameof(ThrowNamedFrame), error.StackTrace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T5_null_and_open_results_are_rejected_without_taking_ownership(bool open)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source();
        var history = new History();
        var connection = new HistoryConnection(fixture.Connection, history);
        if (open) connection.Open();
        try
        {
            var calls = 0;
            var engine = new TestEngine(text => { Assert.Equal(fixture.Connection, text); calls++; return open ? connection : null!; });
            var result = await new QueryRunner(engine, 7).RunAsync(source, Raw(), default);
            Assert.Equal(1, calls);
            Assert.Equal("SourceUnavailable", result.Failure!.Code);
            Assert.Equal("connect", result.Failure!.Step);
            var error = Assert.IsType<InvalidOperationException>(result.Failure!.DriverException);
            Assert.Equal(open ? "The connection factory returned a connection that is not closed (Open). It must return a new, closed connection for each call." : "The connection factory returned null. It must return a new, closed connection for each call.", error.Message);
            Assert.Empty(history.Events);
            if (open) Assert.Equal(ConnectionState.Open, connection.State);
        }
        finally { await connection.DisposeAsync(); }
    }

    [Theory]
    [InlineData("0", false, true)]
    [InlineData("7", false, false)]
    [InlineData("NULL", false, null)]
    [InlineData("-1", false, null)]
    [InlineData("'7'", false, null)]
    [InlineData("1.5", false, null)]
    [InlineData("7", true, null)]
    [InlineData("missing", false, null)]
    public async Task T6_kept_match_evidence_is_validated_and_private(string expression, bool int32, bool? noMatches)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source();
        var history = new History();
        var engine = new TestEngine(text => new HistoryConnection(text, history))
        { Cell = (value, column) => int32 && value is long number ? (object)(int)number : value };
        var sql = expression == "missing" ? "SELECT NULL AS n" : $"SELECT {expression} AS __dg_matched_rows, NULL AS n";
        if (noMatches is null)
        {
            // Bad evidence is DataGateway's own broken assumption: an unexpected error, never a source outage.
            var error = await Assert.ThrowsAsync<DataGateway.Data.GatewayInvariantException>(() =>
                new QueryRunner(engine, 7).RunAsync(source, Raw(sql, matched: true), default));
            Assert.Equal("The query row is missing its matched-row count.", error.Message);
            Clean(history);
            return;
        }
        var result = await new QueryRunner(engine, 7).RunAsync(source, Raw(sql, matched: true), default);
        if (noMatches is { } expected)
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(expected, result.Value.NoMatchingRows);
            Assert.False(result.Value.More);
            Assert.Equal(1, result.Value.RowCount);
            Assert.False(result.Value.Rows[0].ContainsKey(CompiledQuery.MatchedRowCountColumn));
            Assert.Null(result.Value.Rows[0]["n"]);
        }
        else
        {
            Assert.Equal("SourceUnavailable", result.Failure!.Code);
            Assert.Equal("$.source", result.Failure!.Path);
            Assert.Equal("command", result.Failure!.Step);
            Assert.Equal("The query row is missing its matched-row count.", result.Failure!.DriverException!.Message);
        }
        Clean(history);
    }

    [Fact]
    public async Task T6_probe_evidence_is_not_examined_and_raw_callback_keeps_it()
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source();
        var history = new History();
        var engine = new TestEngine(text => new HistoryConnection(text, history));
        var query = Raw("SELECT 1 AS __dg_matched_rows UNION ALL SELECT NULL UNION ALL SELECT -1", 1, true);
        var result = await new QueryRunner(engine, 7).RunAsync(source, query, default);
        Assert.True(result.IsSuccess);
        Assert.True(result.Value.More);
        Assert.False(result.Value.NoMatchingRows);
        Assert.Empty(Assert.Single(result.Value.Rows));
        Assert.Equal(2, history.TrueReads);
        Clean(history);
        var raw = await Read(new SqliteEngine(), fixture, query);
        Assert.Equal(3, raw.RowCount);
        Assert.Equal(1L, raw.Rows[0][CompiledQuery.MatchedRowCountColumn]);
        Assert.Null(raw.Rows[1][CompiledQuery.MatchedRowCountColumn]);
    }

    public static IEnumerable<object[]> PrimaryCases()
    {
        foreach (var capped in new[] { false, true })
        foreach (var stage in new[] { "open", "notified", "command", "parameter", "prepare", "execute", "schema", "read", "convert", "consumer" })
            if (!capped || stage is not ("notified" or "consumer")) yield return [capped, stage];
    }

    [Theory]
    [MemberData(nameof(PrimaryCases))]
    public async Task T7_primary_failure_survives_all_cleanup_failures(bool capped, string stage)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source();
        var inner = new SentinelException();
        var primary = new IOException("primary " + stage, inner);
        var history = new History();
        history.Failures[stage] = primary;
        foreach (var resource in new[] { "reader", "command", "connection" }) history.Failures[resource + "-disposed"] = new IOException("cleanup " + resource);
        var engine = new TestEngine(text => new HistoryConnection(text, history)) { Trace = history.Hit };
        Exception? actual;
        if (capped)
        {
            var result = await new QueryRunner(engine, 7).RunAsync(source,
                new CompiledQuery("SELECT @p AS n", new Dictionary<string, object> { ["@p"] = "value" }, 3, false, []), default);
            Assert.False(result.IsSuccess);
            actual = result.Failure!.DriverException;
        }
        else actual = await Assert.ThrowsAsync<IOException>(() => Read(engine, fixture,
            new CompiledQuery("SELECT @p AS n", new Dictionary<string, object> { ["@p"] = "value" }, 3, false, []),
            rows => { history.Hit("consumer"); return SqliteEngineTests.Collect(rows); }, () => history.Hit("notified")));
        Assert.Same(primary, actual);
        Assert.Same(inner, actual!.InnerException);
        Assert.Contains(nameof(ThrowNamedFrame), actual.StackTrace);
        Clean(history, history.Events.Contains("command-created") ? 1 : 0, history.Events.Contains("reader") ? 1 : 0);
    }

    [Theory]
    [InlineData(false, "reader")]
    [InlineData(false, "command")]
    [InlineData(false, "connection")]
    [InlineData(false, "all")]
    [InlineData(true, "reader")]
    [InlineData(true, "command")]
    [InlineData(true, "connection")]
    [InlineData(true, "all")]
    public async Task T7_success_cleanup_first_failure_and_awaited_gates(bool capped, string resource)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source();
        var history = new History { Gate = resource == "all" ? "reader" : resource };
        foreach (var name in new[] { "reader", "command", "connection" })
            if (resource == "all" || name == resource) history.Failures[name + "-disposed"] = new IOException(name);
        var primary = history.Failures[(resource == "all" ? "reader" : resource) + "-disposed"];
        var engine = new TestEngine(text => new HistoryConnection(text, history));
        var operation = capped
            ? InspectCapped()
            : InspectRaw();
        async Task<Exception> InspectCapped()
        {
            var result = await new QueryRunner(engine, 7).RunAsync(source, Raw(), default);
            Assert.Equal("SourceUnavailable", result.Failure!.Code);
            return result.Failure!.DriverException!;
        }
        async Task<Exception> InspectRaw() => await Assert.ThrowsAsync<IOException>(() => Read(engine, fixture));
        try
        {
            await history.Entered.Task.WaitAsync(Bound);
            Assert.False(operation.IsCompleted);
            Assert.DoesNotContain(history.Gate + "-disposed", history.Events);
        }
        finally { history.Release.TrySetResult(); }
        Assert.Same(primary, await operation.WaitAsync(Bound));
        Clean(history);
    }

    [Theory]
    [InlineData("pre", null, false)]
    [InlineData("open", "connect", true)]
    [InlineData("opened", "command", true)]
    [InlineData("row", "command", true)]
    [InlineData("eof", "command", false)]
    [InlineData("connection-disposed", "command", false)]
    public async Task T8_cancellation_phases_and_post_success_check(string stage, string? step, bool driver)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source();
        using var caller = new CancellationTokenSource();
        var history = new History { OpenWait = stage == "open" };
        history.OnEvent = name => { if (name == stage && stage != "open") caller.Cancel(); };
        var factoryCalls = 0;
        var engine = new TestEngine(text => { factoryCalls++; return new HistoryConnection(text, history); });
        if (stage == "pre") caller.Cancel();
        var run = new QueryRunner(engine, 7).RunAsync(source, Raw("SELECT id FROM proof_items WHERE id <= 2"), caller.Token);
        if (stage == "open")
        {
            try { await history.Entered.Task.WaitAsync(Bound); caller.Cancel(); }
            finally { history.Release.TrySetResult(); }
        }
        var result = await run.WaitAsync(Bound);
        Assert.Equal("QueryCancelled", result.Failure!.Code);
        Assert.Equal("$.query", result.Failure!.Path);
        Assert.Equal(step, result.Failure!.Step);
        Assert.Equal(driver, result.Failure!.DriverException is not null);
        Assert.Equal(stage == "pre" ? 0 : 1, factoryCalls);
        if (stage != "pre")
        {
            DeadlineToken.Passed(caller.Token, history.OpenToken);
            if (stage != "open") DeadlineToken.Passed(caller.Token, history.ExecuteToken);
            Clean(history, stage == "open" ? 0 : 1, stage is "open" or "opened" ? 0 : 1);
        }
    }

    [Fact]
    public async Task T8_distinct_enumeration_token_is_linked_and_raw_eof_keeps_its_contract()
    {
        using var fixture = new SqliteFixture();
        using var operation = new CancellationTokenSource();
        using var enumeration = new CancellationTokenSource();
        var history = new History();
        var engine = new TestEngine(text => new HistoryConnection(text, history));
        await Read(engine, fixture, consume: async rows =>
        {
            await using var enumerator = rows.GetAsyncEnumerator(enumeration.Token);
            Assert.True(await enumerator.MoveNextAsync());
            Assert.NotEqual(operation.Token, history.ReadToken);
            Assert.NotEqual(enumeration.Token, history.ReadToken);
            var linked = history.ReadToken;
            enumeration.Cancel();
            Assert.True(linked.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
            return new QueryRows([], false);
        }, token: operation.Token);
        Assert.False(operation.IsCancellationRequested);
        Assert.Equal(operation.Token, history.OpenToken);
        Assert.Equal(operation.Token, history.ExecuteToken);
        Clean(history);
        using var end = new CancellationTokenSource();
        var eofHistory = new History { OnEvent = name => { if (name == "eof") end.Cancel(); } };
        var raw = await Read(new TestEngine(text => new HistoryConnection(text, eofHistory)), fixture,
            Raw("SELECT 1 AS n WHERE 0"), token: end.Token);
        Assert.Equal(0, raw.RowCount);
        Assert.True(end.IsCancellationRequested);
        Clean(eofHistory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T8_live_token_oce_and_unexpected_faults_propagate(bool unexpected)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source();
        using var caller = new CancellationTokenSource();
        Exception primary = unexpected ? new SentinelException() : new OperationCanceledException("independent", caller.Token);
        var history = new History();
        history.Failures["execute"] = primary;
        if (unexpected) history.OnEvent = stage => { if (stage == "execute") caller.Cancel(); };
        var runner = new QueryRunner(new TestEngine(text => new HistoryConnection(text, history)), 7);
        var thrown = await Record.ExceptionAsync(() => runner.RunAsync(source, Raw(), caller.Token));
        Assert.Same(primary, thrown);
        Clean(history, 1, 0);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("full")]
    [InlineData("early")]
    [InlineData("failure")]
    [InlineData("dispose")]
    public async Task T9_row_leases_expire_and_detached_values_survive(string mode)
    {
        using var fixture = new SqliteFixture();
        var history = new History();
        var engine = new TestEngine(text => new HistoryConnection(text, history));
        IAsyncEnumerable<IDictionary<string, object?>>? saved = null;
        IAsyncEnumerator<IDictionary<string, object?>>? lease = null;
        IDictionary<string, object?>? detached = null;
        var primary = new SentinelException();
        async Task<QueryRows> Consume(IAsyncEnumerable<IDictionary<string, object?>> rows)
        {
            saved = rows;
            if (mode == "zero") return new QueryRows([], false);
            lease = rows.GetAsyncEnumerator();
            Assert.Equal("Source rows are not positioned on a row.", Assert.Throws<InvalidOperationException>(() => lease.Current).Message);
            Assert.Equal("Source rows can only be enumerated once.", Assert.Throws<InvalidOperationException>(() => rows.GetAsyncEnumerator()).Message);
            Assert.True(await lease.MoveNextAsync());
            detached = lease.Current;
            Assert.Same(detached, lease.Current);
            if (mode == "failure") ThrowNamedFrame(primary);
            if (mode == "full")
            {
                while (await lease.MoveNextAsync()) { }
                Assert.False(await lease.MoveNextAsync());
                Assert.Equal("Source rows are not positioned on a row.", Assert.Throws<InvalidOperationException>(() => lease.Current).Message);
            }
            await lease.DisposeAsync();
            Assert.False(await lease.MoveNextAsync());
            Assert.Throws<InvalidOperationException>(() => lease.Current);
            return new QueryRows([detached], false);
        }
        if (mode == "failure") Assert.Same(primary, await Assert.ThrowsAsync<SentinelException>(() => Read(engine, fixture, consume: Consume)));
        else await Read(engine, fixture, consume: Consume);
        Assert.Equal("DataGateway.Data.SourceRows", Assert.Throws<ObjectDisposedException>(() => saved!.GetAsyncEnumerator()).ObjectName);
        if (lease is not null)
        {
            Assert.Equal("DataGateway.Data.SourceRows", Assert.Throws<ObjectDisposedException>(() => lease.Current).ObjectName);
            Assert.Equal("DataGateway.Data.SourceRows", (await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.MoveNextAsync().AsTask())).ObjectName);
            await lease.DisposeAsync();
        }
        if (detached is not null) { detached["id"] = 999; Assert.Equal(999, detached["id"]); }
        Clean(history);
    }

    [Fact]
    public async Task T9_schema_refusal_case_sensitive_order_nulls_and_metadata()
    {
        using var fixture = new SqliteFixture();
        var history = new History();
        var engine = new TestEngine(text => new HistoryConnection(text, history));
        var error = await Assert.ThrowsAsync<DataGateway.Data.GatewayInvariantException>(() => Read(engine, fixture, Raw("SELECT 1 AS a, 2 AS a WHERE 0")));
        Assert.Equal("The source row has duplicate column names.", error.Message);
        Assert.Equal(0, history.Reads);
        Clean(history);
        var rows = await Read(new SqliteEngine(), fixture,
            Raw("SELECT 1 AS a, 2 AS A, NULL AS nullable, 1 AS flag UNION ALL SELECT 3, 4, NULL, 0", columns: [new OutputColumn("flag", "bool"), new OutputColumn("nullable", "bool")]));
        Assert.Equal(new[] { "a", "A", "nullable", "flag" }, rows.Rows[0].Keys);
        Assert.Equal(1L, rows.Rows[0]["a"]);
        Assert.Equal(2L, rows.Rows[0]["A"]);
        Assert.Null(rows.Rows[0]["nullable"]);
        Assert.Equal(true, rows.Rows[0]["flag"]);
        Assert.Equal(false, rows.Rows[1]["flag"]);
        Assert.IsType<OrderedDictionary<string, object?>>(rows.Rows[0]);
        rows.Rows[0]["a"] = 100L;
        Assert.Equal(3L, rows.Rows[1]["a"]);
        var conversions = 0;
        var neutral = new TestEngine(text => new SqliteConnection(text)) { Cell = (value, column) => { conversions++; return value; } };
        await Read(neutral, fixture, Raw("SELECT NULL AS n"));
        Assert.Equal(0, conversions);
    }

    [Fact]
    public async Task T9_duplicate_declared_columns_fail_before_reads_and_cleanup_fully()
    {
        using var fixture = new SqliteFixture();
        var history = new History();
        var engine = new TestEngine(text => new HistoryConnection(text, history));
        var opened = 0;
        var consumed = false;
        var query = Raw("SELECT 1 AS x, 2 AS y", columns: [new("x", "number"), new("x", "number")]);
        var error = await Assert.ThrowsAsync<DataGateway.Data.GatewayInvariantException>(() =>
            Read(engine, fixture, query, consume: _ =>
            {
                consumed = true;
                return Task.FromResult(new QueryRows([], false));
            }, opened: () => opened++));
        Assert.Equal("The source row has duplicate column names.", error.Message);
        Assert.Equal(1, opened);
        Assert.False(consumed);
        Assert.Equal(0, history.Reads);
        Clean(history);
    }

    [Fact]
    public void T10_sqlite_hooks_are_the_seven_small_provider_differences()
    {
        var engine = new SqliteEngine();
        object? Hook(string name, params object?[] args)
        {
            try { return typeof(SqliteEngine).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
        }
        Assert.IsType<SqliteCompiler>(Hook("CreateCompiler"));
        Assert.NotSame(Hook("CreateCompiler"), Hook("CreateCompiler"));
        Assert.Equal("COUNT(*) AS \"__dg_matched_rows\"", Hook("get_MatchedRowCountExpression"));
        Assert.Equal((@"%\_[x]\\", "\\"), Hook("PrepareLikePattern", @"%_[x]\"));
        foreach (var flag in new[] { true, false })
        {
            var parameter = new ProbeParameter { ParameterName = "@p", DbType = DbType.Guid };
            Hook("PrepareParameter", parameter, flag);
            Assert.Equal(flag ? 1L : 0L, Assert.IsType<long>(parameter.Value));
            Assert.Equal(DbType.Int64, parameter.DbType);
            Assert.Equal("@p", parameter.ParameterName);
        }
        foreach (var value in new object?[] { null, DBNull.Value, 4, 2m, "text", new object() })
        {
            var parameter = new ProbeParameter { ParameterName = "@p", DbType = DbType.Guid };
            Hook("PrepareParameter", parameter, value);
            Assert.Same(value ?? DBNull.Value, parameter.Value);
            Assert.Equal(DbType.Guid, parameter.DbType);
            Assert.Equal("@p", parameter.ParameterName);
        }
        var boolean = new OutputColumn("flag", "bool");
        Assert.Equal(false, Hook("ConvertCell", 0L, boolean));
        Assert.Equal(true, Hook("ConvertCell", 1L, boolean));
        foreach (var value in new object[] { 2L, 1m, 1 })
            Assert.Equal("The source boolean cell is invalid.", Assert.Throws<InvalidCastException>(() => Hook("ConvertCell", value, boolean)).Message);
        var unchanged = new object();
        Assert.Same(unchanged, Hook("ConvertCell", unchanged, null));
        Assert.Equal(1L, Hook("ConvertCell", 1L, new OutputColumn("n", "number")));
        Assert.Null(engine.RecognizeFailure(new IOException()));
        Assert.Throws<ArgumentNullException>(() => engine.RecognizeFailure(null!));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNamedFrame(Exception error) => throw error;
    private sealed class SentinelException : Exception;

    private class TestEngine(Func<string, SqliteConnection> factory) : DatabaseEngine<SqliteConnection>(factory), IDatabaseEngine
    {
        string IDatabaseEngine.ProviderName => "sqlite";
        internal Action<string>? Trace;
        internal Func<object?, (object? Value, DbType? Type)>? Parameter;
        internal Func<object, OutputColumn?, object>? Cell;
        protected override Compiler CreateCompiler() { Trace?.Invoke("compiler"); return new SqliteCompiler(); }
        protected override string MatchedRowCountExpression { get { Trace?.Invoke("count-expression"); return "COUNT(*) AS \"__dg_matched_rows\""; } }
        protected override (string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern)
        {
            Trace?.Invoke("like");
            return (pattern.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal), "\\");
        }
        protected override void PrepareParameter(DbParameter parameter, object? value)
        {
            Trace?.Invoke("prepare");
            (object? Value, DbType? Type) prepared = Parameter is not null ? Parameter(value) : (value, null);
            if (prepared.Type is { } type) parameter.DbType = type;
            parameter.Value = prepared.Value ?? DBNull.Value;
        }
        protected override object ConvertCell(object value, OutputColumn? column)
        {
            Trace?.Invoke("convert");
            return Cell is not null ? Cell(value, column) : value;
        }
        public override void ValidateSourceConnection(string? connection) { Trace?.Invoke("validate"); new SqliteEngine().ValidateSourceConnection(connection); }
        public override ProviderFailure? RecognizeFailure(Exception error) => new SqliteEngine().RecognizeFailure(error);
    }

    private sealed class HidingEngine(Func<string, SqliteConnection> factory) : TestEngine(factory), IDatabaseEngine
    {
        string IDatabaseEngine.ProviderName => "sqlite";
        public new CompiledQuery Compile(ValidatedQuery query) => throw new SentinelException();
        public new Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int timeout, Action opened,
            Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken token) => throw new SentinelException();
        CompiledQuery IDatabaseEngine.Compile(ValidatedQuery query) => Compile(query);
        Task<QueryRows> IDatabaseEngine.ReadSourceAsync(string connection, CompiledQuery query, int timeout, Action opened,
            Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken token) => ReadSourceAsync(connection, query, timeout, opened, consume, token);
    }

    private sealed class History
    {
        internal readonly List<string> Events = [];
        internal readonly Dictionary<string, Exception> Failures = [];
        internal readonly List<(string Name, object? Value, DbType Type)> Parameters = [];
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action<string>? OnEvent;
        internal string? Gate, Original;
        internal bool OpenWait;
        internal int Reads, TrueReads, Timeout;
        internal CancellationToken OpenToken, ExecuteToken, ReadToken;
        internal HistoryConnection? Connection;
        internal void Hit(string name)
        {
            Events.Add(name);
            OnEvent?.Invoke(name);
            if (Failures.TryGetValue(name, out var error)) ThrowNamedFrame(error);
        }
        internal async Task BeforeCleanup(string resource)
        {
            if (Gate != resource) return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(Bound);
        }
    }

    private static void Clean(History history, int commands = 1, int readers = 1)
    {
        var expected = new List<string>();
        if (readers != 0) expected.Add("reader-disposed");
        if (commands != 0) expected.Add("command-disposed");
        expected.Add("connection-disposed");
        Assert.Equal(expected, history.Events.Where(name => name.EndsWith("-disposed", StringComparison.Ordinal)));
        Assert.Equal(ConnectionState.Closed, history.Connection!.State);
    }

    private sealed class HistoryConnection : SqliteConnection
    {
        private readonly History history;
        internal HistoryConnection(string text, History history) : base(text) { this.history = history; history.Connection = this; }
        public override async Task OpenAsync(CancellationToken cancellationToken)
        {
            history.OpenToken = cancellationToken;
            history.Hit("open");
            if (history.OpenWait)
            {
                history.Entered.TrySetResult();
                await history.Release.Task.WaitAsync(Bound, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await base.OpenAsync(cancellationToken);
            history.Hit("opened");
        }
        protected override DbCommand CreateDbCommand()
        {
            history.Hit("command");
            var command = new HistoryCommand(base.CreateDbCommand(), history);
            history.Hit("command-created");
            return command;
        }
        public override async ValueTask DisposeAsync()
        {
            await history.BeforeCleanup("connection");
            await base.DisposeAsync();
            history.Hit("connection-disposed");
        }
    }

    private sealed class HistoryCommand(DbCommand inner, History history) : DbCommand
    {
        internal bool IsDisposed;
        [AllowNull] public override string CommandText { get => inner.CommandText; set => inner.CommandText = value!; }
        public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
        public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
        public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
        public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
        protected override DbConnection? DbConnection { get => inner.Connection; set => inner.Connection = value; }
        protected override DbTransaction? DbTransaction { get => inner.Transaction; set => inner.Transaction = value; }
        protected override DbParameterCollection DbParameterCollection => inner.Parameters;
        protected override DbParameter CreateDbParameter() { history.Hit("parameter"); return inner.CreateParameter(); }
        public override void Cancel() => inner.Cancel();
        public override int ExecuteNonQuery() => inner.ExecuteNonQuery();
        public override object? ExecuteScalar() => inner.ExecuteScalar();
        public override void Prepare() => inner.Prepare();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => inner.ExecuteReader(behavior);
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            history.Timeout = inner.CommandTimeout;
            history.ExecuteToken = cancellationToken;
            foreach (DbParameter parameter in inner.Parameters)
                history.Parameters.Add((parameter.ParameterName, parameter.Value, parameter.DbType));
            history.Hit("execute");
            cancellationToken.ThrowIfCancellationRequested();
            var reader = new HistoryReader(await inner.ExecuteReaderAsync(behavior, cancellationToken), history);
            history.Hit("reader");
            return reader;
        }
        public override async ValueTask DisposeAsync()
        {
            if (IsDisposed) throw new InvalidOperationException("Repeated outer command disposal.");
            await history.BeforeCleanup("command");
            await inner.DisposeAsync();
            IsDisposed = true;
            history.Hit("command-disposed");
        }
    }

    private sealed class HistoryReader(DbDataReader inner, History history) : DbDataReader
    {
        internal int ReadAttempts, SuccessfulReads;
        internal bool IsDisposed;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int Depth => inner.Depth;
        public override int RecordsAffected => inner.RecordsAffected;
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override string GetName(int ordinal) { history.Hit("schema"); return inner.GetName(ordinal); }
        public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            history.Reads++;
            ReadAttempts++;
            history.ReadToken = cancellationToken;
            history.Hit("read");
            cancellationToken.ThrowIfCancellationRequested();
            var result = await inner.ReadAsync(cancellationToken);
            history.Hit(result ? "row" : "eof");
            if (result) { history.TrueReads++; SuccessfulReads++; }
            return result;
        }
        public override bool Read() => inner.Read();
        public override bool NextResult() => throw new SentinelException();
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => throw new SentinelException();
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
        public override void Close() => inner.Close();
        public override async ValueTask DisposeAsync()
        {
            if (IsDisposed) throw new InvalidOperationException("Repeated outer reader disposal.");
            await history.BeforeCleanup("reader");
            await inner.DisposeAsync();
            IsDisposed = true;
            history.Hit("reader-disposed");
        }
    }

}
