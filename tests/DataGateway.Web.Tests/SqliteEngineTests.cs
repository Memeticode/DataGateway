using System.Data;
using System.Text.Json;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data.Queries;
using DataGateway.Data.Sqlite;
using DataGateway.Data.SqlServer;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace DataGateway.Web.Tests;

/// <summary>The SQLite engine (<see cref="SqliteEngine"/>) on its own: connection validation, SQL compilation,
/// LIKE escaping, boolean conversion and the open-read-dispose lifecycle, against a real SQLite file.</summary>
/// <remarks>
/// <para>The engine is a tests-only proof that a new database plugs in through the five public engine members
/// alone. These tests pin that it keeps the same contract as the production engines: refuse bad input before opening
/// anything, pass the caller's token and timeout through, hand back detached rows, and close every resource in order
/// even when something fails.</para>
/// <para>Lifecycle tests run through <see cref="SqliteProbe"/>, which records each real provider call.</para>
/// </remarks>
public sealed class SqliteEngineTests(ITestOutputHelper output)
{
    /// <summary>A compiled query with hand-written SQL, no parameters and a keep of 3, for tests that skip
    /// validation.</summary>
    /// <param name="sql">The SQL to run as is.</param>
    /// <param name="columns">The output columns the engine converts cells by.</param>
    internal static CompiledQuery Raw(string sql, params OutputColumn[] columns) => new(sql,
        new Dictionary<string, object>(), 3, false, columns);

    /// <summary>The plain row consumer: reads every row into a list.</summary>
    internal static async Task<QueryRows> Collect(IAsyncEnumerable<IDictionary<string, object?>> rows)
    {
        var result = new List<IDictionary<string, object?>>();
        await foreach (var row in rows) result.Add(row);
        return new QueryRows(result, false);
    }

    /// <summary>Runs <paramref name="query"/> through the engine against the fixture's read-only file, with a 7-second
    /// command timeout.</summary>
    /// <param name="engine">The engine under test.</param>
    /// <param name="fixture">The database to read.</param>
    /// <param name="query">The compiled query.</param>
    /// <param name="consume">The row consumer; <see cref="Collect"/> when null.</param>
    /// <param name="opened">Called once the connection is open; a no-op when null.</param>
    /// <param name="token">The caller's cancellation token.</param>
    internal static Task<QueryRows> Read(SqliteEngine engine, SqliteFixture fixture, CompiledQuery query,
        Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>>? consume = null,
        Action? opened = null, CancellationToken token = default) =>
        engine.ReadSourceAsync(fixture.Connection, query, 7, opened ?? (() => { }), consume ?? Collect, token);

    /// <summary>Asserts that one read left nothing open: every reader closed and disposed before its command, every
    /// command disposed, the single connection closed and disposed last, and no second result set read.</summary>
    /// <param name="probe">The probe that recorded the read.</param>
    /// <param name="readers">How many readers the read should have created.</param>
    /// <param name="commands">How many commands the read should have created.</param>
    internal static void Clean(SqliteProbe probe, int readers = 1, int commands = 1)
    {
        Assert.Equal(readers, probe.Readers.Count);
        Assert.Equal(commands, probe.Commands.Count);
        Assert.All(probe.Readers, reader => { Assert.True(reader.IsDisposed); Assert.True(reader.IsClosed); });
        Assert.All(probe.Commands, command => Assert.True(command.IsDisposed));
        var connection = Assert.Single(probe.Connections);
        Assert.True(connection.IsDisposed);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal(readers, probe.Events.Count(value => value == "reader-disposed"));
        Assert.Equal(commands, probe.Events.Count(value => value == "command-disposed"));
        Assert.Single(probe.Events, value => value == "connection-disposed");
        Assert.Equal("connection-disposed", probe.Events.Last());
        if (readers > 0) Assert.True(probe.Events.IndexOf("reader-disposed") < probe.Events.IndexOf("command-disposed"));
        Assert.Equal(0, probe.NextResults);
    }

