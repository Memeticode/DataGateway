using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Data.Catalog;
using DataGateway.Data.PostgreSql;
using DataGateway.Data.SqlServer;
using DataGateway.Web.Catalog;
using Microsoft.Data.SqlClient;
using Npgsql;
using Xunit;
using static DataGateway.Web.Tests.CatalogEngineTests;

namespace DataGateway.Web.Tests;

/// <summary>Pins the two shipped catalog services, <see cref="SqlServerCatalog"/> and <see cref="PostgreSqlCatalog"/>,
/// and the shared reader behind them: their public surface, their SQL byte for byte, and how strictly they map, time
/// out, cancel, clean up and fail.</summary>
/// <remarks>
/// <para>TERM integral: whether the provider may return a boolean flag as an integer 0 or 1 (SQL Server) instead of
/// only a real boolean (PostgreSQL). TERM sentinel: the one row the detail query's left join returns for a source
/// with no columns, with every column cell null. TERM fallback: when the detail query finds no source, a second query
/// on the same connection reads the available names and checks they are well formed before the source is reported
/// unknown.</para>
/// <para>Every bad cell fails with one fixed message and no inner exception, because the catalog holds each source's
/// connection string and object name, and a message built from the cell could leak them
/// (<see cref="T12_Secret_values_never_enter_diagnostics_or_assertion_output"/>).</para>
/// </remarks>
public sealed class ExampleCatalogTests
{
    /// <summary>The five column capability flags.</summary>
    private static readonly string[] Flags = ["can_select", "can_filter", "can_sort", "can_group", "can_aggregate"];
    /// <summary>The column fields that may be null.</summary>
    private static readonly string[] Optional = ["unit", "magnitude", "display_format"];
    /// <summary>The fields of a list row.</summary>
    private static readonly string[] ListFields = ["name", "title", "description", "grain", "row_cap"];
    /// <summary>A distinctive object name that must never appear in a diagnostic.</summary>
    private const string SecretObject = "synthetic_object_private_5b25f7e0_long_witness";
    /// <summary>A distinctive connection value that must never appear in a diagnostic.</summary>
    private const string SecretConnection = "synthetic_connection_private_6f28a1d3_long_witness";

