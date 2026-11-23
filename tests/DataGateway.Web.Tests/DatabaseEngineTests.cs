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

/// <summary>Pins the shared engine base, <see cref="DatabaseEngine{TConnection}"/>: its small set of provider hooks,
/// the order it opens, runs and disposes resources, the row cap, match evidence, cancellation, failure mapping and
/// the lifetime of the rows it streams.</summary>
/// <remarks>
/// <para>TERM keep: the number of rows a compiled query keeps. The engine fetches one row more than it keeps; that
/// extra probe row only says whether there are more rows. TERM match evidence: the hidden matched-row count column
/// an aggregate query carries, read from the first kept row to say whether any row matched.</para>
/// <para>The tests run on a real SQLite database (<see cref="SqliteFixture"/>) through wrappers that record every
/// connection, command and reader event (<see cref="History"/>), so they can assert order and cleanup, and inject a
/// failure or a pause at any named step.</para>
/// </remarks>
public sealed class DatabaseEngineTests
{
    /// <summary>The longest any test waits on a gate, so a broken handshake fails instead of hanging the run.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    /// <summary>A hand-written compiled query, bypassing validation, for tests that need SQL the query language cannot
    /// produce.</summary>
    /// <param name="sql">The SQL to run.</param>
    /// <param name="keep">How many rows to keep.</param>
    /// <param name="matched">Whether the first row carries match evidence.</param>
    /// <param name="columns">The declared output columns, which drive cell conversion.</param>
    private static CompiledQuery Raw(string sql = "SELECT id FROM proof_items ORDER BY id", int keep = 3,
        bool matched = false, params OutputColumn[] columns) => new(sql, new Dictionary<string, object>(), keep, matched, columns);
    /// <summary>Calls the engine's raw read on the fixture database with defaults for every argument a test does not set.</summary>
    private static Task<QueryRows> Read(DatabaseEngine<SqliteConnection> engine, SqliteFixture fixture,
        CompiledQuery? query = null, Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>>? consume = null,
        Action? opened = null, CancellationToken token = default, int timeout = 7) =>
        engine.ReadSourceAsync(fixture.Connection, query ?? Raw(), timeout, opened ?? (() => { }), consume ?? SqliteEngineTests.Collect, token);
    /// <summary>Validates query JSON against <paramref name="source"/> with a row cap of 3.</summary>
    private static ValidatedQuery Validate(SourceContract source, string json = SqliteFixture.Detail) =>
        QueryBuilding.Validate(source, json, QueryLimits.ForRowCap(3)).Value;

    /// <summary>The base class exposes exactly seven abstract hooks plus <c>Compile</c> and <c>ReadSourceAsync</c>, and
    /// the runner reaches the base implementation even when a subclass hides those methods with <c>new</c>.</summary>
    /// <remarks>The surface is pinned by reflection because it is the contract an engine author implements (the five
    /// protected hooks and two public checks the engine guide lists): a change here changes what every provider must
    /// write. The source-text checks pin that each read creates its own
    /// connection context from the factory before running the batch.</remarks>
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
        var source = await fixture.Source(7);
        var history = new History();
        var engine = new HidingEngine(text => new HistoryConnection(text, history));
        var runner = new QueryRunner(engine);
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

    /// <summary>Compiling matches the SQLite engine's output, prepares LIKE patterns before the compiler runs, and a read
    /// runs validate, factory, open, notify, command, execute, read and dispose in that order with the caller's token
    /// and timeout.</summary>
    [Fact]
    public async Task T2_compilation_clones_prepares_before_compiler_and_execution_order()
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source(7);
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

    /// <summary>Each call, serial or overlapping, gets its own connection, token and timeout; one engine instance holds no
    /// per-call state.</summary>
    [Fact]
    public async Task T2_serial_and_overlapping_calls_have_independent_resources_tokens_and_timeouts()
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source(41);
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
            var two = await new QueryRunner(engine).RunAsync(source, Raw("SELECT 41 AS n"), second.Token);
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