    /// <summary>A missing or unparseable source connection is refused with a fixed message that neither quotes the
    /// text nor wraps the parser's exception, and no connection is created.</summary>
    /// <remarks>The parser's own message can quote a password fragment; the fixed message cannot.</remarks>
    [Theory]
    [InlineData(null, "A read-only SQLite file with pooling disabled is required.")]
    [InlineData(" ", "A read-only SQLite file with pooling disabled is required.")]
    [InlineData("PasswordMarker=planted-secret-marker", "The source connection string cannot be parsed.")]
    [InlineData("Data Source=\"unterminated", "The source connection string cannot be parsed.")]
    public void T2_safe_validation(string? text, string source)
    {
        var engine = new SqliteEngine(_ => throw new Exception("Validation must not create a connection."));
        var error = Assert.Throws<ArgumentException>(() => engine.ValidateSourceConnection(text));
        Assert.Equal(source, error.Message);
        Assert.Null(error.InnerException);
    }

    /// <summary>Only a named, read-only file with pooling off is accepted: no data source, <c>:memory:</c>, memory
    /// mode, writable modes and pooling on are all refused.</summary>
    /// <remarks>Read-only at the connection is the engine's guard against writes; pooling off keeps a handle from
    /// outliving the read.</remarks>
    [Theory]
    [InlineData("Mode=ReadOnly;Pooling=false")]
    [InlineData("Data Source=:memory:;Mode=ReadOnly;Pooling=false")]
    [InlineData("Data Source=imaginary.sqlite;Mode=Memory;Pooling=false")]
    [InlineData("Data Source=imaginary.sqlite;Pooling=false")]
    [InlineData("Data Source=imaginary.sqlite;Mode=ReadWrite;Pooling=false")]
    [InlineData("Data Source=imaginary.sqlite;Mode=ReadWriteCreate;Pooling=false")]
    [InlineData("Data Source=imaginary.sqlite;Mode=ReadOnly")]
    public void T2_required_mode(string text)
    {
        var engine = new SqliteEngine(_ => throw new Exception("No factory invocation allowed."));
        Assert.Equal("A read-only SQLite file with pooling disabled is required.", Assert.Throws<ArgumentException>(() => engine.ValidateSourceConnection(text)).Message);
    }