    /// <summary>Both services require a database context and a positive timeout, refuse an already cancelled token before
    /// opening anything, answer a blank source name as unknown without a catalog call, and let a factory failure
    /// propagate unchanged.</summary>
    [Fact]
    public async Task T1_Services_have_required_context_timeout_and_observe_precancellation()
    {
        CheckSurface(typeof(SqlServerCatalog), typeof(SqlConnection));
        CheckSurface(typeof(PostgreSqlCatalog), typeof(NpgsqlConnection));
        Assert.Throws<ArgumentNullException>(() => new SqlServerCatalog(null!, 5));
        Assert.Throws<ArgumentNullException>(() => new PostgreSqlCatalog(null!, 5));
        var sqlCalls = 0;
        var pgCalls = 0;
        var failure = new IOException("Synthetic factory failure.");
        var sqlDb = DbContext<SqlConnection>.FromFactory(() =>
        {
            sqlCalls++;
            throw failure;
        });
        var pgDb = DbContext<NpgsqlConnection>.FromFactory(() =>
        {
            pgCalls++;
            throw failure;
        });
        foreach (var timeout in new[] { 0, -1 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SqlServerCatalog(sqlDb, timeout));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlCatalog(pgDb, timeout));
        }
        foreach (var timeout in new[] { 1, 41, int.MaxValue })
        {
            Assert.NotNull(new SqlServerCatalog(sqlDb, timeout));
            Assert.NotNull(new PostgreSqlCatalog(pgDb, timeout));
        }
        CatalogService<SqlConnection> sql = new SqlServerCatalog(sqlDb, 41);
        CatalogService<NpgsqlConnection> pg = new PostgreSqlCatalog(pgDb, 43);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Cancelled(() => sql.QueryMcpMetadataAsync(cancel.Token), cancel.Token);
        await Cancelled(() => sql.QueryCatalogAsync(cancel.Token), cancel.Token);
        await Cancelled(() => sql.QueryCatalogDetailAsync("synthetic", cancel.Token), cancel.Token);
        await Cancelled(() => pg.QueryMcpMetadataAsync(cancel.Token), cancel.Token);
        await Cancelled(() => pg.QueryCatalogAsync(cancel.Token), cancel.Token);
        await Cancelled(() => pg.QueryCatalogDetailAsync("synthetic", cancel.Token), cancel.Token);
        await Assert.ThrowsAsync<ArgumentNullException>(() => pg.QueryCatalogDetailAsync(null!, CancellationToken.None));
        // A blank name is an unknown source (404), answered without a catalog call.
        foreach (var name in new[] { "", " ", "\t\r\n" })
            Assert.Null(await pg.QueryCatalogDetailAsync(name, CancellationToken.None));
        Assert.Equal(0, sqlCalls);
        Assert.Equal(0, pgCalls);
        Assert.Same(failure, await Record.ExceptionAsync(() => sql.QueryCatalogAsync(CancellationToken.None)));
        Assert.Same(failure, await Record.ExceptionAsync(() => sql.QueryCatalogDetailAsync("synthetic", CancellationToken.None)));
        Assert.Same(failure, await Record.ExceptionAsync(() => pg.QueryCatalogAsync(CancellationToken.None)));
        Assert.Same(failure, await Record.ExceptionAsync(() => pg.QueryCatalogDetailAsync("synthetic", CancellationToken.None)));
        Assert.Same(failure, await Record.ExceptionAsync(() => sql.QueryMcpMetadataAsync(CancellationToken.None)));
        Assert.Equal(3, sqlCalls);
        Assert.Same(failure, await Record.ExceptionAsync(() => pg.QueryMcpMetadataAsync(CancellationToken.None)));
        Assert.Equal(3, pgCalls);
    }

    /// <summary>The list, detail and available-names SQL of both services equal the documented text byte for byte,
    /// with its line endings, size and SHA-256.</summary>
    /// <remarks>Size and hash are pinned too, so even a whitespace or line-ending change fails.</remarks>
    [Fact]
    public void T2_Six_SQL_copies_match_engine_bytes()
    {
        const string sqlServerList = "SELECT [name], [title], [description], [grain], [row_cap]\r\n" +
            "FROM [dbo].[vDataSource]\r\n" +
            "WHERE [is_active] = 1;";
        const string sqlServerRead = "SELECT s.[id], s.[name], s.[title], s.[description], s.[grain], s.[row_cap], s.[query_timeout_seconds], s.[object_name], s.[connection_string],\r\n" +
            "       c.[column_name], c.[ordinal], c.[title] AS [column_title],\r\n" +
            "       c.[description] AS [column_description], c.[data_type], c.[unit],\r\n" +
            "       c.[magnitude], c.[display_format], c.[can_select], c.[can_filter],\r\n" +
            "       c.[can_sort], c.[can_group], c.[can_aggregate]\r\n" +
            "FROM [dbo].[vDataSource] AS s\r\n" +
            "LEFT JOIN [dbo].[vDataSourceColumn] AS c ON c.[datasource_fk] = s.[id]\r\n" +
            "WHERE s.[name] COLLATE Latin1_General_100_CI_AS = @source AND s.[is_active] = 1\r\n" +
            "ORDER BY c.[ordinal], c.[column_name];";
        const string sqlServerAvailable = "SELECT [name] FROM [dbo].[vDataSource] WHERE [is_active] = 1;";
        const string postgreSqlList = "SELECT name, title, description, grain, row_cap\n" +
            "FROM datagateway.vdatasource\n" +
            "WHERE is_active = TRUE;";
        const string postgreSqlRead = "SELECT s.id, s.name, s.title, s.description, s.grain, s.row_cap, s.query_timeout_seconds, s.object_name, s.connection_string,\n" +
            "       c.column_name, c.ordinal, c.title AS column_title,\n" +
            "       c.description AS column_description, c.data_type, c.unit,\n" +
            "       c.magnitude, c.display_format, c.can_select, c.can_filter,\n" +
            "       c.can_sort, c.can_group, c.can_aggregate\n" +
            "FROM datagateway.vdatasource AS s\n" +
            "LEFT JOIN datagateway.vdatasourcecolumn AS c ON c.datasource_fk = s.id\n" +
            "WHERE lower(s.name COLLATE \"C\") = lower(@source COLLATE \"C\") AND s.is_active = TRUE\n" +
            "ORDER BY c.ordinal, c.column_name COLLATE \"C\";";
        const string postgreSqlAvailable = "SELECT name FROM datagateway.vdatasource WHERE is_active = TRUE;";

        Check(SqlServerCatalog.ListSql, sqlServerList, 107, "737473e34e126a126a944b4a05e0315b6c22500376e81f655c4d53981208d6e2");
        Check(SqlServerCatalog.ReadSql, sqlServerRead, 638, "ca59273874a34e52a016b680ded14a2fb02d1478cff1550cc65a940e203ed1a8");
        Check(SqlServerCatalog.AvailableSql, sqlServerAvailable, 61, "c6e1899d47c7e3029b812bb39945f2e473dcaf8de92820af3546d9051c42483b");
        Check(PostgreSqlCatalog.ListSql, postgreSqlList, 100, "885362ba68d78d4675b0fa37e568d369ff696735a319eb2d10d153429f1cfaf8");
        Check(PostgreSqlCatalog.ReadSql, postgreSqlRead, 598, "8c9a096e6dca416a39766a667b370effcc594a54eea64ba30506fcf64054c1a1");
        Check(PostgreSqlCatalog.AvailableSql, postgreSqlAvailable, 64, "28a6dbc9483d677e527001f2d9f85fc5e92939aca5f086876ea5136d1ed1d6a4");

        static void Check(string actual, string expected, int size, string hash)
        {
            Assert.Equal(expected, actual);
            var bytes = Encoding.UTF8.GetBytes(actual);
            Assert.Equal(size, bytes.Length);
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    /// <summary>Mapping list rows keeps every field exactly (no trimming), refuses a missing, null or mistyped field, and
    /// returns rows that do not change when the input rows do.</summary>
    [Fact]
    public void T3_List_projection_is_complete_strict_and_detached()
    {
        var first = ListRow();
        first["name"] = "z ";
        first["title"] = " First \u03a9 ";
        var second = ListRow();
        second["name"] = "a";
        second["description"] = " Second \u03b2 ";
        second["grain"] = "new grain";
        second["row_cap"] = 7;
        var rows = new List<dynamic> { first, second };
        var result = CatalogTableReader.MapList(rows);
        Assert.Equal(2, result.Count);
        AssertList(first, result[0]);
        AssertList(second, result[1]);
        first["name"] = "changed";
        second["row_cap"] = 9;
        rows.Clear();
        Assert.Equal("z ", result[0].Name);
        Assert.Equal(7, result[1].RowCap);
        Assert.Empty(CatalogTableReader.MapList([]));
        foreach (var field in ListFields)
        {
            var missing = ListRow();
            missing.Remove(field);
            Bad(() => CatalogTableReader.MapList([missing]));
            foreach (var value in new object?[] { null, DBNull.Value, new object() })
            {
                var row = ListRow();
                row[field] = value;
                Bad(() => CatalogTableReader.MapList([row]));
            }
        }
        Bad(() => CatalogTableReader.MapList([new object()]));
        Bad(() => CatalogTableReader.MapList([null!]));
    }

    /// <summary>Mapping a detail keeps every source and column field exactly, keeps the query's column order and the
    /// provider name, and is detached from the input rows.</summary>
    [Fact]
    public void T4_Detail_projection_preserves_every_field_provider_and_column_order()
    {
        foreach (var integral in new[] { true, false })
        {
            var first = RichRow(2);
            var second = RichRow(1);
            var provider = integral ? SqlServerEngine.ProviderName : PostgreSqlEngine.ProviderName;
            if (integral)
                first["can_select"] = (byte)1;
            var rows = new List<dynamic> { first, second };
            var expected = ExpectedDetail(first, second, provider);
            var result = CatalogTableReader.MapDetail(rows, provider, integral);
            AssertDetail(expected.Rows, result, provider);
            first["object_name"] = "changed";
            first["title"] = "changed";
            first["column_name"] = "changed";
            second["display_format"] = "changed";
            rows.Clear();
            AssertDetail(expected.Rows, result, provider);
        }
    }

    /// <summary>Every detail field that is missing, mistyped, or null where required is refused, on the first row and on
    /// a later row; only unit, magnitude and display format may be null.</summary>
    [Fact]
    public void T5_Detail_rejects_every_missing_wrong_and_required_null_cell()
    {
        foreach (var integral in new[] { true, false })
        foreach (var field in SourceFields.Concat(ColumnFields))
        {
            foreach (var kind in new[] { "missing", "wrong", "null", "dbnull" })
            {
                if (Optional.Contains(field) && kind is "null" or "dbnull")
                {
                    var nullable = SourceRow();
                    nullable[field] = kind == "null" ? null : DBNull.Value;
                    Assert.Single(CatalogTableReader.MapDetail([nullable], "provider", integral).Rows.Columns);
                    continue;
                }
                var row = SourceRow();
                Mutate(row, field, kind);
                Bad(() => CatalogTableReader.MapDetail([row], "provider", integral));
                if (SourceFields.Contains(field))
                    Bad(() => CatalogTableReader.MapDetail([SourceRow(), row], "provider", integral));
            }
        }
        Bad(() => CatalogTableReader.MapDetail([new object()], "provider", true));
        Bad(() => CatalogTableReader.MapDetail([SourceRow(), null!], "provider", false));
    }

    /// <summary>The sentinel row maps to a source with no columns only when every column cell is present and null; a
    /// partly null row is refused.</summary>
    [Fact]
    public void T6_Left_join_sentinel_requires_every_column_cell_present_and_null()
    {
        foreach (var integral in new[] { true, false })
        {
            foreach (var kind in new[] { "null", "dbnull", "mixed" })
            {
                var row = Sentinel(kind);
                var result = CatalogTableReader.MapDetail([row], "sentinel-provider", integral);
                Assert.Empty(result.Rows.Columns);
                Assert.Equal("sentinel-provider", result.Provider);
                AssertSource(row, result.Rows.Source);
            }
            foreach (var field in ColumnFields)
            {
                var missing = Sentinel("null");
                missing.Remove(field);
                Bad(() => CatalogTableReader.MapDetail([missing], "provider", integral));
                foreach (var value in new object[] { "present", false, 0 })
                {
                    var broken = Sentinel("mixed");
                    broken[field] = value;
                    Bad(() => CatalogTableReader.MapDetail([broken], "provider", integral));
                    Bad(() => CatalogTableReader.MapDetail([SourceRow(), broken], "provider", integral));
                }
            }
            var allOff = SourceRow();
            foreach (var flag in Flags)
                allOff[flag] = false;
            var column = Assert.Single(CatalogTableReader.MapDetail([allOff], "provider", integral).Rows.Columns);
            Assert.False(column.CanSelect);
            Assert.False(column.CanFilter);
            Assert.False(column.CanSort);
            Assert.False(column.CanGroup);
            Assert.False(column.CanAggregate);
        }
    }

    /// <summary>Ids and ordinals accept any integer type within 32 bits, row caps must be from 1 to the gateway maximum,
    /// and flags accept 0 or 1 only from an integral provider; everything else is refused.</summary>
    [Fact]
    public void T7_Integral_boolean_and_row_cap_rules_match_each_provider()
    {
        foreach (var integral in new[] { true, false })
        {
            foreach (var value in Integers(1))
            foreach (var field in new[] { "id", "ordinal", "row_cap" })
            {
                var row = SourceRow();
                row[field] = value;
                var result = CatalogTableReader.MapDetail([row], "provider", integral);
                Assert.Equal(field == "id" ? 1 : 7319, result.Rows.Source.Id);
                Assert.Equal(1, result.Rows.Columns[0].Ordinal);
                Assert.Equal(field == "row_cap" ? 1 : 500, result.Rows.Source.RowCap);
            }
            foreach (var cap in new[] { 1, QueryLimits.MaxRowCap })
            {
                var row = SourceRow();
                row["row_cap"] = cap;
                Assert.Equal(cap, CatalogTableReader.MapDetail([row], "provider", integral).Rows.Source.RowCap);
                Assert.Equal(cap, CatalogTableReader.MapList([row])[0].RowCap);
            }
            foreach (var value in new object[] { sbyte.MinValue, sbyte.MaxValue, byte.MaxValue, short.MinValue, short.MaxValue,
                ushort.MaxValue, int.MinValue, int.MaxValue, (uint)int.MaxValue, (long)int.MinValue,
                (long)int.MaxValue, (ulong)int.MaxValue })
            foreach (var field in new[] { "id", "ordinal" })
            {
                var row = SourceRow();
                row[field] = value;
                var actual = CatalogTableReader.MapDetail([row], "provider", integral);
                Assert.Equal(Convert.ToInt32(value), field == "id" ? actual.Rows.Source.Id : actual.Rows.Columns[0].Ordinal);
            }
            foreach (var value in new object[] { sbyte.MaxValue, byte.MaxValue, short.MaxValue, ushort.MaxValue,
                QueryLimits.MaxRowCap, (uint)QueryLimits.MaxRowCap, (long)QueryLimits.MaxRowCap, (ulong)QueryLimits.MaxRowCap })
            {
                var row = SourceRow();
                row["row_cap"] = value;
                Assert.Equal(Convert.ToInt32(value), CatalogTableReader.MapDetail([row], "provider", integral).Rows.Source.RowCap);
                Assert.Equal(Convert.ToInt32(value), CatalogTableReader.MapList([row])[0].RowCap);
            }
            foreach (var cap in new[] { -1, 0, int.MaxValue })
            {
                var row = SourceRow();
                row["row_cap"] = cap;
                Bad(() => CatalogTableReader.MapDetail([row], "provider", integral));
                Bad(() => CatalogTableReader.MapList([row]));
            }
            foreach (var field in new[] { "id", "ordinal", "row_cap" })
            foreach (var value in new object[] { (long)int.MaxValue + 1, long.MinValue, uint.MaxValue, ulong.MaxValue, "1", 1m, 1f, 1d, true, DayOfWeek.Monday })
            {
                var row = SourceRow();
                row[field] = value;
                Bad(() => CatalogTableReader.MapDetail([row], "provider", integral));
                if (field == "row_cap")
                    Bad(() => CatalogTableReader.MapList([row]));
            }
            foreach (var value in new object[] { (sbyte)-1, (short)-2, -3, -4L, int.MinValue })
            {
                var row = SourceRow();
                row["id"] = value;
                row["ordinal"] = value;
                var result = CatalogTableReader.MapDetail([row], "provider", integral);
                Assert.True(result.Rows.Source.Id < 0);
                Assert.True(result.Rows.Columns[0].Ordinal < 0);
            }
            foreach (var field in Flags)
            {
                foreach (var value in new object[] { true, false }.Concat(Integers(0)).Concat(Integers(1)))
                {
                    var row = SourceRow();
                    row[field] = value;
                    if (!integral && value is not bool)
                        Bad(() => CatalogTableReader.MapDetail([row], "provider", integral));
                    else
                    {
                        var column = CatalogTableReader.MapDetail([row], "provider", integral).Rows.Columns[0];
                        var actual = FlagValue(column, field);
                        Assert.Equal(value is bool flag ? flag : Convert.ToInt32(value) == 1, actual);
                    }
                }
                foreach (var value in new object[] { (sbyte)-1, (short)-1, -1, -1L, long.MaxValue, ulong.MaxValue,
                    "true", "1", 1m, 1f, 1d, DayOfWeek.Monday }.Concat(Integers(2)))
                {
                    var row = SourceRow();
                    row[field] = value;
                    Bad(() => CatalogTableReader.MapDetail([row], "provider", integral));
                }
            }
        }
    }

    /// <summary>Each read uses one connection, the caller's token and the service's timeout, binds the source name as a
    /// string parameter (never in the SQL text), and runs the fallback on the same connection only when the source is
    /// not found.</summary>
    [Fact]
    public async Task T8_Commands_forward_timeout_token_source_and_same_connection_fallback()
    {
        foreach (var integral in new[] { true, false })
        foreach (var scenario in new[] { "list", "empty-list", "detail", "sentinel", "miss", "bad-detail", "missing-name", "null-name", "wrong-name" })
        {
            using var probe = Probe();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var sql = Sql(integral);
            var detail = scenario != "list" && scenario != "empty-list";
            probe.BeforeExecute = command =>
            {
                Assert.Equal(budget.Token, probe.ExecuteToken);
                if (command.CommandText == sql.Available)
                {
                    var row = new Dictionary<string, object?> { ["name"] = "available" };
                    if (scenario == "missing-name")
                    {
                        row.Remove("name");
                        row["other"] = "present";
                    }
                    if (scenario == "null-name")
                        row["name"] = null;
                    if (scenario == "wrong-name")
                        row["name"] = 1;
                    Load(probe, row);
                }
                else if (scenario is "miss" or "empty-list" or "missing-name" or "null-name" or "wrong-name")
                    Load(probe, [], SourceRow().Keys.ToArray());
                else if (scenario == "sentinel")
                    Load(probe, Sentinel("mixed"));
                else if (scenario == "bad-detail")
                {
                    var row = SourceRow();
                    row["id"] = "bad";
                    Load(probe, row);
                }
                else
                    Load(probe, SourceRow());
            };
            const string name = " MiXeD ' ; -- /* synthetic */ ";
            object? result = null;
            var error = await Record.ExceptionAsync(async () =>
            {
                result = detail
                    ? await CatalogTableReader.DetailAsync(Context(), sql.Read, sql.Available, name, sql.Provider, integral, 43, budget.Token)
                    : await CatalogTableReader.ListAsync(Context(), sql.List, 41, budget.Token);
            });
            if (scenario is "bad-detail" or "missing-name" or "null-name" or "wrong-name")
            {
                CheckBad(Assert.IsType<InvalidCastException>(error));
                Assert.Null(result);
            }
            else
            {
                Assert.Null(error);
                if (scenario == "miss")
                    Assert.Null(result);
                else if (detail)
                {
                    var actual = Assert.IsType<CatalogDetail>(result);
                    Assert.Equal(sql.Provider, actual.Provider);
                    AssertSource(SourceRow(), actual.Rows.Source);
                    Assert.Equal(scenario == "sentinel" ? 0 : 1, actual.Rows.Columns.Count);
                    if (scenario == "detail")
                        AssertColumn(SourceRow(), actual.Rows.Columns[0]);
                }
                else
                {
                    var actual = Assert.IsAssignableFrom<IReadOnlyList<SourceListRow>>(result);
                    Assert.Equal(scenario == "empty-list" ? 0 : 1, actual.Count);
                    if (scenario == "list")
                        AssertList(SourceRow(), actual[0]);
                }
            }
            var fallback = scenario is "miss" or "missing-name" or "null-name" or "wrong-name";
            Assert.Equal(fallback ? 2 : 1, probe.Commands.Count);
            Assert.Single(probe.Connections);
            Assert.Equal(1, probe.ConnectionDisposals);
            Assert.Equal(probe.Commands.Count, probe.ReaderDisposals);
            Assert.Equal(budget.Token, probe.OpenToken);
            Assert.Equal(budget.Token, probe.ExecuteToken);
            Assert.All(probe.ReadTokens, token => Assert.Equal(budget.Token, token));
            Assert.All(probe.Commands, command => Assert.Equal(detail ? 43 : 41, command.CommandTimeout));
            Assert.All(probe.Commands, command => Assert.Same(probe.Connections[0], command.Connection));
            Assert.Equal(detail ? sql.Read : sql.List, probe.Commands[0].CommandText);
            if (detail)
            {
                var parameter = Assert.Single(probe.Commands[0].Parameters.Cast<DbParameter>());
                Assert.Equal("source", parameter.ParameterName.TrimStart('@'));
                Assert.Equal(DbType.String, parameter.DbType);
                Assert.True(string.Equals(name, parameter.Value as string, StringComparison.Ordinal), "Source parameter differs.");
                Assert.False(probe.Commands[0].CommandText.Contains(name, StringComparison.Ordinal));
            }
            else
                Assert.Empty(probe.Commands[0].Parameters);
            if (fallback)
            {
                Assert.Equal(sql.Available, probe.Commands[1].CommandText);
                Assert.Empty(probe.Commands[1].Parameters);
            }
        }
    }

    /// <summary>Cancellation during open, execute, read, cleanup or the fallback ends the read with that token, and the
    /// read waits for the connection's cleanup to finish before it completes.</summary>
    [Fact]
    public async Task T9_Cancellation_after_entry_and_during_cleanup_is_observed()
    {
        foreach (var detail in new[] { false, true })
        foreach (var stage in new[] { "open", "execute", "read", "cleanup", "fallback" })
        {
            if (!detail && stage == "fallback")
                continue;
            using var probe = Probe();
            using var cancellation = new CancellationTokenSource();
            var entered = SourceProbe.Gate();
            var gate = SourceProbe.Gate();
            probe.CleanupGate = SourceProbe.Gate();
            probe.CleanupEntered = SourceProbe.Gate();
            if (stage == "open")
            {
                probe.OpenGate = gate;
                probe.OpenEntered = entered;
            }
            if (stage == "execute")
            {
                probe.ExecuteGate = gate;
                probe.ExecuteEntered = entered;
            }
            if (stage is "read" or "fallback")
            {
                probe.ReadGate = gate;
                probe.ReadEntered = entered;
            }
            if (stage == "cleanup")
                entered = probe.CleanupEntered;
            probe.BeforeExecute = command =>
            {
                if (stage == "fallback" && command.CommandText == SqlServerCatalog.ReadSql)
                {
                    probe.ReadGate = null;
                    probe.ReadEntered = null;
                    Load(probe, [], SourceRow().Keys.ToArray());
                }
                else if (stage == "fallback")
                {
                    probe.ReadGate = gate;
                    probe.ReadEntered = entered;
                    probe.GateReadAt = probe.Reads + 1;
                    Load(probe, new Dictionary<string, object?> { ["name"] = "available" });
                }
                else
                    Load(probe, SourceRow());
            };
            Task operation = Read(detail, cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await probe.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(operation.IsCompleted, "Operation must await connection cleanup.");
                probe.CleanupGate.TrySetResult();
                await Cancelled(() => operation, cancellation.Token);
                Assert.Equal(1, probe.ConnectionDisposals);
                Assert.True(probe.Connections.All(connection => connection.IsDisposed));
                Assert.Equal(stage is "open" or "execute" ? 0 : stage == "fallback" ? 2 : 1, probe.ReaderDisposals);
                Assert.Equal(stage == "fallback" ? 2 : stage == "open" ? 0 : 1, probe.Commands.Count);
            }
            finally
            {
                gate.TrySetResult();
                probe.CleanupGate.TrySetResult();
                await Record.ExceptionAsync(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }
    }

    /// <summary>A failure at any stage, including a bad second row, propagates without returning partial rows or a
    /// default value, and the connection is still disposed.</summary>
    [Fact]
    public async Task T10_Failures_throw_without_partial_or_default_results()
    {
        foreach (var detail in new[] { false, true })
        foreach (var stage in new[] { "open", "execute", "read", "late-read", "fallback", "cleanup", "shape" })
        foreach (var timeout in new[] { false, true })
        {
            if (!detail && stage == "fallback")
                continue;
            using var probe = Probe();
            Exception failure = timeout ? new TimeoutException("Synthetic timeout.") : new IOException("Synthetic I/O failure.");
            if (stage == "open")
                probe.OpenError = failure;
            if (stage == "execute")
                probe.ExecuteError = failure;
            if (stage is "read" or "late-read")
            {
                probe.ReadError = failure;
                probe.FailReadAt = stage == "read" ? 1 : 2;
            }
            if (stage == "cleanup")
                probe.ConnectionCleanupError = failure;
            probe.BeforeExecute = command =>
            {
                if (stage == "fallback" && command.CommandText == SqlServerCatalog.ReadSql)
                    Load(probe, [], SourceRow().Keys.ToArray());
                else if (stage == "fallback")
                {
                    probe.ReadError = failure;
                    probe.FailReadAt = probe.Reads + 1;
                    Load(probe, new Dictionary<string, object?> { ["name"] = "available" });
                }
                else if (stage == "shape")
                {
                    var broken = SourceRow();
                    broken["title"] = false;
                    Load(probe, [SourceRow(), broken], SourceRow().Keys.ToArray());
                }
                else
                    Load(probe, SourceRow());
            };
            object? result = null;
            var error = await Record.ExceptionAsync(async () => result = await Read(detail, CancellationToken.None));
            Assert.Null(result);
            if (stage == "shape")
                CheckBad(Assert.IsType<InvalidCastException>(error));
            else
                Assert.Same(failure, error);
            Assert.Equal(1, probe.ConnectionDisposals);
            if (stage == "late-read")
                Assert.Equal(1, probe.SuccessfulReads);
            Assert.Equal(stage == "fallback" ? 2 : stage == "open" ? 0 : 1, probe.Commands.Count);
        }
    }

    /// <summary>Each read opens a fresh connection and returns new objects, so a later read reflects catalog changes and
    /// leaves earlier results untouched.</summary>
    [Fact]
    public async Task T11_Repeated_reads_are_fresh_buffered_and_detached()
    {
        IReadOnlyList<SourceListRow> firstList;
        IReadOnlyList<SourceListRow> secondList;
        CatalogDetail firstDetail;
        CatalogDetail secondDetail;
        using (var probe = Probe())
        {
            var context = Context();
            var version = 0;
            var miss = false;
            probe.BeforeExecute = command =>
            {
                var row = SourceRow();
                row["title"] = version == 0 ? "old title" : "new title";
                row["column_title"] = version == 0 ? "old column" : "new column";
                row["row_cap"] = version == 0 ? 3 : 7;
                if (command.CommandText == SqlServerCatalog.ReadSql && miss)
                    Load(probe, [], row.Keys.ToArray());
                else
                    Load(probe, row);
            };
            firstList = await CatalogTableReader.ListAsync(context, SqlServerCatalog.ListSql, 41, CancellationToken.None);
            CheckCompleted(probe, 1, 1);
            firstDetail = (await CatalogTableReader.DetailAsync(context, SqlServerCatalog.ReadSql, SqlServerCatalog.AvailableSql,
                "synthetic", "provider", true, 43, CancellationToken.None))!;
            CheckCompleted(probe, 2, 2);
            version = 1;
            secondList = await CatalogTableReader.ListAsync(context, SqlServerCatalog.ListSql, 41, CancellationToken.None);
            CheckCompleted(probe, 3, 3);
            secondDetail = (await CatalogTableReader.DetailAsync(context, SqlServerCatalog.ReadSql, SqlServerCatalog.AvailableSql,
                "synthetic", "provider", true, 43, CancellationToken.None))!;
            CheckCompleted(probe, 4, 4);
            foreach (var absent in new[] { true, false, false, true })
            {
                miss = absent;
                var actual = await CatalogTableReader.DetailAsync(context, SqlServerCatalog.ReadSql, SqlServerCatalog.AvailableSql,
                    "synthetic", "provider", true, 43, CancellationToken.None);
                Assert.Equal(absent, actual is null);
                CheckCompleted(probe, probe.Connections.Count, probe.Commands.Count);
            }
            Assert.Equal(8, probe.Connections.Count);
            Assert.Equal(10, probe.Commands.Count);
            Assert.All(probe.Commands, command => Assert.Contains(command.CommandText,
                new[] { SqlServerCatalog.ListSql, SqlServerCatalog.ReadSql, SqlServerCatalog.AvailableSql }));
        }
        Assert.Equal("old title", firstList[0].Title);
        Assert.Equal(3, firstList[0].RowCap);
        Assert.Equal("new title", secondList[0].Title);
        Assert.Equal(7, secondList[0].RowCap);
        Assert.Equal("old title", firstDetail.Rows.Source.Title);
        Assert.Equal("old column", firstDetail.Rows.Columns[0].Title);
        Assert.Equal(3, firstDetail.Rows.Source.RowCap);
        Assert.Equal("new title", secondDetail.Rows.Source.Title);
        Assert.Equal("new column", secondDetail.Rows.Columns[0].Title);
        Assert.Equal(7, secondDetail.Rows.Source.RowCap);
        Assert.NotSame(firstList, secondList);
        Assert.NotSame(firstDetail.Rows.Source, secondDetail.Rows.Source);
        Assert.NotSame(firstDetail.Rows.Columns, secondDetail.Rows.Columns);
    }

    /// <summary>A source's object name and connection never appear in a mapping error, in the detail's JSON or text form,
    /// or even in this test class's own assertion messages.</summary>
    [Fact]
    public void T12_Secret_values_never_enter_diagnostics_or_assertion_output()
    {
        var row = SourceRow();
        row["object_name"] = SecretObject;
        row["connection_string"] = SecretConnection;
        var detail = CatalogTableReader.MapDetail([row], "provider", true);
        foreach (var scenario in new[] { "list", "detail", "fallback", "sentinel", "overflow", "poison" })
        {
            var broken = new Dictionary<string, object?>(row);
            if (scenario == "sentinel")
                broken["column_name"] = null;
            else if (scenario == "overflow")
                broken["id"] = ulong.MaxValue;
            else
                broken[scenario == "fallback" ? "name" : "title"] = scenario == "poison" ? new Poison() : null;
            Action action = scenario switch
            {
                "list" => () => CatalogTableReader.MapList([broken]),
                "fallback" => () => CatalogTableReader.ValidateAvailable([broken]),
                _ => () => CatalogTableReader.MapDetail([broken], "provider", true)
            };
            var error = Bad(action);
            SafeText(error.Message);
            SafeText(error.ToString());
        }
        var json = JsonSerializer.Serialize(detail, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        SafeText(json);
        Assert.False(json.Contains("connectionString", StringComparison.OrdinalIgnoreCase), "Private property leaked.");
        Assert.False(json.Contains("objectName", StringComparison.OrdinalIgnoreCase), "Private property leaked.");
        Assert.False(json.Contains("\"id\"", StringComparison.OrdinalIgnoreCase), "Private property leaked.");
        Assert.True(json.Contains("synthetic", StringComparison.Ordinal), "Public metadata missing.");
        SafeText(detail.Rows.Source.ToString()!);
        foreach (var field in new[] { "connection_string", "object_name" })
        {
            var changed = new Dictionary<string, object?>(row);
            changed[field] = field == "object_name" ? SecretObject + "_different" : SecretConnection + "_different";
            var other = CatalogTableReader.MapDetail([changed], "provider", true);
            var assertion = Record.Exception(() => AssertDetail(detail.Rows, other, "provider"));
            Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(assertion);
            SafeText(assertion!.Message);
        }
    }

    /// <summary>A probe database that allows the fallback's second result set.</summary>
    private static SourceProbe Probe() => new() { AllowNextResult = true };
    /// <summary>A database context that opens a new probe connection per read.</summary>
    private static DbContext<ProbeConnection> Context() => DbContext<ProbeConnection>.FromFactory(() => new ProbeConnection());
    /// <summary>Runs the SQL Server list or detail read against the probe.</summary>
    private static async Task<object?> Read(bool detail, CancellationToken token) => detail
        ? await CatalogTableReader.DetailAsync(Context(), SqlServerCatalog.ReadSql, SqlServerCatalog.AvailableSql,
            "synthetic", SqlServerEngine.ProviderName, true, 43, token)
        : await CatalogTableReader.ListAsync(Context(), SqlServerCatalog.ListSql, 41, token);

    /// <summary>One service's three SQL texts and provider name: SQL Server when <paramref name="integral"/>, else PostgreSQL.</summary>
    private static (string List, string Read, string Available, string Provider) Sql(bool integral) => integral
        ? (SqlServerCatalog.ListSql, SqlServerCatalog.ReadSql, SqlServerCatalog.AvailableSql, SqlServerEngine.ProviderName)
        : (PostgreSqlCatalog.ListSql, PostgreSqlCatalog.ReadSql, PostgreSqlCatalog.AvailableSql, PostgreSqlEngine.ProviderName);

    /// <summary>Asserts a service's public shape: sealed, derived from the catalog service, one constructor taking the
    /// context and an optional 15-second timeout, and exactly the three overridden read methods.</summary>
    private static void CheckSurface(Type service, Type connection)
    {
        Assert.True(service.IsPublic);
        Assert.True(service.IsSealed);
        Assert.Equal(typeof(CatalogService<>).MakeGenericType(connection), service.BaseType);
        var constructor = Assert.Single(service.GetConstructors());
        var parameters = constructor.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(DbContext<>).MakeGenericType(connection), parameters[0].ParameterType);
        Assert.Equal(typeof(int), parameters[1].ParameterType);
        Assert.Equal("db", parameters[0].Name);
        Assert.False(parameters[0].IsOptional);
        Assert.False(parameters[0].HasDefaultValue);
        Assert.Equal("catalogTimeoutSeconds", parameters[1].Name);
        Assert.True(parameters[1].IsOptional);
        Assert.True(parameters[1].HasDefaultValue);
        Assert.Equal(15, parameters[1].DefaultValue);
        var methods = service.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.Equal(3, methods.Length);
        Assert.Equal(new[] { "QueryCatalogAsync", "QueryCatalogDetailAsync", "QueryMcpMetadataAsync" }, methods.Select(method => method.Name).Order());
        var metadata = Assert.Single(methods, method => method.Name == "QueryMcpMetadataAsync");
        Assert.Equal(typeof(Task<McpMetadata>), metadata.ReturnType);
        Assert.Equal(typeof(CancellationToken), Assert.Single(metadata.GetParameters()).ParameterType);
        Assert.False(metadata.GetParameters()[0].IsOptional);
        Assert.All(methods, method => Assert.NotEqual(method, method.GetBaseDefinition()));
    }

    /// <summary>Asserts the operation ends in cancellation for <paramref name="token"/>.</summary>
    private static async Task Cancelled(Func<Task> operation, CancellationToken token)
    {
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(token, error.CancellationToken);
    }

    /// <summary>Asserts the operation fails with the fixed bad-cell error, and returns it.</summary>
    private static InvalidCastException Bad(Action operation)
    {
        var error = Assert.Throws<InvalidCastException>(operation);
        CheckBad(error);
        return error;
    }

    /// <summary>Asserts the fixed bad-cell message and that no inner exception carries the cell.</summary>
    private static void CheckBad(InvalidCastException error)
    {
        Assert.Equal("The catalog cell shape is invalid.", error.Message);
        Assert.Equal(34, Encoding.UTF8.GetByteCount(error.Message));
        Assert.Null(error.InnerException);
    }

    /// <summary>Removes <paramref name="field"/> or sets it to null, DBNull or an object of the wrong type.</summary>
    private static void Mutate(Dictionary<string, object?> row, string field, string kind)
    {
        if (kind == "missing")
            row.Remove(field);
        else
            row[field] = kind == "null" ? null : kind == "dbnull" ? DBNull.Value : new object();
    }

    /// <summary>A sentinel row whose column cells are null, DBNull, or a mix of both.</summary>
    private static Dictionary<string, object?> Sentinel(string kind)
    {
        var row = SourceRow();
        for (var i = 0; i < ColumnFields.Length; i++)
            row[ColumnFields[i]] = kind == "dbnull" || kind == "mixed" && i % 2 == 0 ? DBNull.Value : null;
        return row;
    }

    /// <summary><paramref name="value"/> as each of the eight integer types.</summary>
    private static object[] Integers(int value) => [(sbyte)value, (byte)value, (short)value, (ushort)value, value, (uint)value, (long)value, (ulong)value];

    /// <summary>The mapped column's value for a flag field name.</summary>
    private static bool FlagValue(DataSourceColumnRow column, string field) => field switch
    {
        "can_select" => column.CanSelect,
        "can_filter" => column.CanFilter,
        "can_sort" => column.CanSort,
        "can_group" => column.CanGroup,
        "can_aggregate" => column.CanAggregate,
        _ => throw new ArgumentException("Unknown synthetic flag.")
    };

    /// <summary>A detail row with padded, non-ASCII text, secret values and alternating flags, to catch any trimming or
    /// field mix-up.</summary>
    private static Dictionary<string, object?> RichRow(int ordinal)
    {
        var row = SourceRow();
        row["name"] = " Mixed \u03a9 ";
        row["title"] = " Source \u03b2 ";
        row["description"] = " Source description \u4e2d ";
        row["grain"] = " Exact grain ";
        row["object_name"] = SecretObject;
        row["connection_string"] = SecretConnection;
        row["column_name"] = " column \u03a9 " + ordinal;
        row["ordinal"] = ordinal;
        row["column_title"] = " title " + ordinal;
        row["column_description"] = " description " + ordinal;
        row["data_type"] = " type " + ordinal;
        row["unit"] = ordinal == 2 ? " unit \u03b2 " : null;
        row["magnitude"] = ordinal == 2 ? null : " magnitude ";
        row["display_format"] = ordinal == 2 ? " format " : null;
        for (var i = 0; i < Flags.Length; i++)
            row[Flags[i]] = (i + ordinal) % 2 == 0;
        return row;
    }

    /// <summary>The detail <see cref="RichRow"/> rows must map to.</summary>
    private static CatalogDetail ExpectedDetail(Dictionary<string, object?> first, Dictionary<string, object?> second, string provider) =>
        new(provider, new CatalogRows(new DataSourceRow((string)first["object_name"]!, (string)first["connection_string"]!)
        { QueryTimeoutSeconds = 30,
            Id = 7319,
            Name = " Mixed \u03a9 ",
            Title = " Source \u03b2 ",
            Description = " Source description \u4e2d ",
            Grain = " Exact grain ",
            IsActive = true,
            RowCap = 500
        }, [new(" column \u03a9 2", 2, " title 2", " description 2", " type 2", " unit \u03b2 ", null, " format ", true, false, true, false, true),
            new(" column \u03a9 1", 1, " title 1", " description 1", " type 1", null, " magnitude ", null, false, true, false, true, false)]));

    /// <summary>Asserts a list row matches its raw row.</summary>
    private static void AssertList(IDictionary<string, object?> expected, SourceListRow actual)
    {
        Assert.Equal(expected["name"], actual.Name);
        Assert.Equal(expected["title"], actual.Title);
        Assert.Equal(expected["description"], actual.Description);
        Assert.Equal(expected["grain"], actual.Grain);
        Assert.True(actual.IsActive);
        Assert.Equal(expected["row_cap"], actual.RowCap);
    }

    /// <summary>Asserts a source matches its raw row, comparing the secret fields without printing them.</summary>
    private static void AssertSource(IDictionary<string, object?> expected, DataSourceRow actual)
    {
        Assert.Equal(expected["id"], actual.Id);
        Assert.Equal(expected["name"], actual.Name);
        Assert.Equal(expected["title"], actual.Title);
        Assert.Equal(expected["description"], actual.Description);
        Assert.Equal(expected["grain"], actual.Grain);
        Assert.True(actual.IsActive);
        Assert.Equal(expected["row_cap"], actual.RowCap);
        var sameConnection = string.Equals(expected["connection_string"] as string, actual.ConnectionString, StringComparison.Ordinal);
        Assert.True(sameConnection, "Catalog connection differs.");
        var sameObject = string.Equals(expected["object_name"] as string, actual.ObjectName, StringComparison.Ordinal);
        Assert.True(sameObject, "Catalog object differs.");
    }

    /// <summary>Asserts a column matches its raw row.</summary>
    private static void AssertColumn(IDictionary<string, object?> expected, DataSourceColumnRow actual)
    {
        Assert.Equal(expected["column_name"], actual.ColumnName);
        Assert.Equal(expected["ordinal"], actual.Ordinal);
        Assert.Equal(expected["column_title"], actual.Title);
        Assert.Equal(expected["column_description"], actual.Description);
        Assert.Equal(expected["data_type"], actual.DataType);
        Assert.Equal(expected["unit"], actual.Unit);
        Assert.Equal(expected["magnitude"], actual.Magnitude);
        Assert.Equal(expected["display_format"], actual.DisplayFormat);
        Assert.Equal(expected["can_select"], actual.CanSelect);
        Assert.Equal(expected["can_filter"], actual.CanFilter);
        Assert.Equal(expected["can_sort"], actual.CanSort);
        Assert.Equal(expected["can_group"], actual.CanGroup);
        Assert.Equal(expected["can_aggregate"], actual.CanAggregate);
    }

    /// <summary>Asserts two details are equal field by field, comparing the secret fields without printing them.</summary>
    /// <remarks>A plain equality assertion would print both records on failure, connection string included; the boolean
    /// checks keep a failing test from leaking what it compares.</remarks>
    private static void AssertDetail(CatalogRows expected, CatalogDetail actual, string provider)
    {
        Assert.Equal(provider, actual.Provider);
        Assert.Equal(expected.Source.Id, actual.Rows.Source.Id);
        Assert.Equal(expected.Source.Name, actual.Rows.Source.Name);
        Assert.Equal(expected.Source.Title, actual.Rows.Source.Title);
        Assert.Equal(expected.Source.Description, actual.Rows.Source.Description);
        Assert.Equal(expected.Source.Grain, actual.Rows.Source.Grain);
        Assert.Equal(expected.Source.IsActive, actual.Rows.Source.IsActive);
        Assert.Equal(expected.Source.RowCap, actual.Rows.Source.RowCap);
        var sameConnection = string.Equals(expected.Source.ConnectionString,
            actual.Rows.Source.ConnectionString, StringComparison.Ordinal);
        Assert.True(sameConnection, "Catalog connection differs.");
        var sameObject = string.Equals(expected.Source.ObjectName,
            actual.Rows.Source.ObjectName, StringComparison.Ordinal);
        Assert.True(sameObject, "Catalog object differs.");
        Assert.Equal(expected.Columns.Count, actual.Rows.Columns.Count);
        for (var i = 0; i < expected.Columns.Count; i++)
        {
            var left = expected.Columns[i];
            var right = actual.Rows.Columns[i];
            Assert.Equal(left.ColumnName, right.ColumnName);
            Assert.Equal(left.Ordinal, right.Ordinal);
            Assert.Equal(left.Title, right.Title);
            Assert.Equal(left.Description, right.Description);
            Assert.Equal(left.DataType, right.DataType);
            Assert.Equal(left.Unit, right.Unit);
            Assert.Equal(left.Magnitude, right.Magnitude);
            Assert.Equal(left.DisplayFormat, right.DisplayFormat);
            Assert.Equal(left.CanSelect, right.CanSelect);
            Assert.Equal(left.CanFilter, right.CanFilter);
            Assert.Equal(left.CanSort, right.CanSort);
            Assert.Equal(left.CanGroup, right.CanGroup);
            Assert.Equal(left.CanAggregate, right.CanAggregate);
        }
    }

    /// <summary>Asserts every connection was opened and disposed, and every command's reader was disposed and asked for
    /// one further result set.</summary>
    private static void CheckCompleted(SourceProbe probe, int connections, int commands)
    {
        Assert.Equal(connections, probe.Connections.Count);
        Assert.Equal(connections, probe.ConnectionDisposals);
        Assert.Equal(commands, probe.Commands.Count);
        Assert.Equal(commands, probe.ReaderDisposals);
        Assert.All(probe.Connections, connection => Assert.True(connection.IsDisposed));
        Assert.All(probe.Connections, connection => Assert.True(connection.WasOpened));
        Assert.Equal(commands, probe.NextResults);
    }

    /// <summary>Fails if <paramref name="text"/> contains either secret value.</summary>
    private static void SafeText(string text)
    {
        Assert.False(text.Contains(SecretObject, StringComparison.Ordinal), "Private object leaked.");
        Assert.False(text.Contains(SecretConnection, StringComparison.Ordinal), "Private connection leaked.");
    }

    /// <summary>A cell value whose text form throws, to prove a mapping error never formats the cell.</summary>
    private sealed class Poison
    {
        /// <summary>Throws.</summary>
        public override string ToString() => throw new InvalidOperationException("Cell must not be formatted.");
    }
}
