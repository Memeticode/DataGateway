using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Web.Catalog;
using Npgsql;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>A source name the catalog database matches more loosely than DataGateway is an unknown source, not a fault.</summary>
public sealed class CatalogNameMatchTests
{
    /// <summary>The PostgreSQL catalog answers a blank name as unknown before opening a connection, as the SQL Server
    /// reader does.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task T1_a_blank_name_is_an_unknown_source_on_postgresql_without_a_catalog_call(string name)
    {
        var calls = 0;
        var db = DbContext<NpgsqlConnection>.FromFactory(() => { calls++; throw new InvalidOperationException("no catalog call expected"); });
        Assert.Null(await new PostgreSqlCatalog(db, 5).QueryCatalogDetailAsync(name, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    /// <summary>A returned row is kept only when its name equals the requested one ignoring case; a row the database
    /// matched through trailing spaces or accent folding is dropped.</summary>
    [Theory]
    [InlineData("Sales", "Sales", true)]
    [InlineData("sales", "Sales", true)]
    [InlineData("Sales ", "Sales", false)]
    [InlineData("Salés", "Sales", false)]
    public void T2_a_row_for_another_name_is_an_unknown_source(string requested, string stored, bool kept)
    {
        var detail = new CatalogDetail("sqlserver", new CatalogRows(new DataSourceRow("dbo.vSales", "Server=synthetic")
        { QueryTimeoutSeconds = 30,
            Name = stored, Title = "t", Description = "d", Grain = "g", IsActive = true, RowCap = 500
        }, []));
        Assert.Equal(kept, CatalogTableReader.Matching(detail, requested) is not null);
    }
}
