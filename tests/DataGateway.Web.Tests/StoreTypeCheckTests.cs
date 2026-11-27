using System.Collections.Concurrent;
using System.Data;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Queries;
using DataGateway.Data.SqlServer;
using DataGateway.Failures;
using DataGateway.Web.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>The SQL Server store-type check: the catalog's store types are compared with the view's declared
/// types once per source per process, and only agreeing (or overridden) columns bind by them.</summary>
public sealed class StoreTypeCheckTests
{
    private const string Fix = "Run the fill tool (SqlServerStoreTypeScript.CreateAsync) or correct store_type.";

    [Fact]
    public async Task T1_matching_types_bind_and_the_view_is_read_once()
    {
        var reader = new FakeReader(Col("code", "varchar", 20), Col("qty", "int", 4), Col("amount", "decimal", 9, 18, 0),
            Col("stamp", "datetime2", 8, 27, 7), Col("ratio", "real", 4, 24), Col("name", "nvarchar", 80));
        var check = reader.Check();
        var source = Source(("code", "VARCHAR(20)", false), ("qty", "int", false), ("amount", "decimal", false),
            ("stamp", "datetime2", false), ("ratio", "float(10)", false), ("name", "nvarchar(40)", false));
        var findings = new List<StoreTypeFinding>();
        for (var i = 0; i < 3; i++)
        {
            var bound = await check.ResolveAsync(source, findings.Add, default);
            Assert.Equal(new[] { "amount", "code", "name", "qty", "ratio", "stamp" }, bound.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("varchar(20)", bound["CODE"].Spelling);
            Assert.All(bound.Values, type => Assert.False(type.Utf8Collation));
        }
        Assert.Empty(findings);
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task T2_a_mismatch_without_override_binds_as_preview6_and_is_logged_once_as_an_error()
    {
        var check = new FakeReader(Col("code", "nvarchar", 40), Col("qty", "int", 4)).Check();
        var source = Source(("code", "varchar(20)", false), ("qty", "int", false));
        var findings = new List<StoreTypeFinding>();
        for (var i = 0; i < 3; i++)
            Assert.Equal(new[] { "qty" }, (await check.ResolveAsync(source, findings.Add, default)).Keys);
        Assert.Equal(new StoreTypeFinding(StoreTypeSeverity.Error, "synthetic", "code",
            "Source 'synthetic', column 'code': the catalog's store_type is varchar(20) but the view's column is nvarchar(20), " +
            "so the column binds as if store_type were empty. " + Fix), Assert.Single(findings));
    }

    [Fact]
    public async Task T3_a_mismatch_with_override_binds_as_the_catalog_says_and_is_logged_once_as_a_warning()
    {
        var check = new FakeReader(Col("code", "nvarchar", 40)).Check();
        var source = Source(("code", "varchar(20)", true));
        var findings = new List<StoreTypeFinding>();
        for (var i = 0; i < 3; i++)
            Assert.Equal("varchar(20)", Assert.Single(await check.ResolveAsync(source, findings.Add, default)).Value.Spelling);
        Assert.Equal(new StoreTypeFinding(StoreTypeSeverity.Warning, "synthetic", "code",
            "Source 'synthetic', column 'code': the catalog's store_type is varchar(20) but the view's column is nvarchar(20); " +
            "store_type_override is set, so filter values bind as varchar(20). " + Fix), Assert.Single(findings));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T4_a_catalog_column_missing_from_the_view_binds_as_preview6_and_is_logged_once_as_an_error(bool overridden)
    {
        var check = new FakeReader(Col("qty", "int", 4)).Check();
        var source = Source(("gone", "char(4)", overridden), ("qty", "int", false));
        var findings = new List<StoreTypeFinding>();
        for (var i = 0; i < 2; i++)
            Assert.Equal(new[] { "qty" }, (await check.ResolveAsync(source, findings.Add, default)).Keys);
        Assert.Equal(new StoreTypeFinding(StoreTypeSeverity.Error, "synthetic", "gone",
            "Source 'synthetic', column 'gone': the catalog's store_type is char(4) but the view has no such column, so the " +
            "column binds as if store_type were empty. " + Fix), Assert.Single(findings));
    }

    [Fact]
    public async Task T5_a_failed_lookup_binds_everything_as_preview6_logs_its_first_failure_and_retries_after_the_interval()
    {
        var time = new ManualTime();
        var reader = new FakeReader(Col("code", "varchar", 20)) { Failures = 2 };
        var check = reader.Check(time);
        var source = Source(("code", "varchar(20)", false));
        var findings = new List<StoreTypeFinding>();
        Assert.Empty(await check.ResolveAsync(source, findings.Add, default));
        Assert.Empty(await check.ResolveAsync(source, findings.Add, default));
        time.Advance(SqlServerStoreTypes.RetryInterval - TimeSpan.FromSeconds(1));
        Assert.Empty(await check.ResolveAsync(source, findings.Add, default));
        Assert.Equal(1, reader.Calls);
        var error = Assert.Single(findings);
        Assert.Equal(new StoreTypeFinding(StoreTypeSeverity.Error, "synthetic", null,
            "Source 'synthetic': DataGateway could not read the column types of its view (SqlLookupFailure), so every column " +
            "binds as if store_type were empty. It tries again on a query at least 30 seconds after this read began, and logs this again only after a " +
            "read succeeds, or after 60 minutes. Check that the view exists and that the source login may see its definition " +
            "(VIEW DEFINITION)."), error);
        // The second failed read, 30 seconds on, is tried but not logged again.
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(await check.ResolveAsync(source, findings.Add, default));
        Assert.Equal((2, 1), (reader.Calls, findings.Count));
        time.Advance(SqlServerStoreTypes.RetryInterval);
        Assert.Equal("varchar(20)", Assert.Single(await check.ResolveAsync(source, findings.Add, default)).Value.Spelling);
        Assert.Equal((3, 1), (reader.Calls, findings.Count));
        // A successful read is kept for good, however much time passes.
        time.Advance(TimeSpan.FromDays(1));
        Assert.Single(await check.ResolveAsync(source, findings.Add, default));
        Assert.Equal(3, reader.Calls);
    }

    [Fact]
    public async Task T5_a_view_that_stays_unreadable_is_retried_every_30_seconds_and_logged_at_most_once_an_hour()
    {
        var time = new ManualTime();
        var reader = new FakeReader(Col("code", "varchar", 20)) { Failures = int.MaxValue };
        var check = reader.Check(time);
        var source = Source(("code", "varchar(20)", false));
        var other = Source(30, "fixture-other-connection", ("code", "varchar(20)", false));
        var findings = new List<StoreTypeFinding>();
        var logged = new List<int>();
        // Two hours and a minute of queries, one per retry interval: a read each time, an Error only at 0, 60 and 120 minutes.
        for (var minute = 0.0; minute <= 121; minute += SqlServerStoreTypes.RetryInterval.TotalMinutes)
        {
            var before = findings.Count;
            Assert.Empty(await check.ResolveAsync(source, findings.Add, default));
            if (findings.Count > before) logged.Add((int)minute);
            time.Advance(SqlServerStoreTypes.RetryInterval);
        }
        Assert.Equal(new[] { 0, 60, 120 }, logged);
        Assert.Equal(243, reader.Calls);
        // Another source's failure is its own: logged at once.
        Assert.Empty(await check.ResolveAsync(other, findings.Add, default));
        Assert.Equal(4, findings.Count);
        Assert.All(findings, finding => Assert.Equal(StoreTypeSeverity.Error, finding.Severity));
    }

    [Fact]
    public async Task T5_a_view_with_no_visible_columns_is_a_failed_lookup_not_a_missing_column_each()
    {
        var reader = new FakeReader();
        var check = reader.Check();
        var findings = new List<StoreTypeFinding>();
        Assert.Empty(await check.ResolveAsync(Source(("code", "varchar(20)", false), ("qty", "int", false)), findings.Add, default));
        var finding = Assert.Single(findings);
        Assert.Null(finding.ColumnName);
        Assert.Contains("(the view was not found, or its definition is not visible to the login)", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T6_a_utf8_varchar_column_binds_its_text_as_preview6_nvarchar()
    {
        var check = new FakeReader(Col("code", "varchar", 20, collation: "Latin1_General_100_CI_AS_SC_UTF8"),
            Col("plain", "varchar", 20, collation: "SQL_Latin1_General_CP1_CI_AS")).Check();
        var bound = await check.ResolveAsync(Source(("code", "varchar(20)", false), ("plain", "varchar(20)", false)), _ => { }, default);
        Assert.True(bound["code"].Utf8Collation);
        Assert.False(bound["plain"].Utf8Collation);
        var utf8 = Bind(bound["code"], "ABCD");
        Assert.Equal((SqlDbType.NVarChar, 4), (utf8.SqlDbType, utf8.Size));
        var ansi = Bind(bound["plain"], "ABCD");
        Assert.Equal((SqlDbType.VarChar, 20), (ansi.SqlDbType, ansi.Size));
    }

    [Fact]
    public async Task T7_concurrent_first_queries_share_one_lookup()
    {
        var reader = new FakeReader(Col("code", "varchar", 20)) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var check = reader.Check();
        var source = Source(("code", "varchar(20)", false));
        var pending = Enumerable.Range(0, 32).Select(_ => Task.Run(() => check.ResolveAsync(source, _ => { }, default))).ToArray();
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        reader.Gate.SetResult();
        var results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, bound => Assert.Equal("varchar(20)", Assert.Single(bound).Value.Spelling));
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task T7_a_caller_who_leaves_neither_cancels_the_shared_lookup_nor_fails()
    {
        var reader = new FakeReader(Col("code", "varchar", 20)) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var check = reader.Check();
        var source = Source(("code", "varchar(20)", false));
        using var leaving = new CancellationTokenSource();
        var first = check.ResolveAsync(source, _ => { }, leaving.Token);
        var second = check.ResolveAsync(source, _ => { }, default);
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        leaving.Cancel();
        Assert.Empty(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        reader.Gate.SetResult();
        Assert.Single(await second.WaitAsync(TimeSpan.FromSeconds(10)));
        // The read ran on its own deadline's token, which the leaving caller did not cancel.
        Assert.NotEqual(leaving.Token, reader.Token);
        Assert.True(reader.Token.CanBeCanceled);
        Assert.False(reader.Token.IsCancellationRequested);
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task T8_unreadable_store_types_are_warned_once_and_need_no_lookup_when_nothing_else_is_typed()
    {
        var reader = new FakeReader(Col("code", "varchar", 20));
        var check = reader.Check();
        var findings = new List<StoreTypeFinding>();
        for (var i = 0; i < 3; i++)
            Assert.Empty(await check.ResolveAsync(Source(("code", "store-type-marker-9b1d", false)), findings.Add, default));
        var warning = Assert.Single(findings);
        Assert.Equal(new StoreTypeFinding(StoreTypeSeverity.Warning, "synthetic", "code",
            "Source 'synthetic', column 'code': store_type is not a SQL Server type DataGateway can read, so the column binds " +
            "as if store_type were empty. " + Fix), warning);
        Assert.DoesNotContain("marker", warning.Message, StringComparison.Ordinal);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task T9_a_reporter_that_throws_never_changes_the_answer()
    {
        var check = new FakeReader(Col("code", "nvarchar", 20), Col("qty", "int", 4)).Check();
        var bound = await check.ResolveAsync(Source(("code", "varchar(20)", false), ("qty", "int", false), ("bad", "x(", false)),
            _ => throw new InvalidOperationException("reporter down"), default);
        Assert.Equal(new[] { "qty" }, bound.Keys);
    }

    [Theory]
    [InlineData("decimal", "decimal", 18, 0, true)]
    [InlineData("decimal(18)", "decimal", 18, 0, true)]
    [InlineData("decimal(19,4)", "decimal", 19, 4, true)]
    [InlineData("decimal(19,4)", "numeric", 19, 4, true)]
    [InlineData("numeric(19,4)", "decimal", 19, 4, true)]
    [InlineData("numeric", "decimal", 18, 0, true)]
    [InlineData("decimal(19,4)", "numeric", 19, 2, false)]
    [InlineData("rowversion", "timestamp", 8, 0, true)]
    [InlineData("timestamp", "timestamp", 8, 0, true)]
    [InlineData("rowversion", "bigint", 8, 0, false)]
    [InlineData("decimal(19,2)", "decimal", 19, 4, false)]
    [InlineData("float", "float", 53, 0, true)]
    [InlineData("float(24)", "real", 24, 0, true)]
    [InlineData("float(25)", "float", 53, 0, true)]
    [InlineData("datetime2", "datetime2", 27, 7, true)]
    [InlineData("datetime2(3)", "datetime2", 23, 3, true)]
    [InlineData("datetime2(3)", "datetime2", 27, 7, false)]
    [InlineData("datetime", "datetime2", 27, 7, false)]
    public void T10_types_compare_with_sql_server_defaults_filled_in(string catalog, string name, int precision, int scale, bool same)
    {
        Assert.True(StoreType.TryParse(catalog, out var parsed));
        var database = Assert.IsType<StoreType>(new SqlServerColumnType("c", name, 0, precision, scale, null).StoreType);
        Assert.Equal(same, SqlServerStoreTypes.Same(parsed, database));
    }

    [Theory]
    [InlineData("varchar", 20, 0, 0, "varchar(20)")]
    [InlineData("varchar", -1, 0, 0, "varchar(max)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("nvarchar", 80, 0, 0, "nvarchar(40)")]
    [InlineData("nchar", 10, 0, 0, "nchar(5)")]
    [InlineData("char", 10, 0, 0, "char(10)")]
    [InlineData("decimal", 9, 19, 4, "decimal(19,4)")]
    [InlineData("datetime2", 8, 27, 7, "datetime2(7)")]
    [InlineData("time", 5, 16, 7, "time(7)")]
    [InlineData("float", 8, 53, 0, "float(53)")]
    [InlineData("int", 4, 10, 0, "int")]
    [InlineData("datetime", 8, 23, 3, "datetime")]
    [InlineData("bit", 1, 1, 0, "bit")]
    public void T11_view_columns_are_spelled_as_a_catalog_store_type(string name, int maxLength, int precision, int scale, string spelling)
    {
        var column = new SqlServerColumnType("c", name, maxLength, precision, scale, null);
        Assert.Equal(spelling, column.Spelling);
        Assert.Equal(spelling, column.StoreType!.Spelling);
    }

    [Fact]
    public async Task T12_the_query_runner_asks_only_a_store_type_engine_and_only_for_a_source_with_store_types()
    {
        var engine = new TypedEngine();
        var runner = new QueryRunner(engine);
        var compiled = new CompiledQuery("select @p1", new Dictionary<string, object> { ["@p1"] = "AB" }, 1, false, [])
        {
            ParameterColumns = new Dictionary<string, string> { ["@p1"] = "code" }
        };
        var findings = new List<StoreTypeFinding>();
        Assert.Same(compiled, await runner.BindStoreTypesAsync(Source(), compiled, findings.Add, default));
        Assert.Equal(0, engine.Calls);
        var bound = await runner.BindStoreTypesAsync(Source(("code", "varchar(20)", false)), compiled, findings.Add, default);
        Assert.Equal(1, engine.Calls);
        Assert.Equal("varchar(20)", bound.StoreTypes["@p1"].Spelling);
        Assert.Equal(new StoreTypeFinding(StoreTypeSeverity.Warning, "synthetic", "code", "probe"), Assert.Single(findings));

        // The example host's forwarding engine keeps the capability of the engine it forwards to.
        var forwarding = new HostDatabaseEngine("sqlserver");
        forwarding.Bind(engine);
        var forwarded = await new QueryRunner(forwarding).BindStoreTypesAsync(Source(("code", "varchar(20)", false)), compiled, findings.Add, default);
        Assert.Equal("varchar(20)", forwarded.StoreTypes["@p1"].Spelling);
        var plain = new HostDatabaseEngine("sqlite");
        plain.Bind(new DataGateway.Data.Sqlite.SqliteEngine());
        Assert.Empty((await new QueryRunner(plain).BindStoreTypesAsync(Source(("code", "varchar(20)", false)), compiled, findings.Add, default)).StoreTypes);
        Assert.True(typeof(IStoreTypeEngine).IsAssignableFrom(typeof(SqlServerEngine)));
    }

    [Fact]
    public void T13_findings_are_logged_under_their_own_category_at_their_own_level()
    {
        var logs = new RecordedLogs();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        var log = new StoreTypeLog(factory);
        log.Report(new(StoreTypeSeverity.Error, "synthetic", "code", "error text"));
        log.Report(new(StoreTypeSeverity.Warning, "synthetic", null, "warning text"));
        Assert.Equal(new[]
        {
            ("DataGateway.StoreType", LogLevel.Error, "Store type Error for source synthetic, column code: error text"),
            ("DataGateway.StoreType", LogLevel.Warning, "Store type Warning for source synthetic, column (null): warning text")
        }, logs.Entries.ToArray());
        // A provider that throws never reaches the caller.
        using var broken = LoggerFactory.Create(builder => builder.AddProvider(new RecordedLogs { Throw = true }));
        new StoreTypeLog(broken).Report(new(StoreTypeSeverity.Error, "synthetic", "code", "error text"));
    }

    [Theory]
    [InlineData(30, 15)]
    [InlineData(15, 15)]
    [InlineData(5, 5)]
    [InlineData(1, 1)]
    public void T14_the_whole_read_is_bounded_by_the_lesser_of_15_seconds_and_the_source_timeout(int queryTimeout, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds),
            new FakeReader().Check().Deadline(Source(queryTimeout, "fixture-connection-removed", ("code", "int", false))));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T14_a_read_past_its_deadline_is_a_failed_read_even_when_the_reader_ignores_cancellation(bool honoursToken)
    {
        CancellationToken seen = default;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = new SqlServerStoreTypes(async (_, token) =>
        {
            seen = token;
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, honoursToken ? token : CancellationToken.None);
            return [];
        }, TimeProvider.System);
        var findings = new List<StoreTypeFinding>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // The caller's own token never fires: the read's deadline (1 s, the source's query timeout) ends it.
        Assert.Empty(await check.ResolveAsync(Source(1, "fixture-connection-removed", ("code", "varchar(20)", false)), findings.Add, default)
            .WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(8));
        await started.Task;
        Assert.True(seen.IsCancellationRequested);
        var finding = Assert.Single(findings);
        Assert.Equal(StoreTypeSeverity.Error, finding.Severity);
        Assert.StartsWith("Source 'synthetic': DataGateway could not read the column types of its view (it took longer than " +
            "the metadata read's time limit), so every column binds as if store_type were empty. It tries again on a query " +
            "at least 30 seconds after this read began, and logs this again only after a read succeeds, or after 60 minutes.", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T14_a_source_server_that_accepts_but_never_answers_is_cut_off_at_the_deadline_connect_included()
    {
        // A listener that takes the TCP connection and then says nothing: the driver's own Connect Timeout (30 s here)
        // would hold the first query; the read's deadline (the source's 2 s query timeout) ends it instead.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var held = new List<System.Net.Sockets.Socket>();
        var accepting = Task.Run(async () =>
        {
            try { while (true) held.Add(await listener.AcceptSocketAsync()); }
            catch (Exception error) when (error is System.Net.Sockets.SocketException or ObjectDisposedException) { }
        });
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var source = Source(2, $"Data Source=127.0.0.1,{port};Integrated Security=true;Encrypt=False;Connect Timeout=30;ConnectRetryCount=0;Pooling=false",
                ("code", "varchar(20)", false));
            var findings = new List<StoreTypeFinding>();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.Empty(await SqlServerStoreTypes.ForSources(TimeProvider.System).ResolveAsync(source, findings.Add, default)
                .WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(12));
            Assert.Contains("(it took longer than the metadata read's time limit)", Assert.Single(findings).Message, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            foreach (var socket in held) socket.Dispose();
            await accepting;
        }
    }

    [Fact]
    public async Task T15_an_unresolvable_synonym_is_logged_once_and_never_read_again_in_the_process()
    {
        var time = new ManualTime();
        var calls = 0;
        var check = new SqlServerStoreTypes((_, _) => { calls++; throw new UnresolvedObjectException(); }, time);
        var source = Source(("code", "varchar(20)", false));
        var findings = new List<StoreTypeFinding>();
        for (var i = 0; i < 3; i++)
        {
            Assert.Empty(await check.ResolveAsync(source, findings.Add, default));
            time.Advance(TimeSpan.FromHours(1));
        }
        Assert.Equal(1, calls);
        Assert.Equal(new StoreTypeFinding(StoreTypeSeverity.Error, "synthetic", null,
            "Source 'synthetic': DataGateway could not read the column types of its view (the source object is named with a " +
            "database or server part, or is a synonym for an object on another server, in another database, or no longer " +
            "there), so every column binds as if store_type were empty. It does not try again until the host restarts. " +
            "Point object_name at a view in the source's own database (schema.view or view), or set store_type to NULL."),
            Assert.Single(findings));
    }

    // On a case-sensitive database a view can hold stamp and STAMP as two columns. The catalog's spelling picks the one
    // spelled exactly like it; with no such column there is no telling which one it means.
    [Theory]
    [InlineData("STAMP", "time(7)", false, "time(7)")]
    [InlineData("stamp", "date", false, "date")]
    [InlineData("STAMP", "date", false, null)]
    [InlineData("STAMP", "date", true, "date")]
    public async Task T16_a_column_spelled_exactly_like_one_of_two_case_variants_is_compared_with_that_one(string column,
        string storeType, bool overridden, string? bound)
    {
        var check = new FakeReader(Col("stamp", "date", 3, 10, 0), Col("STAMP", "time", 5, 16, 7)).Check();
        var findings = new List<StoreTypeFinding>();
        var types = await check.ResolveAsync(Source((column, storeType, overridden)), findings.Add, default);
        Assert.Equal(bound, types.GetValueOrDefault(column)?.Spelling);
        if (bound is null || overridden)
            Assert.Contains("but the view's column is " + (column == "STAMP" ? "time(7)" : "date"), Assert.Single(findings).Message,
                StringComparison.Ordinal);
        else Assert.Empty(findings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T16_a_column_matching_two_case_variants_and_neither_exactly_binds_as_preview6_and_is_logged_once_as_an_error(bool overridden)
    {
        var check = new FakeReader(Col("stamp", "date", 3, 10, 0), Col("STAMP", "time", 5, 16, 7), Col("qty", "int", 4)).Check();
        var source = Source(("Stamp", "date", overridden), ("qty", "int", false));
        var findings = new List<StoreTypeFinding>();
        for (var i = 0; i < 3; i++)
            Assert.Equal(new[] { "qty" }, (await check.ResolveAsync(source, findings.Add, default)).Keys);
        Assert.Equal(new StoreTypeFinding(StoreTypeSeverity.Error, "synthetic", "Stamp",
            "Source 'synthetic', column 'Stamp': the catalog's store_type is date but the view has more than one column of " +
            "that name in different letter case and none spelled exactly the same, so the column binds as if store_type were " +
            "empty. Spell column_name in the catalog exactly as the view does."), Assert.Single(findings));
    }

    private static SqlParameter Bind(StoreType storeType, object value)
    {
        var parameter = new SqlParameter { ParameterName = "@p1" };
        var hook = typeof(SqlServerEngine).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(method => method.Name == "PrepareParameter" && method.GetParameters().Length == 3);
        hook.Invoke(new SqlServerEngine(), [parameter, value, storeType]);
        return parameter;
    }

    private static SqlServerColumnType Col(string name, string type, int maxLength, int precision = 0, int scale = 0,
        string? collation = null) => new(name, type, maxLength, precision, scale, collation);

    internal static SourceContract Source(params (string Column, string StoreType, bool Override)[] typed) =>
        Source(30, "fixture-connection-removed", typed);

    internal static SourceContract Source(int queryTimeoutSeconds, string connection,
        params (string Column, string StoreType, bool Override)[] typed)
    {
        var columns = typed.Select((column, index) =>
                new DataSourceColumnRow(column.Column, index + 1, column.Column, "Invented.", "text", null, null, null, true, true, true, true, true)
                { StoreType = column.StoreType, StoreTypeOverride = column.Override })
            .Append(new DataSourceColumnRow("note", 99, "note", "Invented.", "text", null, null, null, true, true, true, true, true))
            .ToArray();
        var mapped = CatalogMapping.Map(new(new DataSourceRow("dbo.synthetic", connection)
        {
            Id = 7319, Name = "synthetic", Title = "Synthetic", Description = "Synthetic", Grain = "One row",
            IsActive = true, RowCap = 500, QueryTimeoutSeconds = queryTimeoutSeconds
        }, columns));
        Assert.True(mapped.IsSuccess, mapped.Failure?.Message);
        return mapped.Value;
    }

    private sealed class SqlLookupFailure() : Exception("synthetic lookup failure");

    private sealed class FakeReader(params SqlServerColumnType[] columns)
    {
        private int calls;
        internal int Failures { get; set; }
        internal TaskCompletionSource? Gate { get; init; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; private set; }
        internal int Calls => Volatile.Read(ref calls);

        internal SqlServerStoreTypes Check(TimeProvider? time = null) => new(ReadAsync, time ?? TimeProvider.System);

        private async Task<IReadOnlyList<SqlServerColumnType>> ReadAsync(SourceContract source, CancellationToken token)
        {
            Assert.Equal("dbo.synthetic", source.ObjectName);
            Token = token;
            var call = Interlocked.Increment(ref calls);
            Started.TrySetResult();
            if (Gate is not null) await Gate.Task;
            if (call <= Failures) throw new SqlLookupFailure();
            return columns;
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan by) => now += by;
    }

    private sealed class TypedEngine : IDatabaseEngine, IStoreTypeEngine
    {
        internal int Calls;
        public string ProviderName => "sqlserver";
        public CompiledQuery Compile(ValidatedQuery query) => throw new NotSupportedException();
        public void ValidateSourceConnection(string? connection) { }
        public ProviderFailure? RecognizeFailure(Exception error) => null;
        public Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int commandTimeoutSeconds, Action opened,
            Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, StoreType>> ResolveStoreTypesAsync(SourceContract source,
            Action<StoreTypeFinding> report, CancellationToken cancellationToken)
        {
            Calls++;
            report(new(StoreTypeSeverity.Warning, source.Source.Name, "code", "probe"));
            Assert.True(StoreType.TryParse(source.StoreTypes["code"].Text, out var type));
            return Task.FromResult<IReadOnlyDictionary<string, StoreType>>(new Dictionary<string, StoreType> { ["code"] = type });
        }
    }

    private sealed class RecordedLogs : ILoggerProvider
    {
        internal bool Throw { get; init; }
        internal ConcurrentQueue<(string, LogLevel, string)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);
        public void Dispose() { }

        private sealed class Recorder(RecordedLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (owner.Throw) throw new InvalidOperationException("provider down");
                owner.Entries.Enqueue((category, logLevel, formatter(state, exception)));
            }
        }
    }
}
