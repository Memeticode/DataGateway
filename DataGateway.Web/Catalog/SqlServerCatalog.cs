using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Data.Catalog;
using DataGateway.Data.SqlServer;
using Microsoft.Data.SqlClient;

namespace DataGateway.Web.Catalog;

public sealed class SqlServerCatalog : CatalogService<SqlConnection>
{
    private readonly int catalogTimeoutSeconds;

    public SqlServerCatalog(DbContext<SqlConnection> db, int catalogTimeoutSeconds = 15) : base(db)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(catalogTimeoutSeconds);
        this.catalogTimeoutSeconds = catalogTimeoutSeconds;
    }

    public override Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(CancellationToken cancellationToken) =>
        CatalogTableReader.ListAsync(Db, ListSql, catalogTimeoutSeconds, cancellationToken);

    public override async Task<CatalogDetail?> QueryCatalogDetailAsync(
        string sourceName, CancellationToken cancellationToken) =>
        CatalogTableReader.Matching(await CatalogTableReader.DetailAsync(Db, ReadSql, AvailableSql, sourceName,
            SqlServerEngine.ProviderName, true, catalogTimeoutSeconds, cancellationToken).ConfigureAwait(false), sourceName);

    internal const string ListSql =
        "SELECT [name], [title], [description], [grain], [row_cap]\r\n" +
        "FROM [dbo].[vDataSource]\r\n" +
        "WHERE [is_active] = 1;";

    internal const string AvailableSql = "SELECT [name] FROM [dbo].[vDataSource] WHERE [is_active] = 1;";

    internal const string ReadSql =
        "SELECT s.[id], s.[name], s.[title], s.[description], s.[grain], s.[row_cap], s.[query_timeout_seconds], s.[object_name], s.[connection_string],\r\n" +
        "       c.[column_name], c.[ordinal], c.[title] AS [column_title],\r\n" +
        "       c.[description] AS [column_description], c.[data_type], c.[unit],\r\n" +
        "       c.[magnitude], c.[display_format], c.[can_select], c.[can_filter],\r\n" +
        "       c.[can_sort], c.[can_group], c.[can_aggregate]\r\n" +
        "FROM [dbo].[vDataSource] AS s\r\n" +
        "LEFT JOIN [dbo].[vDataSourceColumn] AS c ON c.[datasource_fk] = s.[id]\r\n" +
        "WHERE s.[name] COLLATE Latin1_General_100_CI_AS = @source AND s.[is_active] = 1\r\n" +
        "ORDER BY c.[ordinal], c.[column_name];";
}
