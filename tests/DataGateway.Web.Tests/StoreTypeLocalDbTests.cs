using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Queries;
using DataGateway.Data.SqlServer;
using DataGateway.Core.Tests;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>A fact that runs only where SQL Server LocalDB answers; elsewhere it is reported as skipped, not passed.</summary>
public sealed class LocalDbFactAttribute : FactAttribute
{
    internal const string Server = @"(localdb)\MSSQLLocalDB";

    private static readonly Lazy<string?> Unavailable = new(() =>
    {
        try
        {
            using var probe = new SqlConnection(StoreTypeLocalDbTests.ConnectionTo("master"));
            probe.Open();
            return null;
        }
        catch (Exception error) when (error is SqlException or InvalidOperationException or PlatformNotSupportedException)
        {
            return $"SQL Server LocalDB ({Server}) is not available: {error.GetType().Name}.";
        }
    });

    public LocalDbFactAttribute()
    {
        if (Unavailable.Value is { } reason) Skip = reason;
    }
}

/// <summary>The store-type metadata query, check and fill tool on a real SQL Server, in a scratch
/// LocalDB database named dg_st_* that each test creates and drops.</summary>
public sealed class StoreTypeLocalDbTests
{
    internal static string ConnectionTo(string catalog) =>
        $"Data Source={LocalDbFactAttribute.Server};Initial Catalog={catalog};Integrated Security=true;TrustServerCertificate=true;Connect Timeout=10;Pooling=false";

    private const string Schema = """
        CREATE TABLE dbo.st_items (
            id int NOT NULL,
            v20 varchar(20) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
            vmax nvarchar(max) NULL,
            c10 char(10) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
            n5 nchar(5) NULL,
            u8 varchar(20) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
            i int NULL,
            d decimal(19,4) NULL,
            dt datetime NULL,
            dd date NULL,
            b bit NULL);
        """;

    [LocalDbFact]
    public async Task T1_the_metadata_query_reads_each_declared_type_and_utf8_collation()
    {
        await using var scratch = await Scratch.CreateAsync();
        await scratch.ExecuteAsync(Schema);
        await scratch.ExecuteAsync("CREATE VIEW dbo.vst AS SELECT id, v20, vmax, c10, n5, u8, i, d, dt, dd, b FROM dbo.st_items;");
        var columns = await SqlServerColumnTypes.ReadAsync(scratch.Connection, "dbo.vst", 15, default);
        Assert.Equal(new (string, string, bool)[]
        {
            ("id", "int", false), ("v20", "varchar(20)", false), ("vmax", "nvarchar(max)", false), ("c10", "char(10)", false),
            ("n5", "nchar(5)", false), ("u8", "varchar(20)", true), ("i", "int", false), ("d", "decimal(19,4)", false),
            ("dt", "datetime", false), ("dd", "date", false), ("b", "bit", false)
        }, columns.Select(column => (column.ColumnName, column.Spelling, column.Utf8Collation)));
        Assert.All(columns, column => Assert.NotNull(column.StoreType));
        Assert.Equal("Latin1_General_100_CI_AS_SC_UTF8", columns.Single(column => column.ColumnName == "u8").Collation);
        // A bare name resolves in the login's default schema, as the source query does; an unknown view reads as empty.
        Assert.Equal(11, (await SqlServerColumnTypes.ReadAsync(scratch.Connection, "vst", 15, default)).Count);
        Assert.Empty(await SqlServerColumnTypes.ReadAsync(scratch.Connection, "dbo.missing", 15, default));
    }

