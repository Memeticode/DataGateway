using DataGateway.Core.Catalog;
using DataGateway.Data.SqlServer;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>The store-type fill tool: it writes reviewable UPDATE statements for the catalog's own engine and
/// never touches an overridden column.</summary>
public sealed class StoreTypeScriptTests
{
    private static readonly SqlServerColumnType[] View =
    [
        new("code", "char", 10, 0, 0, "SQL_Latin1_General_CP1_CI_AS"),
        new("name", "nvarchar", -1, 0, 0, "SQL_Latin1_General_CP1_CI_AS"),
        new("qty", "int", 4, 10, 0, null),
        new("amount", "decimal", 9, 19, 4, null),
        new("kept", "varchar", 30, 0, 0, "SQL_Latin1_General_CP1_CI_AS"),
        new("spot", "geography", -1, 0, 0, null),
    ];

    private static CatalogRows Rows() => new(new DataSourceRow("dbo.synthetic", "fixture-connection-removed")
    {
        Id = 7, Name = "synthetic", Title = "Synthetic", Description = "Synthetic", Grain = "One row",
        IsActive = true, RowCap = 500, QueryTimeoutSeconds = 30
    },
    [
        Column("code", 1, null),                       // empty, but char under a SQL_ collation: comment only
        Column("name", 2, "nvarchar(40)"),             // different: corrected
        Column("qty", 3, "INT"),                       // same, other spelling: no statement
        Column("amount", 4, "decimal(19,4)"),          // same: no statement
        Column("kept", 5, "char(30)", overridden: true), // overridden: comment only
        Column("lost", 6, "int"),                      // not in the view: comment only
        Column("spot", 7, null),                       // a type the binding rule leaves alone: still recorded
        Column("bad", 8, "x(\n-- injected"),           // unreadable, and not in the view: comment shows no catalog text
    ]);

    [Fact]
    public async Task T1_sql_server_catalog_script_updates_empty_and_different_store_types_only()
    {
        var script = await SqlServerStoreTypeScript.CreateAsync([Rows()], CatalogScriptDialect.SqlServer, (_, _) =>
            Task.FromResult<IReadOnlyList<SqlServerColumnType>>(View), default);
        Assert.Equal(
            "-- DataGateway store_type fill script for a SQL Server catalog. Review each statement, then run it on the catalog; this tool changes nothing itself.\n" +
            "\n" +
            "-- Source 'synthetic' (id 7), view dbo.synthetic:\n" +
            SqlCollationComment +
            "UPDATE [dbo].[DataSourceColumn] SET [store_type] = N'nvarchar(max)' WHERE [datasource_fk] = 7 AND [column_name] = 'name';\n" +
            "--   Column 'kept' keeps store_type char(30) (store_type_override is set); the view's type is varchar(30).\n" +
            "--   Column 'lost' (store_type int) is not in the view; left unchanged.\n" +
            "UPDATE [dbo].[DataSourceColumn] SET [store_type] = N'geography' WHERE [datasource_fk] = 7 AND [column_name] = 'spot';\n" +
            "--   Column 'bad' (store_type unreadable) is not in the view; left unchanged.\n", script);
    }

    [Fact]
    public async Task T2_postgresql_catalog_script_uses_the_postgresql_catalog_table_and_plain_literals()
    {
        var script = await SqlServerStoreTypeScript.CreateAsync([Rows()], CatalogScriptDialect.PostgreSql, (_, _) =>
            Task.FromResult<IReadOnlyList<SqlServerColumnType>>(View), default);
        var lines = script.Split('\n');
        Assert.Equal(new[]
        {
            "-- DataGateway store_type fill script for a PostgreSQL catalog. Review each statement, then run it on the catalog; this tool changes nothing itself.",
            "-- Run it in one transaction (BEGIN; ... COMMIT;): the SET LOCAL below makes every literal read the same way whatever the server's standard_conforming_strings, and ends with that transaction.",
            "SET LOCAL standard_conforming_strings = on;",
            "",
            "-- Source 'synthetic' (id 7), view dbo.synthetic:",
            SqlCollationComment[..^1],
            "UPDATE datagateway.datasourcecolumn SET store_type = 'nvarchar(max)' WHERE datasource_fk = 7 AND column_name = 'name';",
        }, lines[..7]);
        Assert.Equal(2, lines.Count(line => line.StartsWith("UPDATE", StringComparison.Ordinal)));
        // The SET LOCAL is the first statement: everything before it is comment.
        Assert.All(lines.Where(line => line.Length > 0).Skip(3), line => Assert.True(line.StartsWith("--", StringComparison.Ordinal) || line.StartsWith("UPDATE", StringComparison.Ordinal), line));
    }

