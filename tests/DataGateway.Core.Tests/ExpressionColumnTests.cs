using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataGateway.Core.Catalog;
using Xunit;

namespace DataGateway.Core.Tests;

/// <summary>The reserved catalog column <c>expression</c>: stored and read, refused when filled in.</summary>
public sealed class ExpressionColumnTests
{
    internal const string Marker = "reserved-expression-marker-5c1e";
    private const string Refusal = "Column 'measure' has an expression, which this version does not support. " +
        "Remove the expression from the catalog and compute the column in the source's view.";

    public static IEnumerable<object[]> FilledIn()
    {
        foreach (var expression in new[] { Marker, "", " ", "\t\r\n" })
        foreach (var flags in new[] { 31, 0 })
            yield return [expression, flags];
    }

    [Theory]
    [MemberData(nameof(FilledIn))]
    public void T1_non_null_empty_or_whitespace_expression_is_refused_whatever_the_flags(string expression, int flags)
    {
        // The companion keeps the source usable, so only the expression can cause the refusal.
        var companion = Column("companion", 31) with { Ordinal = 0 };
        var result = CatalogMapping.Map(new(Source(), [companion, Column("measure", flags) with { Expression = expression }]));
        Refused(result);
    }

    [Fact]
    public void T1_a_lone_all_off_column_with_an_expression_gets_the_expression_refusal_not_the_flag_one()
    {
        var result = CatalogMapping.Map(new(Source(), [Column("measure", 0) with { Expression = Marker }]));
        Refused(result);
    }

    [Fact]
    public void T1_expression_on_a_later_column_is_refused_too()
    {
        var result = CatalogMapping.Map(new(Source(), [Column("first", 31) with { Ordinal = 1 },
            Column("measure", 31) with { Ordinal = 2, Expression = Marker }]));
        Refused(result);
    }

    [Fact]
    public void T2_null_expression_maps_exactly_as_a_row_built_without_it()
    {
        var plain = new DataSourceColumnRow("measure", 1, "Measure", "Meaning", "number", "units", "millions", "N2",
            true, false, true, false, true);
        var explicitNull = plain with { Expression = null };
        Assert.Equal(plain, explicitNull);
        var first = CatalogMapping.Map(new(Source(), [plain]));
        var second = CatalogMapping.Map(new(Source(), [explicitNull]));
        Assert.True(first.IsSuccess, first.Failure?.Message);
        Assert.True(second.IsSuccess, second.Failure?.Message);
        var expected = new ColumnMetadata("measure", "Measure", "Meaning", "number", "units", "millions", "N2",
            true, false, true, false, true);
        Assert.Equal(expected, Assert.Single(first.Value.Columns));
        Assert.Equal(expected, Assert.Single(second.Value.Columns));
        Assert.Equal(JsonSerializer.Serialize(first.Value.Columns), JsonSerializer.Serialize(second.Value.Columns));
    }