    [LocalDbFact]
    public async Task T2_the_engine_checks_the_view_binds_agreeing_columns_and_runs_the_typed_query()
    {
        await using var scratch = await Scratch.CreateAsync();
        await scratch.ExecuteAsync(Schema);
        await scratch.ExecuteAsync("CREATE VIEW dbo.vst AS SELECT id, v20, vmax, c10, n5, u8, i, d, dt, dd, b FROM dbo.st_items;");
        await scratch.ExecuteAsync("INSERT INTO dbo.st_items (id, v20, vmax, c10, u8) VALUES (1, 'abc', N'long', 'ABC', 'ABCD'), (2, 'xyz', N'x', 'XYZ', 'WXYZ');");
        var contract = Contract(scratch.Connection, ("v20", "varchar(20)", false), ("vmax", "nvarchar(40)", false),
            ("c10", "char(10)", false), ("u8", "varchar(20)", false));
        var engine = new SqlServerEngine();
        var runner = new QueryRunner(engine);
        var findings = new List<StoreTypeFinding>();
        var validated = QueryBuilding.Validate(contract,
            """{"select":["id"],"filters":[{"col":"v20","op":"eq","value":"abc"},{"col":"c10","op":"like","value":"AB%"},{"col":"vmax","op":"contains","value":"on"},{"col":"u8","op":"eq","value":"ABCD"}]}""",
            QueryLimits.ForRowCap(500)).Value;
        var compiled = await runner.BindStoreTypesAsync(contract, runner.Compile(validated), findings.Add, default);
        Assert.Equal(new (string, string, bool)[] { ("@p1", "varchar(20)", false), ("@p2", "char(10)", false), ("@p4", "varchar(20)", true) },
            compiled.StoreTypes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value.Spelling, pair.Value.Utf8Collation)));
        Assert.Equal(new StoreTypeFinding(StoreTypeSeverity.Error, "st", "vmax",
            "Source 'st', column 'vmax': the catalog's store_type is nvarchar(40) but the view's column is nvarchar(max), so the " +
            "column binds as if store_type were empty. Run the fill tool (SqlServerStoreTypeScript.CreateAsync) or correct store_type."),
            Assert.Single(findings));
        // LIKE 'AB%' against char(10) binds as varchar(10), so the stored 'ABC       ' matches.
        var rows = await runner.RunAsync(contract, compiled, default);
        Assert.True(rows.IsSuccess, rows.Failure?.Message);
        Assert.Equal(1, Assert.Single(rows.Value.Rows)["id"]);
        // Checked once per engine: a second query reads no metadata and reports nothing new.
        await runner.BindStoreTypesAsync(contract, runner.Compile(validated), findings.Add, default);
        Assert.Single(findings);
    }

    [LocalDbFact]
    public async Task T3_the_fill_script_runs_on_a_sql_server_catalog_and_leaves_overrides_alone()
    {
        await using var scratch = await Scratch.CreateAsync();
        await scratch.ExecuteAsync(Schema);
        await scratch.ExecuteAsync("CREATE VIEW dbo.vst AS SELECT id, v20, vmax, c10, n5, u8, i, d, dt, dd, b FROM dbo.st_items;");
        await scratch.RunScriptAsync(File.ReadAllText(TestFiles.InRepo("db/catalog-ddl.sql")));
        await scratch.ExecuteAsync("""
            INSERT INTO dbo.DataSource ([name], [connection_string], [object_name], [title], [description], [grain]) VALUES ('st', 'unused', 'dbo.vst', N'Store types', N'Invented.', N'One row');
            INSERT INTO dbo.DataSourceColumn ([datasource_fk], [column_name], [ordinal], [title], [description], [data_type], [store_type], [store_type_override])
            SELECT [id], c.[name], c.[ordinal], c.[name], N'Invented.', 'text', c.[store_type], c.[override] FROM dbo.DataSource
            CROSS JOIN (VALUES ('v20', 1, NULL, 0), ('vmax', 2, N'nvarchar(40)', 0), ('c10', 3, N'char(10)', 0), ('n5', 4, N'nchar(9)', 1), ('i', 5, N'bigint', 0))
                AS c([name], [ordinal], [store_type], [override]);
            """);
        var sourceId = Convert.ToInt32(await scratch.ScalarAsync("SELECT [id] FROM dbo.DataSource;"));
        var rows = new CatalogRows(new DataSourceRow("dbo.vst", scratch.Connection)
        {
            Id = sourceId, Name = "st", Title = "Store types", Description = "Invented.", Grain = "One row", IsActive = true, RowCap = 500, QueryTimeoutSeconds = 30
        },
        [
            Column("v20", 1, null), Column("vmax", 2, "nvarchar(40)"), Column("c10", 3, "char(10)"),
            Column("n5", 4, "nchar(9)", overridden: true), Column("i", 5, "bigint")
        ]);
        var script = await SqlServerStoreTypeScript.CreateAsync([rows], CatalogScriptDialect.SqlServer, 15, default);
        var updates = script.Split('\n').Where(line => line.StartsWith("UPDATE", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, updates.Length);
        Assert.Contains("--   Column 'n5' keeps store_type nchar(9) (store_type_override is set); the view's type is nchar(5).", script);
        // v20 is varchar under a SQL_ collation, where a store_type changes comparison rules: named, never filled.
        Assert.Contains("--   Column 'v20' (store_type empty) is varchar(20) under the SQL collation SQL_Latin1_General_CP1_CI_AS, ", script);
        await scratch.ExecuteAsync(script);
        var stored = await scratch.ReadAsync("SELECT [column_name], ISNULL([store_type], N'NULL'), [store_type_override] FROM dbo.DataSourceColumn ORDER BY [ordinal];");
        Assert.Equal(new[] { "v20|NULL|False", "vmax|nvarchar(max)|False", "c10|char(10)|False", "n5|nchar(9)|True", "i|int|False" }, stored);
        var audited = await scratch.ReadAsync("SELECT [audit_action] + ':' + [column_name], ISNULL([store_type], N'NULL'), [store_type_override] FROM dbo.DataSourceColumnAudit WHERE [audit_action] = 'U' ORDER BY [id];");
        Assert.Equal(new[] { "U:vmax|nvarchar(max)|False", "U:i|int|False" }, audited);
        // Run again on the catalog with v20 set by its owner: nothing left to write.
        var corrected = new CatalogRows(rows.Source, [Column("v20", 1, "varchar(20)"), Column("vmax", 2, "nvarchar(max)"),
            Column("c10", 3, "char(10)"), Column("n5", 4, "nchar(9)", overridden: true), Column("i", 5, "int")]);
        Assert.DoesNotContain("UPDATE", await SqlServerStoreTypeScript.CreateAsync([corrected], CatalogScriptDialect.SqlServer, 15, default), StringComparison.Ordinal);
    }

    [LocalDbFact]
    public async Task T4_a_synonym_for_a_local_object_is_followed_and_a_remote_missing_or_database_qualified_object_is_refused()
    {
        await using var scratch = await Scratch.CreateAsync();
        await scratch.ExecuteAsync(Schema);
        await scratch.ExecuteAsync("CREATE VIEW dbo.vst AS SELECT id, v20, u8 FROM dbo.st_items;");
        await scratch.ExecuteAsync($"CREATE SYNONYM dbo.local_view FOR dbo.vst; CREATE SYNONYM dbo.same_db_view FOR [{scratch.Name}].dbo.vst; " +
            "CREATE SYNONYM dbo.other_db FOR master.dbo.spt_values; CREATE SYNONYM dbo.gone FOR dbo.no_such_view; " +
            "CREATE SYNONYM dbo.linked FOR remote_server.remote_db.dbo.v;");
        foreach (var name in new[] { "dbo.local_view", "local_view", "dbo.same_db_view" })
            Assert.Equal(new[] { "id:int", "v20:varchar(20)", "u8:varchar(20)" },
                (await SqlServerColumnTypes.ReadAsync(scratch.Connection, name, 15, default)).Select(column => column.ColumnName + ":" + column.Spelling));
        foreach (var name in new[] { "dbo.other_db", "dbo.gone", "dbo.linked" })
            await Assert.ThrowsAsync<UnresolvedObjectException>(() => SqlServerColumnTypes.ReadAsync(scratch.Connection, name, 15, default));
        // A name with a database or server part is refused before OBJECT_ID, even this database's own: sys.columns is
        // this database's, so an id from another one would read an unrelated local object's columns.
        foreach (var name in new[] { $"[{scratch.Name}].dbo.vst", $"{scratch.Name}..vst", "master.dbo.spt_values", "master.sys.objects",
                     "remote_server.remote_db.dbo.v", $"[srv].[{scratch.Name}].[dbo].[vst]" })
            await Assert.ThrowsAsync<UnresolvedObjectException>(() => SqlServerColumnTypes.ReadAsync(scratch.Connection, name, 15, default));
        var foreign = new CatalogRows(new DataSourceRow($"{scratch.Name}.dbo.vst", scratch.Connection)
        {
            Id = 3, Name = "foreign", Title = "Foreign", Description = "Invented.", Grain = "One row", IsActive = true, RowCap = 500, QueryTimeoutSeconds = 30
        }, [Column("v20", 1, null)]);
        var script = await SqlServerStoreTypeScript.CreateAsync([foreign], CatalogScriptDialect.SqlServer, 15, default);
        Assert.DoesNotContain("UPDATE", script, StringComparison.Ordinal);
        Assert.Contains("--   The view's column types could not be read (the source object is named with a database or server part, ", script, StringComparison.Ordinal);

        // Through the engine: the local synonym binds as its view's types; the remote one is reported once and binds as preview.6.
        var findings = new List<StoreTypeFinding>();
        var engine = (IStoreTypeEngine)new SqlServerEngine();
        var local = await engine.ResolveStoreTypesAsync(Contract(scratch.Connection, "dbo.local_view", ("v20", "varchar(20)", false)), findings.Add, default);
        Assert.Equal("varchar(20)", Assert.Single(local).Value.Spelling);
        var remote = Contract(scratch.Connection, "dbo.other_db", ("v20", "varchar(20)", false));
        Assert.Empty(await engine.ResolveStoreTypesAsync(remote, findings.Add, default));
        Assert.Empty(await engine.ResolveStoreTypesAsync(remote, findings.Add, default));
        Assert.Contains("It does not try again until the host restarts.", Assert.Single(findings).Message, StringComparison.Ordinal);
        // The same for a source whose object_name names a database: one Error per process, every column as preview.6.
        var qualified = Contract(scratch.Connection, $"{scratch.Name}.dbo.vst", ("v20", "varchar(20)", false));
        Assert.Empty(await engine.ResolveStoreTypesAsync(qualified, findings.Add, default));
        Assert.Empty(await engine.ResolveStoreTypesAsync(qualified, findings.Add, default));
        Assert.Equal(2, findings.Count);
        Assert.Equal(StoreTypeSeverity.Error, findings[1].Severity);
        Assert.Contains("(the source object is named with a database or server part, ", findings[1].Message, StringComparison.Ordinal);
        Assert.Contains("It does not try again until the host restarts.", findings[1].Message, StringComparison.Ordinal);
    }

    // Database names are unique under the instance's collation (accent-sensitive here), so dg_st_*_meta and the same name
    // with an accented e are two databases; under an accent-insensitive source database the two names compare equal. A synonym into the
    // other one must still be refused: OBJECT_ID would find its object, and sys.columns here would read an unrelated one.
    [LocalDbFact]
    public async Task T6_a_synonym_into_another_database_is_refused_even_when_the_source_collation_equates_the_names()
    {
        var stem = "dg_st_" + Guid.NewGuid().ToString("N")[..12];
        await using var source = await Scratch.CreateAsync(stem + "_meta", "Latin1_General_100_CI_AI");
        await using var other = await Scratch.CreateAsync(stem + "_méta", "Latin1_General_100_CI_AS");
        await other.ExecuteAsync("CREATE VIEW dbo.v AS SELECT 1 AS id, CAST('x' AS varchar(20)) AS v20;");
        await source.ExecuteAsync("CREATE VIEW dbo.v AS SELECT 1 AS id;");
        await source.ExecuteAsync($"CREATE SYNONYM dbo.accented FOR [{other.Name}].dbo.v; CREATE SYNONYM dbo.own FOR [{source.Name}].dbo.v; " +
            $"CREATE SYNONYM dbo.no_db FOR [{stem}_none].dbo.v;");
        // The premise: two databases, whose names the source database's collation cannot tell apart.
        Assert.Equal(new[] { "same|different" }, await source.ReadAsync(
            $"SELECT CASE WHEN N'{other.Name}' = DB_NAME() THEN 'same' ELSE 'apart' END, " +
            $"CASE WHEN DB_ID(N'{other.Name}') = DB_ID() THEN 'one' ELSE 'different' END;"));
        await Assert.ThrowsAsync<UnresolvedObjectException>(() => SqlServerColumnTypes.ReadAsync(source.Connection, "dbo.accented", 15, default));
        await Assert.ThrowsAsync<UnresolvedObjectException>(() => SqlServerColumnTypes.ReadAsync(source.Connection, "dbo.no_db", 15, default));
        Assert.Equal(new[] { "id:int" },
            (await SqlServerColumnTypes.ReadAsync(source.Connection, "dbo.own", 15, default)).Select(column => column.ColumnName + ":" + column.Spelling));
    }

    [LocalDbFact]
    public async Task T5_the_store_type_migration_upgrades_a_preview6_catalog_rerun_safely_and_audits_every_change()
    {
        await using var scratch = await Scratch.CreateAsync();
        // The preview.6 catalog, saved byte for byte from the preview.6 release (db/catalog-ddl.sql and db/catalog-views.sql).
        await scratch.RunScriptAsync(File.ReadAllText(TestFiles.InRepo("tests/fixtures/catalog-preview6/catalog-ddl.sql")));
        await scratch.RunScriptAsync(File.ReadAllText(TestFiles.InRepo("tests/fixtures/catalog-preview6/catalog-views.sql")));
        Assert.Equal(0, Convert.ToInt32(await scratch.ScalarAsync("SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'dbo.DataSourceColumn') AND [name] IN (N'expression', N'store_type', N'store_type_override');")));
        await scratch.ExecuteAsync("""
            INSERT INTO dbo.DataSource ([name], [connection_string], [object_name], [title], [description], [grain]) VALUES ('st', 'unused', 'dbo.vst', N'Store types', N'Invented.', N'One row');
            INSERT INTO dbo.DataSourceColumn ([datasource_fk], [column_name], [ordinal], [title], [description], [data_type]) SELECT [id], 'kept', 1, N'Kept', N'A preview.6 row.', 'text' FROM dbo.DataSource;
            GRANT SELECT ON dbo.vDataSourceColumn TO [public];
            """);
        foreach (var migration in new[] { "db/catalog-store-type.sql", "db/catalog-store-type.sql", "db/catalog-expression.sql" })
            await scratch.RunScriptAsync(File.ReadAllText(TestFiles.InRepo(migration)));

        var view = await scratch.ReadAsync("SELECT [name] FROM sys.columns WHERE [object_id] = OBJECT_ID(N'dbo.vDataSourceColumn') ORDER BY [column_id];");
        Assert.Equal(new[] { "datasource_fk", "column_name", "ordinal", "title", "description", "data_type", "unit", "magnitude",
            "display_format", "can_select", "can_filter", "can_sort", "can_group", "can_aggregate", "expression", "store_type", "store_type_override" }, view);
        Assert.Equal(new[] { "DataSourceColumn|DF_DataSourceColumn_store_type_override|((0))|False|bit",
                "DataSourceColumnAudit|DF_DataSourceColumnAudit_store_type_override|((0))|False|bit" },
            await scratch.ReadAsync("""
                SELECT OBJECT_NAME(d.[parent_object_id]), d.[name], d.[definition], c.[is_nullable], TYPE_NAME(c.[system_type_id])
                FROM sys.default_constraints AS d
                JOIN sys.columns AS c ON c.[object_id] = d.[parent_object_id] AND c.[column_id] = d.[parent_column_id]
                WHERE c.[name] = N'store_type_override' ORDER BY 1;
                """));
        Assert.Equal(new[] { "nvarchar|256|True", "nvarchar|256|True" }, await scratch.ReadAsync(
            "SELECT TYPE_NAME([system_type_id]), [max_length], [is_nullable] FROM sys.columns WHERE [name] = N'store_type' AND [object_id] IN (OBJECT_ID(N'dbo.DataSourceColumn'), OBJECT_ID(N'dbo.DataSourceColumnAudit'));"));
        Assert.Equal(new[] { "kept|NULL|NULL|False" }, await scratch.ReadAsync(
            "SELECT [column_name], ISNULL([expression], N'NULL'), ISNULL([store_type], N'NULL'), [store_type_override] FROM dbo.vDataSourceColumn;"));
        Assert.Equal("1", Convert.ToString(await scratch.ScalarAsync(
            "SELECT COUNT(*) FROM sys.database_permissions WHERE [major_id] = OBJECT_ID(N'dbo.vDataSourceColumn') AND [grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'public') AND [permission_name] = N'SELECT' AND [state] = 'G';")));

        await scratch.ExecuteAsync("""
            INSERT INTO dbo.DataSourceColumn ([datasource_fk], [column_name], [ordinal], [title], [description], [data_type], [store_type], [store_type_override])
            SELECT [id], 'probe', 2, N'Probe', N'Invented.', 'text', N'varchar(20)', 1 FROM dbo.DataSource;
            UPDATE dbo.DataSourceColumn SET [store_type] = N'char(10)', [store_type_override] = 0 WHERE [column_name] = 'probe';
            DELETE FROM dbo.DataSourceColumn WHERE [column_name] = 'probe';
            INSERT INTO dbo.DataSourceColumn ([datasource_fk], [column_name], [ordinal], [title], [description], [data_type]) SELECT [id], 'defaulted', 3, N'Defaulted', N'Invented.', 'text' FROM dbo.DataSource;
            """);
        Assert.Equal(new[] { "I|probe|varchar(20)|True", "U|probe|char(10)|False", "D|probe|char(10)|False", "I|defaulted|NULL|False" },
            await scratch.ReadAsync("SELECT [audit_action], [column_name], ISNULL([store_type], N'NULL'), [store_type_override] FROM dbo.DataSourceColumnAudit WHERE [column_name] IN ('probe', 'defaulted') ORDER BY [id];"));
    }

    private static DataSourceColumnRow Column(string name, int ordinal, string? storeType, bool overridden = false) =>
        new(name, ordinal, name, "Invented.", "text", null, null, null, true, true, true, true, true)
        { StoreType = storeType, StoreTypeOverride = overridden };

    private static SourceContract Contract(string connection, params (string Column, string StoreType, bool Override)[] typed) =>
        Contract(connection, "dbo.vst", typed);

    private static SourceContract Contract(string connection, string objectName, params (string Column, string StoreType, bool Override)[] typed)
    {
        var columns = typed.Select((column, index) => Column(column.Column, index + 2, column.StoreType, column.Override))
            .Prepend(new DataSourceColumnRow("id", 1, "id", "Invented.", "number", null, null, null, true, true, true, true, true))
            .ToArray();
        var mapped = CatalogMapping.Map(new(new DataSourceRow(objectName, connection)
        {
            Id = 1, Name = "st", Title = "Store types", Description = "Invented.", Grain = "One row", IsActive = true, RowCap = 500, QueryTimeoutSeconds = 30
        }, columns));
        Assert.True(mapped.IsSuccess, mapped.Failure?.Message);
        return mapped.Value;
    }

    /// <summary>A scratch LocalDB database named dg_st_*, dropped on dispose; shared with the row-for-row proof.</summary>
    internal sealed class Scratch : IAsyncDisposable
    {
        private Scratch(string name) => Name = name;
        internal string Name { get; }
        internal string Connection => ConnectionTo(Name);

        internal static Task<Scratch> CreateAsync() => CreateAsync("dg_st_" + Guid.NewGuid().ToString("N")[..12], null);

        /// <summary>A scratch database of the given dg_st_* name, with its own default collation when one is given.</summary>
        internal static async Task<Scratch> CreateAsync(string name, string? collation)
        {
            Assert.StartsWith("dg_st_", name, StringComparison.Ordinal);
            var scratch = new Scratch(name);
            await Master($"CREATE DATABASE [{scratch.Name}]" + (collation is null ? ";" : $" COLLATE {collation};"));
            return scratch;
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using var session = new SqlConnection(Connection);
            await session.OpenAsync();
            await using var command = new SqlCommand(sql, session) { CommandTimeout = 60 };
            await command.ExecuteNonQueryAsync();
        }

        // Runs a repository script the way sqlcmd does for these files: $(CatalogDatabase) is this database, ':' lines
        // are sqlcmd commands (":On Error exit": any error throws here anyway), and each GO line ends a batch.
        internal async Task RunScriptAsync(string script)
        {
            var batch = new System.Text.StringBuilder();
            foreach (var line in script.Replace("\r\n", "\n").Replace("$(CatalogDatabase)", Name, StringComparison.Ordinal).Split('\n'))
            {
                if (line.StartsWith(':')) continue;
                if (line.Trim() == "GO")
                {
                    if (!string.IsNullOrWhiteSpace(batch.ToString())) await ExecuteAsync(batch.ToString());
                    batch.Clear();
                }
                else batch.Append(line).Append('\n');
            }
            if (!string.IsNullOrWhiteSpace(batch.ToString())) await ExecuteAsync(batch.ToString());
        }

        internal async Task<object?> ScalarAsync(string sql)
        {
            await using var session = new SqlConnection(Connection);
            await session.OpenAsync();
            await using var command = new SqlCommand(sql, session);
            return await command.ExecuteScalarAsync();
        }

        internal async Task<string[]> ReadAsync(string sql)
        {
            await using var session = new SqlConnection(Connection);
            await session.OpenAsync();
            await using var command = new SqlCommand(sql, session);
            await using var reader = await command.ExecuteReaderAsync();
            var lines = new List<string>();
            while (await reader.ReadAsync())
                lines.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i)))));
            return lines.ToArray();
        }

        public async ValueTask DisposeAsync() =>
            await Master($"IF DB_ID(N'{Name}') IS NOT NULL BEGIN ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]; END");

        private static async Task Master(string sql)
        {
            await using var session = new SqlConnection(ConnectionTo("master"));
            await session.OpenAsync();
            await using var command = new SqlCommand(sql, session) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync();
        }
    }
}
