using Xunit;

namespace DataGateway.Core.Tests;

public sealed class RowCapCatalogTests
{
    [Fact]
    public void T1_ddl_view_and_migration_declare_the_cap_contract()
    {
        var ddl = File.ReadAllText(TestFiles.InRepo("db/catalog-ddl.sql")).Replace("\r\n", "\n");
        var migration = File.ReadAllText(TestFiles.InRepo("db/catalog-row-cap.sql"));
        var view = File.ReadAllText(TestFiles.InRepo("db/catalog-views.sql"));
        Assert.Contains("[is_active] [bit] NOT NULL,\n\t[row_cap] [int] NOT NULL,", ddl);
        foreach (var script in new[] { ddl, migration })
        {
            Assert.Contains("[row_cap] [int] NOT NULL", script);
            Assert.Contains("[DF_DataSource_row_cap] DEFAULT ((500))", script);
            Assert.Contains("[CK_DataSource_row_cap]", script);
            Assert.Contains("[row_cap] >= 1 AND [row_cap] <= 2147483646", script);
            Assert.Contains("[row_cap] [int] NULL", script);
            var imageEnd = ReferenceEquals(script, ddl) ? ", [row_cap]\n\t\t, [query_timeout_seconds]\n\t)" : ", [row_cap]\n\t)";
            Assert.Equal(2, script.Split(imageEnd).Length - 1);
            Assert.Contains(ReferenceEquals(script, ddl) ? ", i.[row_cap]\n\t\t, i.[query_timeout_seconds]\n\tFROM inserted AS i" : ", i.[row_cap]\n\tFROM inserted AS i", script);
            Assert.Contains(ReferenceEquals(script, ddl) ? ", d.[row_cap]\n\t\t, d.[query_timeout_seconds]\n\tFROM deleted AS d" : ", d.[row_cap]\n\tFROM deleted AS d", script);
        }
        foreach (var script in new[] { view, migration })
            Assert.Contains("[grain], [is_active], [row_cap]", script);
        foreach (var guard in new[] { "SET XACT_ABORT ON", "BEGIN TRY", "BEGIN TRAN", "COMMIT TRAN", "BEGIN CATCH", "IF XACT_STATE() <> 0", "ROLLBACK TRAN", "THROW", "COL_LENGTH(N'dbo.DataSource', N'row_cap') IS NULL", "COL_LENGTH(N'dbo.DataSourceAudit', N'row_cap') IS NULL", "IF NOT EXISTS", "sys.check_constraints", "WITH VALUES", "EXEC(N'CREATE OR ALTER TRIGGER", "EXEC(N'CREATE OR ALTER VIEW" })
            Assert.Contains(guard, migration);
    }
}