    /// <summary>Every combination of engine kind, keep count and available rows around the cap.</summary>
    public static IEnumerable<object[]> CapCases()
    {
        foreach (var leaf in new[] { false, true })
        foreach (var keep in new[] { 1, 3 })
        foreach (var available in new[] { 0, keep, keep + 1, 5 }.Distinct()) yield return [leaf, keep, available];
    }

    /// <summary>A capped read stops after the probe row, reads to end of data only when the rows run out first, and
    /// reports <c>More</c> exactly when more rows exist than it keeps.</summary>
    [Theory]
    [MemberData(nameof(CapCases))]
    public async Task T3_cap_and_eof_read_counts(bool leaf, int keep, int available)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source(7);
        var probe = new SqliteProbe();
        IDatabaseEngine engine = leaf ? new SqliteEngine(probe.Create) : new TestEngine(probe.Create);
        var runner = new QueryRunner(engine);
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

    /// <summary>The raw read gives every row to its consumer without a cap, the rows outlive the read, and the capped
    /// path keeps the same row objects it was given instead of copying them.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T3_raw_callback_is_uncapped_and_detached_rows_survive(bool leaf)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source(7);
        var engine = leaf ? (IDatabaseEngine)new SqliteEngine() : new TestEngine(text => new SqliteConnection(text));
        var query = Raw();
        var capped = await new QueryRunner(engine).RunAsync(source, query, default);
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

    /// <summary>The source's timeout reaches the command, and the row-fetch parameter stays an <c>int</c> that the
    /// provider's parameter hook never sees.</summary>
    /// <remarks>The fetch count is the engine's own value, not caller data, so a provider hook that rewrites integers
    /// (as the test hook here does) must not be able to change how many rows are fetched.</remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(41)]
    public async Task T4_timeout_and_protected_int32_binding(int timeout)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source(timeout);
        var history = new History();
        var observed = new List<object?>();
        var engine = new TestEngine(text => new HistoryConnection(text, history))
        {
            Parameter = value => { observed.Add(value); return value is int ? (999L, DbType.Int64) : value is bool flag ? (flag ? 1L : 0L, DbType.Int64) : (value, null); }
        };
        var runner = new QueryRunner(engine);
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

