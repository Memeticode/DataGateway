using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Data.Catalog;
using DataGateway.Data.PostgreSql;
using Npgsql;

namespace DataGateway.Web.Catalog;

public sealed class PostgreSqlCatalog : CatalogService<NpgsqlConnection>
{
    private readonly int catalogTimeoutSeconds;

    public PostgreSqlCatalog(DbContext<NpgsqlConnection> db, int catalogTimeoutSeconds = 15) : base(db)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(catalogTimeoutSeconds);
        this.catalogTimeoutSeconds = catalogTimeoutSeconds;
    }

    public override Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(CancellationToken cancellationToken) =>
        CatalogTableReader.ListAsync(Db, ListSql, catalogTimeoutSeconds, cancellationToken);

    public override async Task<CatalogDetail?> QueryCatalogDetailAsync(
        string sourceName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        // A blank name matches no source: unknown (404), as the SQL Server reader answers, not a catalog failure.
        if (string.IsNullOrWhiteSpace(sourceName)) return null;
        return CatalogTableReader.Matching(await CatalogTableReader.DetailAsync(Db, ReadSql, AvailableSql, sourceName,
            PostgreSqlEngine.ProviderName, false, catalogTimeoutSeconds, cancellationToken).ConfigureAwait(false), sourceName);
    }

    internal const string ListSql = "SELECT name, title, description, grain, row_cap\n" +
        "FROM datagateway.vdatasource\nWHERE is_active = TRUE;";
    internal const string AvailableSql = "SELECT name FROM datagateway.vdatasource WHERE is_active = TRUE;";
    internal const string ReadSql =
        "SELECT s.id, s.name, s.title, s.description, s.grain, s.row_cap, s.query_timeout_seconds, s.object_name, s.connection_string,\n" +
        "       c.column_name, c.ordinal, c.title AS column_title,\n" +
        "       c.description AS column_description, c.data_type, c.unit,\n" +
        "       c.magnitude, c.display_format, c.can_select, c.can_filter,\n" +
        "       c.can_sort, c.can_group, c.can_aggregate\n" +
        "FROM datagateway.vdatasource AS s\n" +
        "LEFT JOIN datagateway.vdatasourcecolumn AS c ON c.datasource_fk = s.id\n" +
        "WHERE lower(s.name COLLATE \"C\") = lower(@source COLLATE \"C\") AND s.is_active = TRUE\n" +
        "ORDER BY c.ordinal, c.column_name COLLATE \"C\";";
}