    [Fact]
    public void T3_row_json_and_ToString_never_carry_the_expression()
    {
        var row = Column("measure", 31) with { Expression = Marker };
        var rows = new CatalogRows(Source(), [row]);
        foreach (var json in new[] { JsonSerializer.Serialize(row), JsonSerializer.Serialize(row, JsonSerializerOptions.Web),
                     JsonSerializer.Serialize(rows), JsonSerializer.Serialize(rows, JsonSerializerOptions.Web) })
        {
            Assert.DoesNotContain(Marker, json, StringComparison.Ordinal);
            Assert.DoesNotContain("xpression", json, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(Marker, row.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Expression", row.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void T3_ToString_is_unchanged_from_the_compiler_generated_thirteen_member_form()
    {
        var row = new DataSourceColumnRow("id", 1, "Id", "Item", "number", null, "none", "N0", true, false, true, false, true)
            { Expression = Marker };
        Assert.Equal("DataSourceColumnRow { ColumnName = id, Ordinal = 1, Title = Id, Description = Item, DataType = number, " +
            "Unit = , Magnitude = none, DisplayFormat = N0, CanSelect = True, CanFilter = False, CanSort = True, " +
            "CanGroup = False, CanAggregate = True }", row.ToString());
        var shape = new ThirteenMembers("id", 1, "Id", "Item", "number", null, "none", "N0", true, false, true, false, true);
        Assert.Equal(shape.ToString().Replace(nameof(ThirteenMembers), nameof(DataSourceColumnRow), StringComparison.Ordinal), row.ToString());
    }

    [Fact]
    public void T4_the_thirteen_argument_constructor_and_Deconstruct_are_unchanged()
    {
        var type = typeof(DataSourceColumnRow);
        Type[] expected = [typeof(string), typeof(int), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool)];
        var constructor = Assert.Single(type.GetConstructors());
        Assert.Equal(expected, constructor.GetParameters().Select(p => p.ParameterType));
        var deconstruct = Assert.Single(type.GetMethods(), m => m.Name == "Deconstruct");
        Assert.Equal(expected, deconstruct.GetParameters().Select(p => p.ParameterType.GetElementType()));
        Assert.All(deconstruct.GetParameters(), p => Assert.True(p.IsOut));
        var property = type.GetProperty(nameof(DataSourceColumnRow.Expression))!;
        Assert.Equal(typeof(string), property.PropertyType);
        Assert.NotNull(property.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>());
        Assert.Null(new DataSourceColumnRow("id", 1, "Id", "Item", "number", null, null, null, true, true, true, true, true).Expression);
    }

    [Fact]
    public void T5_sql_server_ddl_views_and_migration_reserve_a_nullable_expression_appended_last()
    {
        var ddl = Text("db/catalog-ddl.sql");
        var views = Text("db/catalog-views.sql");
        var migration = Text("db/catalog-expression.sql");
        // Both tables: the column table and its audit table.
        Assert.Equal(2, Regex.Matches(ddl, Regex.Escape("\t[can_aggregate] [bit] NOT NULL,\n\t[expression] [nvarchar](max) NULL,\n")).Count);
        Assert.Equal(2, Regex.Matches(ddl, Regex.Escape("\t\t, [can_aggregate]\n\t\t, [expression]\n\t\t, [store_type]\n\t\t, [store_type_override]\n\t)")).Count);
        Assert.Contains("\t\t, i.[can_aggregate]\n\t\t, i.[expression]\n\t\t, i.[store_type]\n\t\t, i.[store_type_override]\n\tFROM inserted AS i", ddl);
        Assert.Contains("\t\t, d.[can_aggregate]\n\t\t, d.[expression]\n\t\t, d.[store_type]\n\t\t, d.[store_type_override]\n\tFROM deleted AS d", ddl);
        const string view = "[display_format], [can_select], [can_filter], [can_sort], [can_group], [can_aggregate], [expression], [store_type],\n       [store_type_override]\nFROM [dbo].[DataSourceColumn];";
        Assert.Contains(view, views);
        Assert.Contains(view, migration);

        foreach (var guard in new[] { ":On Error exit", "USE [$(CatalogDatabase)]", "SET XACT_ABORT ON;", "SET LOCK_TIMEOUT 10000;",
                     "BEGIN TRY", "BEGIN TRAN;", "COMMIT TRAN;", "BEGIN CATCH", "IF XACT_STATE() <> 0", "ROLLBACK TRAN;", "THROW;",
                     "IF COL_LENGTH(N'dbo.DataSourceColumn', N'expression') IS NULL\n        ALTER TABLE [dbo].[DataSourceColumn] ADD [expression] [nvarchar](max) NULL;",
                     "IF COL_LENGTH(N'dbo.DataSourceColumnAudit', N'expression') IS NULL\n        ALTER TABLE [dbo].[DataSourceColumnAudit] ADD [expression] [nvarchar](max) NULL;",
                     "EXEC(N'CREATE OR ALTER TRIGGER [dbo].[trDataSourceColumn_Audit]", "EXEC(N'CREATE OR ALTER VIEW [dbo].[vDataSourceColumn]" })
            Assert.Contains(guard, migration);
        // The lock wait is bounded before the transaction starts.
        Assert.True(migration.IndexOf("SET LOCK_TIMEOUT", StringComparison.Ordinal) < migration.IndexOf("BEGIN TRAN;", StringComparison.Ordinal));
        // Grants survive: the migration alters in place and never drops or recreates an object.
        Assert.DoesNotMatch(@"(?im)^\s*(DROP|GRANT|REVOKE|DENY)\b|EXEC\(N'\s*(DROP|GRANT|REVOKE|DENY)\b", migration);

        // The migration's trigger is the DDL's trigger, so a fresh catalog and a migrated one audit the same cells.
        var start = ddl.IndexOf("CREATE TRIGGER [dbo].[trDataSourceColumn_Audit]", StringComparison.Ordinal);
        var trigger = ddl[start..(ddl.IndexOf("END\nGO", start, StringComparison.Ordinal) + 3)];
        Assert.Contains("EXEC(N'" + trigger.Replace("CREATE TRIGGER", "CREATE OR ALTER TRIGGER").Replace("'", "''") + "');", migration);
    }

    [Fact]
    public void T5_postgresql_ddl_views_and_migration_reserve_a_nullable_expression_appended_last()
    {
        var ddl = Text("db/postgresql/catalog-ddl.sql");
        var views = Text("db/postgresql/catalog-views.sql");
        var migration = Text("db/postgresql/catalog-expression.sql");
        Assert.Contains("    can_aggregate boolean NOT NULL DEFAULT false,\n    expression text,\n    store_type varchar(128),\n    store_type_override boolean NOT NULL DEFAULT false\n);", ddl);
        Assert.Contains("    can_aggregate boolean NOT NULL,\n    expression text,\n    store_type varchar(128),\n    store_type_override boolean NOT NULL DEFAULT false\n);", ddl);
        Assert.Contains("can_group, can_aggregate, expression, store_type, store_type_override)\n    VALUES", ddl);
        Assert.Contains("image.can_group, image.can_aggregate, image.expression, image.store_type, image.store_type_override);", ddl);
        // Appended last, so CREATE OR REPLACE VIEW keeps the existing columns in place.
        Assert.Contains("display_format, can_select, can_filter, can_sort, can_group, can_aggregate, expression, store_type,\n       store_type_override\nFROM datagateway.datasourcecolumn;", views);

        // The migration copies the function from catalog-ddl.sql and the view from catalog-views.sql, so a fresh catalog
        // and a migrated one audit and project the same cells.
        var function = Slice(ddl, "CREATE OR REPLACE FUNCTION datagateway.audit_datasourcecolumn()", "$audit$;");
        var view = Slice(views, "CREATE OR REPLACE VIEW datagateway.vdatasourcecolumn", "FROM datagateway.datasourcecolumn;");
        string[] steps = ["\\set ON_ERROR_STOP on", "BEGIN;", "SET LOCAL lock_timeout = '10s';",
            "ALTER TABLE datagateway.datasourcecolumnaudit ADD COLUMN IF NOT EXISTS expression text;",
            "ALTER TABLE datagateway.datasourcecolumn ADD COLUMN IF NOT EXISTS expression text;",
            function, view, "COMMIT;"];
        var at = -1;
        foreach (var step in steps)
        {
            var next = migration.IndexOf(step, StringComparison.Ordinal);
            Assert.True(next > at, "Out of order or missing: " + step);
            at = next;
        }
        Assert.Single(Regex.Matches(migration, @"(?m)^BEGIN;"));
        Assert.Single(Regex.Matches(migration, @"(?m)^COMMIT;"));
        // Only those two objects are replaced, and grants are preserved: nothing revokes, grants or drops, and nothing
        // pulls in catalog-ddl.sql, whose REVOKE ... FROM PUBLIC lines would strip existing PUBLIC grants.
        Assert.Equal(2, Regex.Matches(migration, @"(?m)^CREATE OR REPLACE ").Count);
        Assert.DoesNotMatch(@"(?i)\bREVOKE\b", migration);
        Assert.DoesNotMatch(@"(?im)^\s*(DROP|GRANT)\b", migration);
        Assert.DoesNotContain("\\ir catalog-ddl.sql", migration, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\\i", migration);

        // The older migration refreshes the same trigger and views, so it installs the cells first.
        var timeout = Text("db/postgresql/catalog-query-timeout.sql");
        foreach (var cell in steps[3..5])
            Assert.True(timeout.IndexOf(cell, StringComparison.Ordinal) is var i && i >= 0 &&
                i < timeout.IndexOf("\\ir catalog-ddl.sql", StringComparison.Ordinal), "catalog-query-timeout.sql: " + cell);
    }

    private static void Refused(CatalogResult<SourceContract> result)
    {
        Assert.False(result.IsSuccess);
        Assert.Throws<InvalidOperationException>(() => result.Value);
        var failure = result.Failure!;
        Assert.Equal(new CatalogFailure("InvalidCatalog", "$.source", Refusal), failure);
        Assert.DoesNotContain(Marker, failure.ToString(), StringComparison.Ordinal);
    }

    private static string Slice(string text, string from, string through)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, "Missing: " + from);
        return text[start..(text.IndexOf(through, start, StringComparison.Ordinal) + through.Length)];
    }

    private static string Text(string path) => File.ReadAllText(TestFiles.InRepo(path)).Replace("\r\n", "\n");

    private static DataSourceColumnRow Column(string name, int flags) => new(name, 1, "Measure", "Meaning", "number",
        null, null, null, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0, (flags & 16) != 0);

    private static DataSourceRow Source() => new("dbo.synthetic_items", "synthetic-connection")
    {
        Name = "synthetic_items", Title = "Items", Description = "Invented items.", Grain = "One item.",
        IsActive = true, RowCap = 500, QueryTimeoutSeconds = 30
    };

    // The compiler's own record ToString for the same thirteen members: the independent expected value.
    private sealed record ThirteenMembers(
        string ColumnName, int Ordinal, string Title, string Description, string DataType,
        string? Unit, string? Magnitude, string? DisplayFormat,
        bool CanSelect, bool CanFilter, bool CanSort, bool CanGroup, bool CanAggregate);
}
