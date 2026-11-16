using Xunit;

namespace DataGateway.Web.Tests;

public sealed class RepositorySourceTests
{
    [Fact]
    public void no_value_suppression_remains_in_public_sources()
    {
        var forbidden = ValueSuppressionPattern;
        foreach (var file in Directory.EnumerateFiles(DataGateway.Core.Tests.TestFiles.RepoRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
            Assert.DoesNotMatch(forbidden, File.ReadAllText(file));
    }

    private const string ValueSuppressionPattern = @"\bValue\s*" + "!(?!=)";
    private const string Bang = "!";

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

    [Fact]
    public void T14_catalog_reader_uses_only_projection_views()
    {
        var source = File.ReadAllText(DataGateway.Core.Tests.TestFiles.InRepo("DataGateway.Web/Catalog/SqlServerCatalog.cs"));
        Assert.DoesNotContain("[dbo].[DataSource]", source);
        Assert.DoesNotContain("[dbo].[DataSourceColumn]", source);
        Assert.Contains("[dbo].[vDataSource]", source);
        Assert.Contains("[dbo].[vDataSourceColumn]", source);
        var ddl = File.ReadAllText(DataGateway.Core.Tests.TestFiles.InRepo("db/catalog-views.sql"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(ddl, "CREATE OR ALTER VIEW").Count);
        foreach (var name in new[] { "id", "name", "connection_string", "object_name", "title", "description", "grain", "is_active", "row_cap",
                     "datasource_fk", "column_name", "ordinal", "data_type", "unit", "magnitude", "display_format",
                     "can_select", "can_filter", "can_sort", "can_group", "can_aggregate" })
            Assert.Contains("[" + name + "]", ddl);
        Assert.DoesNotContain("SCHEMABINDING", ddl);
        Assert.DoesNotContain("WHERE", ddl);
        Assert.DoesNotContain("CAST", ddl);
    }
}