    /// <summary>A cancelled token or a zero timeout is refused before anything opens; the caller's token and timeout
    /// reach open, execute and read; a write fails with SQLite's own read-only error, which the engine recognizes by
    /// its code; validating a missing file does not create it.</summary>
    [Fact]
    public async Task T2_guards_read_only_and_real_failure_facts()
    {
        using var fixture = new SqliteFixture();
        var probe = new SqliteProbe();
        var engine = new SqliteEngine(probe.Create);
        var query = Raw("SELECT 1 AS n", new OutputColumn("n", "number"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(engine, fixture, query, token: cancellation.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.ReadSourceAsync(fixture.Connection, query, 0, () => { }, Collect, default));
        Assert.Empty(probe.Connections);
        using var caller = new CancellationTokenSource();
        await Read(engine, fixture, query, token: caller.Token);
        Assert.Equal(fixture.Connection, Assert.Single(probe.Connections).Original);
        Assert.Equal(caller.Token, probe.OpenToken);
        Assert.Equal(caller.Token, probe.ExecuteToken);
        Assert.Equal(caller.Token, probe.ReadToken);
        Assert.Equal(7, Assert.Single(probe.Commands).CommandTimeout);
        Clean(probe);

        var write = new SqliteProbe();
        var writerEngine = new SqliteEngine(write.Create);
        var error = await Assert.ThrowsAsync<SqliteException>(() => Read(writerEngine, fixture, Raw("DELETE FROM proof_items")));
        Assert.Same(error, write.ProviderFailure);
        var failure = engine.RecognizeFailure(error)!;
        Assert.False(failure.IsTimeout);
        Assert.Equal(error.SqliteErrorCode, failure.Number);
        Assert.Equal(8, failure.Number);
        Assert.Null(engine.RecognizeFailure(new IOException("wrapper", error)));
        Assert.Null(engine.RecognizeFailure(new IOException()));
        Assert.Throws<ArgumentNullException>(() => engine.RecognizeFailure(null!));
        Clean(write, readers: 0);
        var count = await Read(new SqliteEngine(), fixture, Raw("SELECT COUNT(*) AS n FROM proof_items", new OutputColumn("n", "number")));
        Assert.Equal(5L, count.Rows[0]["n"]);

        var missingFile = Path.Combine(fixture.DirectoryPath, "absent.sqlite");
        var missing = new SqliteConnectionStringBuilder { DataSource = missingFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        engine.ValidateSourceConnection(missing);
        Assert.False(File.Exists(missingFile));
    }

    /// <summary>Compiled SQL and parameters match fixed goldens (quoted names, LIMIT of the row cap plus one), and
    /// compiling never changes the validated query another engine compiles.</summary>
    /// <remarks>A count without group_by also selects the private matched-row count; with group_by it does
    /// not.</remarks>
    [Fact]
    public async Task T3_compilation_goldens_and_clone_independence()
    {
        using var fixture = new SqliteFixture();
        var engine = new SqliteEngine();
        var source = await fixture.Source();
        var validated = QueryBuilding.Validate(source, SqliteFixture.Detail, QueryLimits.ForRowCap(3)).Value;
        var server = new SqlServerEngine();
        var before = server.Compile(validated);
        for (var i = 0; i < 3; i++)
        {
            var compiled = engine.Compile(validated);
            Assert.Equal("SELECT \"label\", \"id\", \"flag\", \"amount\" FROM \"proof_items\" WHERE \"id\" >= @p0 ORDER BY \"id\" LIMIT @p1", compiled.Sql);
            Assert.Equal(new[] { "@p0", "@p1" }, compiled.Parameters.Keys);
            Assert.Equal(2m, Assert.IsType<decimal>(compiled.Parameters["@p0"]));
            Assert.Equal(4, Assert.IsType<int>(compiled.Parameters["@p1"]));
            Assert.Equal(3, compiled.Keep);
            Assert.False(compiled.HasMatchedRowCount);
            Assert.Equal(new[] { new OutputColumn("label", "text"), new OutputColumn("id", "number"), new OutputColumn("flag", "bool"), new OutputColumn("amount", "number") }, compiled.Columns);
            validated.CloneQuery().Limit(99).Where("id", -10);
            output.WriteLine(compiled.Sql);
            foreach (var (name, value) in compiled.Parameters) output.WriteLine($"{name}={value} ({value.GetType().Name})");
        }
        Assert.Equal(before.Sql, server.Compile(validated).Sql);
        Assert.Equal(before.Parameters, server.Compile(validated).Parameters);
        Assert.Contains("TOP", before.Sql);
        Assert.Contains("[proof_items]", before.Sql);
        var count = QueryBuilding.Validate(source, """{"aggregates":[{"fn":"count","col":"flag"}]}""", QueryLimits.ForRowCap(3)).Value;
        var result = engine.Compile(count);
        Assert.Equal("SELECT COUNT(\"flag\") AS \"count_flag\", COUNT(*) AS \"__dg_matched_rows\" FROM \"proof_items\" LIMIT @p0", result.Sql);
        Assert.Equal(4, Assert.IsType<int>(Assert.Single(result.Parameters).Value));
        Assert.Equal(3, result.Keep);
        Assert.True(result.HasMatchedRowCount);
        Assert.Equal(new OutputColumn("count_flag", "number"), Assert.Single(result.Columns));
        Assert.Contains("COUNT_BIG", server.Compile(count).Sql);
        var grouped = await fixture.Compile(engine, """{"group_by":["flag"],"aggregates":[{"fn":"count","col":"id"}]}""");
        Assert.False(grouped.HasMatchedRowCount);
        Assert.DoesNotContain("__dg_matched_rows", grouped.Sql);
        Assert.Equal(new[] { new OutputColumn("flag", "bool"), new OutputColumn("count_id", "number") }, grouped.Columns);
    }

    /// <summary>Each <c>contains</c> and <c>like</c> value binds with the expected escapes and finds exactly the
    /// expected rows in the real file.</summary>
    /// <remarks>Under the gateway's rules <c>%</c> is the only wildcard in <c>like</c>, so <c>_</c> and backslash
    /// must be escaped for SQLite; <c>[</c> is already plain there.</remarks>
    [Theory]
    [InlineData("contains", @"50%_[x]\end", "2", @"%50\%\_\[x]\\end%")]
    [InlineData("like", @"50%_[x]\end", "2", @"50%\_[x]\\end")]
    [InlineData("like", "%_%", "2", @"%\_%")]
    [InlineData("like", "%[x]%", "2", "%[x]%")]
    [InlineData("like", @"%\end", "2,4", @"%\\end")]
    [InlineData("contains", "%", "2", @"%\%%")]
    [InlineData("contains", "_", "2", @"%\_%")]
    [InlineData("contains", "[", "2", @"%\[%")]
    [InlineData("contains", @"\", "2,4", @"%\\%")]
    public async Task T4_patterns_execute(string op, string value, string ids, string binding)
    {
        using var fixture = new SqliteFixture();
        var engine = new SqliteEngine();
        var json = JsonSerializer.Serialize(new { select = new[] { "id" }, filters = new[] { new { col = "label", op, value } }, order_by = new[] { new { col = "id", dir = "asc" } }, limit = 3 });
        var compiled = await fixture.Compile(engine, json);
        Assert.Equal("SELECT \"id\" FROM \"proof_items\" WHERE \"label\" like @p0 ESCAPE '\\' ORDER BY \"id\" LIMIT @p1", compiled.Sql);
        Assert.Equal(binding, compiled.Parameters["@p0"]);
        Assert.Equal(4, compiled.Parameters["@p1"]);
        var rows = await Read(engine, fixture, compiled);
        Assert.Equal(ids, string.Join(",", rows.Rows.Select(row => row["id"])));
        output.WriteLine(compiled.Sql + " | " + binding + " -> " + ids);
    }

    /// <summary>Patterns inside nested and/or groups are escaped one by one, editing a clone of the query changes
    /// nothing compiled from the original, and injected SQL text stays a bound value.</summary>
    [Fact]
    public async Task T4_nested_clone_and_bound_injection()
    {
        using var fixture = new SqliteFixture();
        var engine = new SqliteEngine();
        var source = await fixture.Source();
        var json = """
            {"select":["id"],"filters":[{"col":"id","op":"gte","value":2},{"or":[{"and":[{"col":"label","op":"like","value":"%_%"},{"col":"label","op":"contains","value":"[x]\\end"}]},{"col":"label","op":"like","value":"tail"}]}],"order_by":[{"col":"id","dir":"asc"}],"limit":3}
            """;
        var validated = QueryBuilding.Validate(source, json, QueryLimits.ForRowCap(3)).Value;
        var before = new SqlServerEngine().Compile(validated);
        var first = engine.Compile(validated);
        var clone = validated.CloneQuery().Limit(100);
        clone.Where("id", -1);
        foreach (var condition in clone.GetComponents<SqlKata.AbstractCondition>("where"))
            if (condition is SqlKata.NestedCondition<SqlKata.Query> nested) nested.Query.Where("label", "changed");
        for (var i = 0; i < 3; i++)
        {
            var next = engine.Compile(validated);
            Assert.Equal(first.Sql, next.Sql);
            Assert.Equal(first.Parameters, next.Parameters);
        }
        Assert.Equal(new object[] { 2m, @"%\_%", @"%\[x]\\end%", "tail", 4 }, first.Parameters.Values);
        Assert.Equal(3, first.Sql.Split("ESCAPE", StringSplitOptions.None).Length - 1);
        Assert.Equal("2,5", string.Join(",", (await Read(engine, fixture, first)).Rows.Select(row => row["id"])));
        Assert.Equal(before.Sql, new SqlServerEngine().Compile(validated).Sql);
        Assert.Equal(before.Parameters, new SqlServerEngine().Compile(validated).Parameters);
        const string injection = "'; DROP TABLE proof_items; --";
        var bound = await fixture.Compile(engine, JsonSerializer.Serialize(new { select = new[] { "id" }, filters = new[] { new { col = "label", op = "like", value = injection } } }));
        Assert.DoesNotContain(injection, bound.Sql);
        Assert.Equal(@"'; DROP TABLE proof\_items; --", bound.Parameters["@p0"]);
        Assert.Empty((await Read(engine, fixture, bound)).Rows);
        Assert.Equal(5L, (await Read(engine, fixture, Raw("SELECT COUNT(*) AS n FROM proof_items", new OutputColumn("n", "number")))).Rows[0]["n"]);
    }

    /// <summary>Only a column declared <c>bool</c> in the compiled output turns 0/1 into booleans, whatever the
    /// column is called; rows are separate dictionaries with the declared names; any other integer in a bool column
    /// is refused.</summary>
    [Fact]
    public async Task T7_metadata_controls_detached_rows_and_boolean_refusal()
    {
        using var fixture = new SqliteFixture();
        var engine = new SqliteEngine();
        const string sql = "SELECT flag AS renamed, amount AS flag_looking FROM proof_items ORDER BY id";
        var rows = await Read(engine, fixture, Raw(sql, new OutputColumn("renamed", "bool"), new OutputColumn("flag_looking", "number")));
        Assert.Equal(new[] { "renamed", "flag_looking" }, rows.Rows[0].Keys);
        Assert.True(Assert.IsType<bool>(rows.Rows[0]["renamed"]));
        Assert.Equal(1L, Assert.IsType<long>(rows.Rows[0]["flag_looking"]));
        Assert.False(rows.Rows[0].ContainsKey("Renamed"));
        Assert.Null(rows.Rows[3]["renamed"]);
        rows.Rows[0]["renamed"] = "detached";
        Assert.True(Assert.IsType<bool>(rows.Rows[1]["renamed"]));
        var raw = await Read(engine, fixture, Raw(sql, new OutputColumn("renamed", "number"), new OutputColumn("flag_looking", "number")));
        Assert.Equal(1L, Assert.IsType<long>(raw.Rows[0]["renamed"]));
        var error = await Assert.ThrowsAsync<InvalidCastException>(() => Read(engine, fixture, Raw("SELECT 2 AS renamed", new OutputColumn("renamed", "bool"))));
        Assert.Equal("The source boolean cell is invalid.", error.Message);
    }

    /// <summary>For a full read, an early stop, a consumer fault, a read fault, an execute fault and duplicate column
    /// names: the right exception comes out, the caller is told the source opened before any command, rows can be
    /// enumerated only once, and every resource closes.</summary>
    /// <remarks>Rows already handed out stay usable after disposal, while the row stream itself refuses further
    /// use.</remarks>
    [Theory]
    [InlineData("full")]
    [InlineData("early")]
    [InlineData("consumer")]
    [InlineData("read")]
    [InlineData("execute")]
    [InlineData("duplicate")]
    public async Task T10_real_lifecycle(string scenario)
    {
        using var fixture = new SqliteFixture();
        var probe = new SqliteProbe();
        var engine = new SqliteEngine(probe.Create);
        var primary = new IOException("invented lifecycle fault");
        if (scenario == "read") { probe.FailReadAt = 2; probe.ReadFailure = primary; }
        var sql = scenario switch
        {
            "full" => "SELECT id FROM proof_items WHERE id < 3 ORDER BY id",
            "execute" => "SELECT id FROM missing_items",
            "duplicate" => "SELECT id AS duplicate, id AS duplicate FROM proof_items WHERE 0",
            _ => "SELECT id FROM proof_items ORDER BY id"
        };
        IAsyncEnumerable<IDictionary<string, object?>>? saved = null;
        IAsyncEnumerator<IDictionary<string, object?>>? enumerator = null;
        var detached = new List<IDictionary<string, object?>>();
        async Task<QueryRows> Consume(IAsyncEnumerable<IDictionary<string, object?>> rows)
        {
            saved = rows;
            enumerator = rows.GetAsyncEnumerator();
            Assert.Equal("Source rows are not positioned on a row.", Assert.Throws<InvalidOperationException>(() => enumerator.Current).Message);
            Assert.Equal("Source rows can only be enumerated once.", Assert.Throws<InvalidOperationException>(() => rows.GetAsyncEnumerator()).Message);
            while (await enumerator.MoveNextAsync())
            {
                detached.Add(enumerator.Current);
                if (scenario == "consumer") throw primary;
                if (scenario == "early" && detached.Count == 4) break;
            }
            await enumerator.DisposeAsync();
            Assert.False(await enumerator.MoveNextAsync());
            return new QueryRows(detached, false);
        }
        var exception = await Record.ExceptionAsync(() => Read(engine, fixture, Raw(sql, new OutputColumn(scenario == "duplicate" ? "duplicate" : "id", "number")), Consume,
            () => probe.Events.Add("notification")));
        if (scenario is "consumer" or "read") Assert.Same(primary, exception);
        else if (scenario == "execute") Assert.Same(probe.ProviderFailure, Assert.IsType<SqliteException>(exception));
        else if (scenario == "duplicate") Assert.Equal("The source row has duplicate column names.", Assert.IsType<DataGateway.Data.GatewayInvariantException>(exception).Message);
        else Assert.Null(exception);
        Assert.True(probe.Events.IndexOf("opened") < probe.Events.IndexOf("notification"));
        Assert.True(probe.Events.IndexOf("notification") < probe.Events.IndexOf("command"));
        Assert.Single(probe.Events, value => value == "notification");
        Assert.Equal(scenario switch { "full" => 3, "early" => 4, "consumer" => 1, "read" => 2, _ => 0 }, probe.Reads);
        Assert.Equal(scenario switch { "full" => 2, "early" => 4, "consumer" or "read" => 1, _ => 0 }, probe.TrueReads);
        Clean(probe, scenario == "execute" ? 0 : 1);
        if (saved is not null)
        {
            Assert.Throws<ObjectDisposedException>(() => saved.GetAsyncEnumerator());
            Assert.Throws<ObjectDisposedException>(() => enumerator!.Current);
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await enumerator!.MoveNextAsync());
            detached[0]["after"] = "mutable";
            Assert.Equal(new[] { "id", "after" }, detached[0].Keys);
        }
        output.WriteLine(scenario + ": " + string.Join(" -> ", probe.Events));
    }

    /// <summary>The read does not complete until the consumer and each asynchronous cleanup (reader, command,
    /// connection) have finished.</summary>
    /// <remarks>Fire-and-forget disposal would let the gateway answer while a connection is still open.</remarks>
    [Theory]
    [InlineData("reader")]
    [InlineData("command")]
    [InlineData("connection")]
    [InlineData("consumer")]
    public async Task T10_awaits_consumer_and_each_cleanup(string gate)
    {
        using var fixture = new SqliteFixture();
        var probe = new SqliteProbe { GateResource = gate };
        var engine = new SqliteEngine(probe.Create);
        async Task<QueryRows> Consume(IAsyncEnumerable<IDictionary<string, object?>> rows)
        {
            await using var cursor = rows.GetAsyncEnumerator();
            Assert.True(await cursor.MoveNextAsync());
            if (gate == "consumer")
            {
                probe.Entered.TrySetResult();
                await probe.Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            return new QueryRows([cursor.Current], false);
        }
        var task = Read(engine, fixture, Raw("SELECT id FROM proof_items", new OutputColumn("id", "number")), Consume);
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(task.IsCompleted);
            Assert.DoesNotContain(gate + "-disposed", probe.Events);
            if (gate == "consumer")
            {
                Assert.Equal(ConnectionState.Open, Assert.Single(probe.Connections).State);
                Assert.False(Assert.Single(probe.Readers).IsClosed);
            }
        }
        finally { probe.Release.TrySetResult(); await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        Clean(probe);
        output.WriteLine(gate + ": " + string.Join(" -> ", probe.Events));
    }

    /// <summary>When cleanups fail too, the first fault wins: the consumer's if it threw, else the reader's cleanup
    /// fault; every resource is still disposed.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T10_cleanup_faults_preserve_first_exception(bool primary)
    {
        using var fixture = new SqliteFixture();
        var probe = new SqliteProbe();
        var first = new IOException("reader cleanup");
        var consumer = new IOException("consumer");
        probe.CleanupErrors.Add("reader", first);
        probe.CleanupErrors.Add("command", new IOException("command cleanup"));
        probe.CleanupErrors.Add("connection", new IOException("connection cleanup"));
        var engine = new SqliteEngine(probe.Create);
        var error = await Record.ExceptionAsync(() => Read(engine, fixture, Raw("SELECT id FROM proof_items"),
            primary ? _ => throw consumer : Collect));
        Assert.Same(primary ? consumer : first, error);
        Clean(probe);
        output.WriteLine(string.Join(" -> ", probe.Events));
    }

    /// <summary>Column names that differ only by case stay separate keys, and the token given to the row enumerator
    /// cancels the read.</summary>
    [Fact]
    public async Task T10_ordinal_names_and_enumeration_cancellation()
    {
        using var fixture = new SqliteFixture();
        var probe = new SqliteProbe();
        using var caller = new CancellationTokenSource();
        using var enumeration = new CancellationTokenSource();
        var engine = new SqliteEngine(probe.Create);
        await Read(engine, fixture, Raw("SELECT id AS a, id AS A FROM proof_items", new OutputColumn("a", "number"), new OutputColumn("A", "number")), async rows =>
        {
            await using var cursor = rows.GetAsyncEnumerator(enumeration.Token);
            Assert.True(await cursor.MoveNextAsync());
            Assert.Equal(new[] { "a", "A" }, cursor.Current.Keys);
            var row = cursor.Current;
            enumeration.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cursor.MoveNextAsync());
            return new QueryRows([row], false);
        }, token: caller.Token);
        Clean(probe);
    }

    /// <summary>Missing arguments, a bad connection and blank SQL are refused before the connection factory runs, and validating a good connection opens nothing.</summary>
    [Fact]
    public async Task T2_arguments_refuse_before_factory_and_catalog_validations_are_local()
    {
        using var fixture = new SqliteFixture();
        var probe = new SqliteProbe();
        var engine = new SqliteEngine(probe.Create);
        var query = Raw("SELECT 1 AS id", new OutputColumn("id", "number"));
        Assert.Throws<ArgumentNullException>(() => new SqliteEngine(null!));
        Assert.Throws<ArgumentNullException>(() => engine.Compile(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.ReadSourceAsync(fixture.Connection, null!, 7, () => { }, Collect, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.ReadSourceAsync(fixture.Connection, query, 7, null!, Collect, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.ReadSourceAsync(fixture.Connection, query, 7, () => { }, null!, default));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.ReadSourceAsync("bad-key=marker", query, 7, () => { }, Collect, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Read(engine, fixture, Raw(" ")));
        engine.ValidateSourceConnection(fixture.Connection);
        Assert.Empty(probe.Connections);
    }
}