    [Theory]
    [InlineData(CatalogScriptDialect.SqlServer)]
    [InlineData(CatalogScriptDialect.PostgreSql)]
    public async Task T5_a_column_name_outside_the_identifier_grammar_is_never_written_into_a_statement(CatalogScriptDialect dialect)
    {
        // A backslash before a quote is the name that would end a PostgreSQL literal early with
        // standard_conforming_strings off; the rest would run as a statement of its own.
        const string hostile = "x\\'; SELECT 1 AS injected; --";
        var rows = new CatalogRows(Rows().Source, [Column("code", 1, null), Column(hostile, 2, null), Column("qty", 3, null)]);
        SqlServerColumnType[] view = [new("code", "char", 10, 0, 0, null), new(hostile, "varchar", 20, 0, 0, null), new("qty", "int", 4, 10, 0, null)];
        var script = await SqlServerStoreTypeScript.CreateAsync([rows], dialect, (_, _) =>
            Task.FromResult<IReadOnlyList<SqlServerColumnType>>(view), default);
        Assert.DoesNotContain("injected", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", script, StringComparison.Ordinal);
        Assert.Contains("--   The column at ordinal 2 has a name that is not a bare ASCII SQL identifier; left unchanged. Correct its name in the catalog.\n", script);
        Assert.Equal(new[] { "code", "qty" }, script.Split('\n').Where(line => line.StartsWith("UPDATE", StringComparison.Ordinal))
            .Select(line => line[(line.LastIndexOf("= '", StringComparison.Ordinal) + 3)..^2]));
    }

    [Fact]
    public async Task T6_a_source_named_with_a_database_part_gets_a_comment_only()
    {
        var rows = new CatalogRows(new DataSourceRow("otherdb.dbo.synthetic", "fixture-connection-removed")
        {
            Id = 7, Name = "synthetic", Title = "Synthetic", Description = "Synthetic", Grain = "One row",
            IsActive = true, RowCap = 500, QueryTimeoutSeconds = 30
        }, [Column("code", 1, null)]);
        var script = await SqlServerStoreTypeScript.CreateAsync([rows], CatalogScriptDialect.SqlServer, (_, _) =>
            throw new UnresolvedObjectException(), default);
        Assert.Equal(
            "-- DataGateway store_type fill script for a SQL Server catalog. Review each statement, then run it on the catalog; this tool changes nothing itself.\n" +
            "\n" +
            "-- Source 'synthetic' (id 7), view otherdb.dbo.synthetic:\n" +
            "--   The view's column types could not be read (the source object is named with a database or server part, or is a synonym for an object on another server, in another database, or no longer there); nothing is written for this source.\n",
            script);
    }

    [Fact]
    public async Task T3_a_source_whose_view_cannot_be_read_gets_a_comment_and_the_others_still_get_statements()
    {
        var other = new CatalogRows(new DataSourceRow("dbo.other", "fixture-connection-removed")
        {
            Id = 8, Name = "other's\nname", Title = "Other", Description = "Other", Grain = "One row",
            IsActive = true, RowCap = 500, QueryTimeoutSeconds = 30
        }, [Column("code", 1, null)]);
        var empty = new CatalogRows(new DataSourceRow("dbo.hidden", "fixture-connection-removed")
        {
            Id = 9, Name = "hidden", Title = "Hidden", Description = "Hidden", Grain = "One row",
            IsActive = true, RowCap = 500, QueryTimeoutSeconds = 30
        }, [Column("code", 1, null)]);
        var script = await SqlServerStoreTypeScript.CreateAsync([other, empty, Rows()], CatalogScriptDialect.SqlServer, (source, _) =>
            source.Id switch
            {
                8 => throw new TimeoutException("synthetic"),
                9 => Task.FromResult<IReadOnlyList<SqlServerColumnType>>([]),
                _ => Task.FromResult<IReadOnlyList<SqlServerColumnType>>(View)
            }, default);
        Assert.Contains("-- Source 'other's?name' (id 8), view dbo.other:\n--   The view's column types could not be read (it took longer than the metadata read's time limit); nothing is written for this source.\n", script);
        Assert.Contains("-- Source 'hidden' (id 9), view dbo.hidden:\n--   The view was not found, or its definition is not visible to the login; nothing is written for this source.\n", script);
        Assert.Equal(2, script.Split('\n').Count(line => line.StartsWith("UPDATE", StringComparison.Ordinal)));
    }

    private const string SqlCollationComment =
        "--   Column 'code' (store_type empty) is char(10) under the SQL collation SQL_Latin1_General_CP1_CI_AS, where a " +
        "store_type changes how filter values compare (STORE-TYPES.md, \"The visible differences\"); left unchanged. Its " +
        "owner may set store_type to char(10) by hand.\n";

    // On a case-sensitive database a view can hold stamp and STAMP as two columns: the catalog's exact spelling picks one,
    // and a name matching both only when case is ignored gets a comment, never the type of whichever came first.
    [Fact]
    public async Task T7_a_column_matching_two_case_variants_takes_the_exact_one_or_gets_a_comment()
    {
        var rows = new CatalogRows(Rows().Source, [Column("STAMP", 1, null), Column("Stamp", 2, null), Column("qty", 3, null)]);
        SqlServerColumnType[] view = [new("stamp", "date", 3, 10, 0, null), new("STAMP", "time", 5, 16, 7, null), new("qty", "int", 4, 10, 0, null)];
        var script = await SqlServerStoreTypeScript.CreateAsync([rows], CatalogScriptDialect.SqlServer, (_, _) =>
            Task.FromResult<IReadOnlyList<SqlServerColumnType>>(view), default);
        Assert.Equal(
            "-- DataGateway store_type fill script for a SQL Server catalog. Review each statement, then run it on the catalog; this tool changes nothing itself.\n" +
            "\n" +
            "-- Source 'synthetic' (id 7), view dbo.synthetic:\n" +
            "UPDATE [dbo].[DataSourceColumn] SET [store_type] = N'time(7)' WHERE [datasource_fk] = 7 AND [column_name] = 'STAMP';\n" +
            "--   Column 'Stamp' (store_type empty) matches more than one view column when letter case is ignored, and none exactly; left unchanged. Spell its name in the catalog exactly as the view does.\n" +
            "UPDATE [dbo].[DataSourceColumn] SET [store_type] = N'int' WHERE [datasource_fk] = 7 AND [column_name] = 'qty';\n", script);
    }

    // Under a SQL_ collation a varchar or char store_type changes which rows a filter returns, so the tool never proposes
    // one there; a Windows collation, and nvarchar under any collation, compare the same either way and are filled.
    [Fact]
    public async Task T8_a_varchar_or_char_column_under_a_sql_collation_gets_a_comment_and_no_update()
    {
        var rows = new CatalogRows(Rows().Source,
        [
            Column("v", 1, null), Column("c", 2, "char(4)"), Column("w", 3, null), Column("n", 4, null), Column("same", 5, "varchar(9)")
        ]);
        SqlServerColumnType[] view =
        [
            new("v", "varchar", 20, 0, 0, "SQL_Latin1_General_CP1_CI_AS"), new("c", "char", 10, 0, 0, "SQL_Latin1_General_CP1_CS_AS"),
            new("w", "varchar", 20, 0, 0, "Latin1_General_CI_AS"), new("n", "nvarchar", 40, 0, 0, "SQL_Latin1_General_CP1_CI_AS"),
            new("same", "varchar", 9, 0, 0, "SQL_Latin1_General_CP1_CI_AS"),
        ];
        foreach (var dialect in new[] { CatalogScriptDialect.SqlServer, CatalogScriptDialect.PostgreSql })
        {
            var script = await SqlServerStoreTypeScript.CreateAsync([rows], dialect, (_, _) =>
                Task.FromResult<IReadOnlyList<SqlServerColumnType>>(view), default);
            var lines = script.Split('\n');
            Assert.Equal(new[] { "w", "n" }, lines.Where(line => line.StartsWith("UPDATE", StringComparison.Ordinal))
                .Select(line => line[(line.LastIndexOf("= '", StringComparison.Ordinal) + 3)..^2]));
            Assert.Contains("--   Column 'v' (store_type empty) is varchar(20) under the SQL collation SQL_Latin1_General_CP1_CI_AS, where a " +
                "store_type changes how filter values compare (STORE-TYPES.md, \"The visible differences\"); left unchanged. Its " +
                "owner may set store_type to varchar(20) by hand.", lines);
            Assert.Contains("--   Column 'c' (store_type char(4)) is char(10) under the SQL collation SQL_Latin1_General_CP1_CS_AS, where a " +
                "store_type changes how filter values compare (STORE-TYPES.md, \"The visible differences\"); left unchanged. Its " +
                "owner may set store_type to char(10) by hand.", lines);
            // A store_type already set by hand that agrees with the view needs nothing.
            Assert.DoesNotContain(lines, line => line.Contains("'same'", StringComparison.Ordinal));
        }
    }

    // A script with no UPDATE statements has nothing to apply, which is not the same as a catalog that matches its views:
    // every column it does not fill is named in a comment.
    [Fact]
    public async Task T9_a_script_without_updates_still_names_every_column_it_could_not_vouch_for()
    {
        var rows = new CatalogRows(Rows().Source,
        [
            Column("kept", 1, "char(30)", overridden: true), Column("lost", 2, "int"), Column("Stamp", 3, null),
            Column("v", 4, null), Column("spot", 5, null)
        ]);
        SqlServerColumnType[] view =
        [
            new("kept", "varchar", 30, 0, 0, "Latin1_General_CI_AS"), new("stamp", "date", 3, 10, 0, null), new("STAMP", "time", 5, 16, 7, null),
            new("v", "varchar", 20, 0, 0, "SQL_Latin1_General_CP1_CI_AS"), new("spot", "json", -1, 0, 0, null),
        ];
        var script = await SqlServerStoreTypeScript.CreateAsync([rows, new(new DataSourceRow("dbo.down", "fixture-connection-removed")
        {
            Id = 8, Name = "down", Title = "Down", Description = "Down", Grain = "One row", IsActive = true, RowCap = 500, QueryTimeoutSeconds = 30
        }, [Column("code", 1, null)])], CatalogScriptDialect.SqlServer, (source, _) => source.Id == 8
            ? throw new TimeoutException("synthetic")
            : Task.FromResult<IReadOnlyList<SqlServerColumnType>>(view), default);
        var lines = script.Split('\n');
        Assert.DoesNotContain(lines, line => line.StartsWith("UPDATE", StringComparison.Ordinal));
        foreach (var named in new[] { "Column 'kept'", "Column 'lost'", "Column 'Stamp'", "Column 'v'", "Column 'spot'", "could not be read" })
            Assert.Single(lines, line => line.StartsWith("--   ", StringComparison.Ordinal) && line.Contains(named, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("a\\b", "'a\\b'")]
    [InlineData("''", "''''''")]
    public void T4_literals_double_their_quotes(string text, string literal) =>
        Assert.Equal(literal, SqlServerStoreTypeScript.Literal(text));

    [Fact]
    public async Task T4_comments_hold_only_printable_ascii()
    {
        Assert.Equal("-- a?b?c?d", SqlServerStoreTypeScript.Comment("a\nb\rc\u00e9d"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SqlServerStoreTypeScript.CreateAsync([], (CatalogScriptDialect)7, 15, default));
        // A bad timeout is refused before any read starts.
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = SqlServerStoreTypeScript.CreateAsync([], CatalogScriptDialect.SqlServer, 0, default); });
    }

    // The published docs say what the code above does: what an empty script means, that padding is not a store-type
    // feature, the log levels, and what an engine wrapping the SQL Server engine must do to keep typed binding.
    [Fact]
    public void T10_the_docs_match_the_fill_tool_padding_log_levels_and_engine_wrapping()
    {
        static string Read(string path) => File.ReadAllText(DataGateway.Core.Tests.TestFiles.InRepo(path)).Replace("\r\n", "\n").Replace('\n', ' ');
        var storeTypes = Read("STORE-TYPES.md");
        var engines = Read("ENGINES.md");
        var configuration = Read("CONFIGURATION.md");
        Assert.DoesNotContain("means the catalog matches the views", storeTypes, StringComparison.Ordinal);
        Assert.Contains("A script with no UPDATE statements has nothing to apply; it does not say the catalog matches the views.", storeTypes, StringComparison.Ordinal);
        Assert.Contains("columns with `store_type_override` set, columns the view lacks, columns that match two view columns in different letter case, `varchar` and `char` columns under a SQL_ collation, columns whose view type DataGateway does not bind by, columns whose name is not a bare ASCII SQL identifier (named by ordinal only), and sources whose view could not be read", storeTypes, StringComparison.Ordinal);
        foreach (var text in new[] { storeTypes, engines })
            Assert.Contains("IN and NOT IN lists are padded on every SQL Server source, with or without store types", text, StringComparison.Ordinal);
        Assert.Contains("An engine that wraps `SqlServerEngine` must also implement `IStoreTypeEngine`", engines, StringComparison.Ordinal);
        Assert.Contains("Warning when an override keeps the catalog's type or a `store_type` cannot be read", configuration, StringComparison.Ordinal);
        Assert.Contains("at least 30 seconds after the failed read began", configuration, StringComparison.Ordinal);
        Assert.Contains("at least 30 seconds after the failed read began", storeTypes, StringComparison.Ordinal);
    }

    private static DataSourceColumnRow Column(string name, int ordinal, string? storeType, bool overridden = false) =>
        new(name, ordinal, name, "Invented.", "text", null, null, null, true, true, true, true, true)
        { StoreType = storeType, StoreTypeOverride = overridden };
}