    /// <summary>A driver timeout while connecting is SourceUnavailable at the source; one while running the command is
    /// QueryTimeout at the query and names the timeout. The original exception is kept.</summary>
    [Theory]
    [InlineData("open", "SourceUnavailable", "$.source", "connect")]
    [InlineData("execute", "QueryTimeout", "$.query", "command")]
    public async Task T4_timeout_identity_and_phase(string stage, string code, string path, string step)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source(41);
        var error = new TimeoutException("synthetic timeout");
        var history = new History();
        history.Failures.Add(stage, error);
        var result = await new QueryRunner(new TestEngine(text => new HistoryConnection(text, history))).RunAsync(source, Raw(), default);
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.Failure!.Code);
        Assert.Equal(path, result.Failure!.Path);
        Assert.Equal(step, result.Failure!.Step);
        Assert.Same(error, result.Failure!.DriverException);
        if (stage == "execute") Assert.Contains("41-second command timeout", result.Failure!.Message);
        Clean(history, stage == "open" ? 0 : 1, 0);
    }

    /// <summary>The connection factory runs only when a read starts, never for compile or connection checks, and bad
    /// arguments or an already cancelled token are refused before it runs.</summary>
    [Fact]
    public async Task T5_lazy_factory_and_argument_refusals()
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source(7);
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

    /// <summary>A connection factory that throws maps to the right error at the connect step with the original exception
    /// and stack trace, and an unexpected exception type propagates unchanged.</summary>
    [Theory]
    [InlineData("argument", "InvalidCatalog")]
    [InlineData("format", "InvalidCatalog")]
    [InlineData("io", "SourceUnavailable")]
    [InlineData("operation", "SourceUnavailable")]
    [InlineData("unexpected", null)]
    public async Task T5_factory_failures_preserve_identity(string kind, string? code)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source(7);
        Exception error = kind switch { "argument" => new ArgumentException("synthetic"), "format" => new FormatException("synthetic"), "io" => new IOException("synthetic"), "operation" => new InvalidOperationException("synthetic"), _ => new SentinelException() };
        var calls = 0;
        var engine = new TestEngine(text => { Assert.Equal(fixture.Connection, text); calls++; ThrowNamedFrame(error); return null!; });
        var runner = new QueryRunner(engine);
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

    /// <summary>A factory that returns <see langword="null"/> or an already open connection is refused, and the engine
    /// neither uses nor disposes a connection it did not open.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T5_null_and_open_results_are_rejected_without_taking_ownership(bool open)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source(7);
        var history = new History();
        var connection = new HistoryConnection(fixture.Connection, history);
        if (open) connection.Open();
        try
        {
            var calls = 0;
            var engine = new TestEngine(text => { Assert.Equal(fixture.Connection, text); calls++; return open ? connection : null!; });
            var result = await new QueryRunner(engine).RunAsync(source, Raw(), default);
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

    /// <summary>Match evidence must be a non-negative 64-bit integer; it sets <c>NoMatchingRows</c> and is removed from the
    /// row. Bad or missing evidence is a gateway invariant failure, not a source outage.</summary>
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
        var source = await fixture.Source(7);
        var history = new History();
        var engine = new TestEngine(text => new HistoryConnection(text, history))
        { Cell = (value, column) => int32 && value is long number ? (object)(int)number : value };
        var sql = expression == "missing" ? "SELECT NULL AS n" : $"SELECT {expression} AS __dg_matched_rows, NULL AS n";
        if (noMatches is null)
        {
            // Bad evidence is DataGateway's own broken assumption: an unexpected error, never a source outage.
            var error = await Assert.ThrowsAsync<DataGateway.Data.GatewayInvariantException>(() =>
                new QueryRunner(engine).RunAsync(source, Raw(sql, matched: true), default));
            Assert.Equal("The query row is missing its matched-row count.", error.Message);
            Clean(history);
            return;
        }
        var result = await new QueryRunner(engine).RunAsync(source, Raw(sql, matched: true), default);
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

    /// <summary>Match evidence on the probe row and later rows is never examined, and the raw read leaves the evidence
    /// column in place.</summary>
    [Fact]
    public async Task T6_probe_evidence_is_not_examined_and_raw_callback_keeps_it()
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source(7);
        var history = new History();
        var engine = new TestEngine(text => new HistoryConnection(text, history));
        var query = Raw("SELECT 1 AS __dg_matched_rows UNION ALL SELECT NULL UNION ALL SELECT -1", 1, true);
        var result = await new QueryRunner(engine).RunAsync(source, query, default);
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

    /// <summary>Every step at which a read can fail, for both the capped and the raw read; the capped read has no
    /// caller-supplied notify or consumer step.</summary>
    public static IEnumerable<object[]> PrimaryCases()
    {
        foreach (var capped in new[] { false, true })
        foreach (var stage in new[] { "open", "notified", "command", "parameter", "prepare", "execute", "schema", "read", "convert", "consumer" })
            if (!capped || stage is not ("notified" or "consumer")) yield return [capped, stage];
    }

    /// <summary>When a step fails and every disposal then fails too, the caller still gets the first failure, with its
    /// inner exception and original stack trace, and every opened resource is still disposed.</summary>
    [Theory]
    [MemberData(nameof(PrimaryCases))]
    public async Task T7_primary_failure_survives_all_cleanup_failures(bool capped, string stage)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source(7);
        var inner = new SentinelException();
        var primary = new IOException("primary " + stage, inner);
        var history = new History();
        history.Failures[stage] = primary;
        foreach (var resource in new[] { "reader", "command", "connection" }) history.Failures[resource + "-disposed"] = new IOException("cleanup " + resource);
        var engine = new TestEngine(text => new HistoryConnection(text, history)) { Trace = history.Hit };
        Exception? actual;
        if (capped)
        {
            var result = await new QueryRunner(engine).RunAsync(source,
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

    /// <summary>When the read succeeds but disposal fails, the first disposal failure is reported, and the read does not
    /// complete until every asynchronous disposal has finished.</summary>
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
        var source = await fixture.Source(7);
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
            var result = await new QueryRunner(engine).RunAsync(source, Raw(), default);
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

    /// <summary>Cancellation at each phase becomes QueryCancelled at the query with the step it happened in, including
    /// cancellation that arrives after the rows were read but before the read returned.</summary>
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
        var source = await fixture.Source(7);
        using var caller = new CancellationTokenSource();
        var history = new History { OpenWait = stage == "open" };
        history.OnEvent = name => { if (name == stage && stage != "open") caller.Cancel(); };
        var factoryCalls = 0;
        var engine = new TestEngine(text => { factoryCalls++; return new HistoryConnection(text, history); });
        if (stage == "pre") caller.Cancel();
        var run = new QueryRunner(engine).RunAsync(source, Raw("SELECT id FROM proof_items WHERE id <= 2"), caller.Token);
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

    /// <summary>The reader sees a token linked to both the call's token and the consumer's enumeration token, and a raw
    /// read that reached end of data still returns its rows when cancellation arrives afterwards.</summary>
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

    /// <summary>An <see cref="OperationCanceledException"/> thrown while the caller's token is still live, and an
    /// unexpected exception thrown after the caller cancelled, both propagate unchanged instead of becoming
    /// QueryCancelled.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T8_live_token_oce_and_unexpected_faults_propagate(bool unexpected)
    {
        using var fixture = new SqliteFixture();
        var source = await fixture.Source(7);
        using var caller = new CancellationTokenSource();
        Exception primary = unexpected ? new SentinelException() : new OperationCanceledException("independent", caller.Token);
        var history = new History();
        history.Failures["execute"] = primary;
        if (unexpected) history.OnEvent = stage => { if (stage == "execute") caller.Cancel(); };
        var runner = new QueryRunner(new TestEngine(text => new HistoryConnection(text, history)));
        var thrown = await Record.ExceptionAsync(() => runner.RunAsync(source, Raw(), caller.Token));
        Assert.Same(primary, thrown);
        Clean(history, 1, 0);
    }

    /// <summary>The streamed rows can be enumerated once, and the sequence and its enumerator stop working when the read
    /// returns, however the consumer ended; the row dictionaries it handed out stay usable.</summary>
    /// <remarks>TERM lease: the consumer's right to the open reader, which ends with the read. A row dictionary is a
    /// detached copy and outlives it.</remarks>
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

    /// <summary>Duplicate column names are refused before any row is read; names are case-sensitive and keep their order,
    /// nulls are never converted, and declared boolean columns are converted.</summary>
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

    /// <summary>Duplicate declared output columns fail after the connection opens but before any read or consumer call,
    /// and every resource is still disposed.</summary>
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

    /// <summary>The SQLite engine's hooks behave as specified: a fresh compiler, the count expression, LIKE escaping,
    /// booleans bound as 0 or 1, strict boolean cell conversion, and no recognized failures.</summary>
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

    /// <summary>Throws <paramref name="error"/> from a named, non-inlined frame, so a test can check the original stack
    /// trace survived.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNamedFrame(Exception error) => throw error;
    /// <summary>An exception type no product code catches, standing for an unexpected fault.</summary>
    private sealed class SentinelException : Exception;

    /// <summary>A minimal engine over SQLite whose hooks can be traced and replaced per test.</summary>
    private class TestEngine(Func<string, SqliteConnection> factory) : DatabaseEngine<SqliteConnection>(factory), IDatabaseEngine
    {
        /// <summary>The provider name, <c>sqlite</c>.</summary>
        string IDatabaseEngine.ProviderName => "sqlite";
        /// <summary>Records each hook call by name.</summary>
        internal Action<string>? Trace;
        /// <summary>Replaces parameter preparation: returns the value to bind and, optionally, its type.</summary>
        internal Func<object?, (object? Value, DbType? Type)>? Parameter;
        /// <summary>Replaces cell conversion.</summary>
        internal Func<object, OutputColumn?, object>? Cell;
        /// <summary>Returns a SQLite compiler.</summary>
        protected override Compiler CreateCompiler() { Trace?.Invoke("compiler"); return new SqliteCompiler(); }
        /// <summary>The SQLite count expression.</summary>
        protected override string MatchedRowCountExpression { get { Trace?.Invoke("count-expression"); return "COUNT(*) AS \"__dg_matched_rows\""; } }
        /// <summary>Escapes backslash and underscore with a backslash.</summary>
        protected override (string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern)
        {
            Trace?.Invoke("like");
            return (pattern.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal), "\\");
        }
        /// <summary>Binds the value as <see cref="Parameter"/> returns it, or unchanged.</summary>
        protected override void PrepareParameter(DbParameter parameter, object? value)
        {
            Trace?.Invoke("prepare");
            (object? Value, DbType? Type) prepared = Parameter is not null ? Parameter(value) : (value, null);
            if (prepared.Type is { } type) parameter.DbType = type;
            parameter.Value = prepared.Value ?? DBNull.Value;
        }
        /// <summary>Converts the cell with <see cref="Cell"/>, or returns it unchanged.</summary>
        protected override object ConvertCell(object value, OutputColumn? column)
        {
            Trace?.Invoke("convert");
            return Cell is not null ? Cell(value, column) : value;
        }
        /// <summary>Checks the connection as the SQLite engine does.</summary>
        public override void ValidateSourceConnection(string? connection) { Trace?.Invoke("validate"); new SqliteEngine().ValidateSourceConnection(connection); }
        /// <summary>Recognizes failures as the SQLite engine does.</summary>
        public override ProviderFailure? RecognizeFailure(Exception error) => new SqliteEngine().RecognizeFailure(error);
    }

    /// <summary>An engine that hides <c>Compile</c> and <c>ReadSourceAsync</c> with throwing <c>new</c> methods and maps
    /// the interface to them, to prove the runner calls the base class's capped path, not the interface.</summary>
    private sealed class HidingEngine(Func<string, SqliteConnection> factory) : TestEngine(factory), IDatabaseEngine
    {
        /// <summary>The provider name, <c>sqlite</c>.</summary>
        string IDatabaseEngine.ProviderName => "sqlite";
        /// <summary>Throws: a call here means the runner went through the hidden method.</summary>
        public new CompiledQuery Compile(ValidatedQuery query) => throw new SentinelException();
        /// <summary>Throws: a call here means the runner went through the hidden method.</summary>
        public new Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int timeout, Action opened,
            Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken token) => throw new SentinelException();
        /// <summary>Routes the interface to the throwing method.</summary>
        CompiledQuery IDatabaseEngine.Compile(ValidatedQuery query) => Compile(query);
        /// <summary>Routes the interface to the throwing method.</summary>
        Task<QueryRows> IDatabaseEngine.ReadSourceAsync(string connection, CompiledQuery query, int timeout, Action opened,
            Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken token) => ReadSourceAsync(connection, query, timeout, opened, consume, token);
    }

    /// <summary>The shared record of one read: every named event in order, what the command and reader received, and the
    /// failures and pauses a test plants at named events.</summary>
    private sealed class History
    {
        /// <summary>The named events, in the order they happened.</summary>
        internal readonly List<string> Events = [];
        /// <summary>Exceptions to throw when the named event happens.</summary>
        internal readonly Dictionary<string, Exception> Failures = [];
        /// <summary>The parameters as bound when the command ran.</summary>
        internal readonly List<(string Name, object? Value, DbType Type)> Parameters = [];
        /// <summary>Completes when the read reaches the gated step.</summary>
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Completed by the test to let the gated step continue.</summary>
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Called on every event, before a planted failure is thrown; tests use it to cancel at a precise step.</summary>
        internal Action<string>? OnEvent;
        /// <summary><c>Gate</c> names the resource whose disposal pauses; <c>Original</c> is the connection text the factory received.</summary>
        internal string? Gate, Original;
        /// <summary>Whether opening the connection pauses at the gate.</summary>
        internal bool OpenWait;
        /// <summary>Read calls, read calls that returned a row, and the command timeout in seconds.</summary>
        internal int Reads, TrueReads, Timeout;
        /// <summary>The tokens the connection open, command execute and last row read received.</summary>
        internal CancellationToken OpenToken, ExecuteToken, ReadToken;
        /// <summary>The connection this history belongs to.</summary>
        internal HistoryConnection? Connection;
        /// <summary>Records one event and throws the failure planted for it, if any.</summary>
        /// <param name="name">The event name.</param>
        internal void Hit(string name)
        {
            Events.Add(name);
            OnEvent?.Invoke(name);
            if (Failures.TryGetValue(name, out var error)) ThrowNamedFrame(error);
        }
        /// <summary>Pauses before disposing <paramref name="resource"/> when it is the gated resource.</summary>
        /// <param name="resource">reader, command or connection.</param>
        internal async Task BeforeCleanup(string resource)
        {
            if (Gate != resource) return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(Bound);
        }
    }

    /// <summary>Asserts that the resources that were created were disposed exactly once, innermost first, and that the
    /// connection ended closed.</summary>
    /// <param name="history">The read to check.</param>
    /// <param name="commands">0 when no command was created.</param>
    /// <param name="readers">0 when no reader was created.</param>
    private static void Clean(History history, int commands = 1, int readers = 1)
    {
        var expected = new List<string>();
        if (readers != 0) expected.Add("reader-disposed");
        if (commands != 0) expected.Add("command-disposed");
        expected.Add("connection-disposed");
        Assert.Equal(expected, history.Events.Where(name => name.EndsWith("-disposed", StringComparison.Ordinal)));
        Assert.Equal(ConnectionState.Closed, history.Connection!.State);
    }

    /// <summary>A SQLite connection that records open, command creation and disposal in its <see cref="History"/>, and can
    /// pause while opening.</summary>
    private sealed class HistoryConnection : SqliteConnection
    {
        /// <summary>The record this connection writes to.</summary>
        private readonly History history;
        /// <summary>Creates the connection and attaches it to <paramref name="history"/>.</summary>
        internal HistoryConnection(string text, History history) : base(text) { this.history = history; history.Connection = this; }
        /// <summary>Records the open and its token, optionally pauses at the gate, then opens.</summary>
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
        /// <summary>Returns a recording command.</summary>
        protected override DbCommand CreateDbCommand()
        {
            history.Hit("command");
            var command = new HistoryCommand(base.CreateDbCommand(), history);
            history.Hit("command-created");
            return command;
        }
        /// <summary>Optionally pauses at the gate, then disposes and records it.</summary>
        public override async ValueTask DisposeAsync()
        {
            await history.BeforeCleanup("connection");
            await base.DisposeAsync();
            history.Hit("connection-disposed");
        }
    }

    /// <summary>A command wrapper that records parameter creation, execution (with its timeout, token and bound
    /// parameters) and disposal.</summary>
    private sealed class HistoryCommand(DbCommand inner, History history) : DbCommand
    {
        /// <summary>Whether the command was disposed; a second disposal throws so a double dispose cannot go unnoticed.</summary>
        internal bool IsDisposed;
        /// <summary>Forwards to the inner command.</summary>
        /// <summary>Forwards to the inner command.</summary>
        [AllowNull] public override string CommandText { get => inner.CommandText; set => inner.CommandText = value!; }
        public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
        /// <summary>Forwards to the inner command.</summary>
        public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
        /// <summary>Forwards to the inner command.</summary>
        public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
        /// <summary>Forwards to the inner command.</summary>
        public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
        /// <summary>Forwards to the inner command.</summary>
        protected override DbConnection? DbConnection { get => inner.Connection; set => inner.Connection = value; }
        /// <summary>Forwards to the inner command.</summary>
        protected override DbTransaction? DbTransaction { get => inner.Transaction; set => inner.Transaction = value; }
        /// <summary>Forwards to the inner command.</summary>
        protected override DbParameterCollection DbParameterCollection => inner.Parameters;
        /// <summary>Records the event and creates the parameter on the inner command.</summary>
        protected override DbParameter CreateDbParameter() { history.Hit("parameter"); return inner.CreateParameter(); }
        /// <summary>Forwards to the inner command.</summary>
        public override void Cancel() => inner.Cancel();
        /// <summary>Forwards to the inner command.</summary>
        public override int ExecuteNonQuery() => inner.ExecuteNonQuery();
        /// <summary>Forwards to the inner command.</summary>
        public override object? ExecuteScalar() => inner.ExecuteScalar();
        /// <summary>Forwards to the inner command.</summary>
        public override void Prepare() => inner.Prepare();
        /// <summary>Forwards to the inner command; the engine uses only the asynchronous path.</summary>
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => inner.ExecuteReader(behavior);
        /// <summary>Records the timeout, token and parameters, then returns a recording reader.</summary>
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
        /// <summary>Optionally pauses at the gate, then disposes and records it; throws on a second disposal.</summary>
        public override async ValueTask DisposeAsync()
        {
            if (IsDisposed) throw new InvalidOperationException("Repeated outer command disposal.");
            await history.BeforeCleanup("command");
            await inner.DisposeAsync();
            IsDisposed = true;
            history.Hit("command-disposed");
        }
    }

    /// <summary>A reader wrapper that records schema reads, row reads and disposal, and refuses a second result set.</summary>
    private sealed class HistoryReader(DbDataReader inner, History history) : DbDataReader
    {
        /// <summary>Read calls on this reader, and those that returned a row.</summary>
        internal int ReadAttempts, SuccessfulReads;
        /// <summary>Whether the reader was disposed; a second disposal throws.</summary>
        internal bool IsDisposed;
        /// <summary>Forwards to the inner reader.</summary>
        public override int FieldCount => inner.FieldCount;
        /// <summary>Forwards to the inner reader.</summary>
        public override bool HasRows => inner.HasRows;
        /// <summary>Forwards to the inner reader.</summary>
        public override bool IsClosed => inner.IsClosed;
        /// <summary>Forwards to the inner reader.</summary>
        public override int Depth => inner.Depth;
        /// <summary>Forwards to the inner reader.</summary>
        public override int RecordsAffected => inner.RecordsAffected;
        /// <summary>Forwards to the inner reader.</summary>
        public override object this[int ordinal] => inner[ordinal];
        /// <summary>Forwards to the inner reader.</summary>
        public override object this[string name] => inner[name];
        /// <summary>Records a schema read and forwards.</summary>
        public override string GetName(int ordinal) { history.Hit("schema"); return inner.GetName(ordinal); }
        /// <summary>Records the read, its token and whether it returned a row.</summary>
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
        /// <summary>Forwards to the inner reader.</summary>
        public override bool Read() => inner.Read();
        /// <summary>Throws: the engine must read exactly one result set.</summary>
        public override bool NextResult() => throw new SentinelException();
        /// <summary>Throws: the engine must read exactly one result set.</summary>
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => throw new SentinelException();
        /// <summary>Forwards to the inner reader.</summary>
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        /// <summary>Forwards to the inner reader.</summary>
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        /// <summary>Forwards to the inner reader.</summary>
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        /// <summary>Forwards to the inner reader.</summary>
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        /// <summary>Forwards to the inner reader.</summary>
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        /// <summary>Forwards to the inner reader.</summary>
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        /// <summary>Forwards to the inner reader.</summary>
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        /// <summary>Forwards to the inner reader.</summary>
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        /// <summary>Forwards to the inner reader.</summary>
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        /// <summary>Forwards to the inner reader.</summary>
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        /// <summary>Forwards to the inner reader.</summary>
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        /// <summary>Forwards to the inner reader.</summary>
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        /// <summary>Forwards to the inner reader.</summary>
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        /// <summary>Forwards to the inner reader.</summary>
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        /// <summary>Forwards to the inner reader.</summary>
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        /// <summary>Forwards to the inner reader.</summary>
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        /// <summary>Forwards to the inner reader.</summary>
        public override int GetValues(object[] values) => inner.GetValues(values);
        /// <summary>Forwards to the inner reader.</summary>
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        /// <summary>Forwards to the inner reader.</summary>
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        /// <summary>Forwards to the inner reader.</summary>
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        /// <summary>Forwards to the inner reader.</summary>
        public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
        /// <summary>Forwards to the inner reader.</summary>
        public override void Close() => inner.Close();
        /// <summary>Optionally pauses at the gate, then disposes and records it; throws on a second disposal.</summary>
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
