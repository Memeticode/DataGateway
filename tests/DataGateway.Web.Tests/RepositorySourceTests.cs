using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>Pins rules about the repository's source text itself: no null-forgiving read of a <c>Value</c> member
/// anywhere, and a catalog reader that reads only the catalog's projection views.</summary>
public sealed class RepositorySourceTests
{
    /// <summary>No C# file applies the null-forgiving operator to a member named <c>Value</c>.</summary>
    /// <remarks>A result's <c>Value</c> throws on a failed result instead of returning null, so a <c>!</c> after it is
    /// never needed.</remarks>
    [Fact]
    public void no_value_suppression_remains_in_public_sources()
    {
        var forbidden = ValueSuppressionPattern;
        foreach (var file in Directory.EnumerateFiles(DataGateway.Core.Tests.TestFiles.RepoRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
            Assert.DoesNotMatch(forbidden, File.ReadAllText(file));
    }

    /// <summary>Matches <c>Value</c> as a whole word followed by <c>!</c>, across whitespace, but not <c>!=</c>.</summary>
    /// <remarks>Built from pieces so this file does not match its own scan.</remarks>
    private const string ValueSuppressionPattern = @"\bValue\s*" + "!(?!=)";
    /// <summary>The <c>!</c> character, kept apart so the test data below does not match the repository scan.</summary>
    private const string Bang = "!";

    /// <summary>The scan pattern catches the suppression across spaces and line breaks, and ignores inequality, other
    /// words that contain <c>Value</c>, and a lower-case <c>value</c>.</summary>
    [Theory]
    [InlineData("result.Value" + Bang, true)]
    [InlineData("Value" + Bang + ".Name", true)]
    [InlineData("result.Value " + Bang, true)]
    [InlineData("result.Value\n" + Bang + ".Name", true)]
    [InlineData("a.Value != b", false)]
    [InlineData("a.Value" + Bang + "= b", false)]
    [InlineData("a.Value\n!= b", false)]
    [InlineData("!result.Value", false)]
    [InlineData("a.HasValue", false)]
    [InlineData("JsonValueKind.Null", false)]
    [InlineData("Values!", false)]
    [InlineData("result.value!", false)]
    public void T5_value_suppression_scan_has_word_whitespace_and_inequality_boundaries(string text, bool matches) =>
        Assert.Equal(matches, System.Text.RegularExpressions.Regex.IsMatch(text, ValueSuppressionPattern));

    /// <summary>The SQL Server catalog reader reads the <c>vDataSource</c> and <c>vDataSourceColumn</c> views, never the
    /// tables, and the view script defines plain column projections with no filter, cast or schema binding.</summary>
    /// <remarks>The views are the catalog's read contract: the tables can change behind them while the reader keeps the
    /// column names it expects.</remarks>
    [Fact]
    public void T14_catalog_reader_uses_only_projection_views()
    {
        var source = File.ReadAllText(DataGateway.Core.Tests.TestFiles.InRepo("DataGateway.Web/Catalog/SqlServerCatalog.cs"));
        Assert.DoesNotContain("[dbo].[DataSource]", source);
        Assert.DoesNotContain("[dbo].[DataSourceColumn]", source);
        Assert.Contains("[dbo].[vDataSource]", source);
        Assert.Contains("[dbo].[vDataSourceColumn]", source);
        var ddl = File.ReadAllText(DataGateway.Core.Tests.TestFiles.InRepo("db/catalog-views.sql"));
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(ddl, "CREATE OR ALTER VIEW").Count);
        foreach (var name in new[] { "id", "name", "connection_string", "object_name", "title", "description", "grain", "is_active", "row_cap",
                     "datasource_fk", "column_name", "ordinal", "data_type", "unit", "magnitude", "display_format",
                     "can_select", "can_filter", "can_sort", "can_group", "can_aggregate" })
            Assert.Contains("[" + name + "]", ddl);
        Assert.DoesNotContain("SCHEMABINDING", ddl);
        Assert.DoesNotContain("WHERE", ddl);
        Assert.DoesNotContain("CAST", ddl);
    }
}
