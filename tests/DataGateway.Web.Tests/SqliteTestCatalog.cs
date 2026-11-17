using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Data.Catalog;
using DataGateway.Web.Catalog;
using Microsoft.Data.Sqlite;

namespace DataGateway.Web.Tests;

internal sealed class SqliteTestCatalog : CatalogService<SqliteConnection>
{
    private readonly int timeout;
    private readonly string provider;

    internal SqliteTestCatalog(DbContext<SqliteConnection> db, int timeout, string provider) : base(db)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeout);
        this.timeout = timeout;
        this.provider = provider;
    }

    public override async Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Db.BatchAsync(async session =>
        {
            var rows = await Read(session, ListSql, null, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return CatalogTableReader.MapList(rows);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public override async Task<CatalogDetail?> QueryCatalogDetailAsync(string sourceName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Db.BatchAsync<CatalogDetail?>(async session =>
        {
            var rows = await Read(session, ReadSql, new { source = sourceName }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count > 0)
                return CatalogTableReader.MapDetail(rows, provider, true);
            var available = await Read(session, AvailableSql, null, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            CatalogTableReader.ValidateAvailable(available);
            return null;
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<IReadOnlyList<dynamic>> Read(DbSession<SqliteConnection> session, string sql, object? parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var rows = new List<dynamic>();
        await foreach (var row in session.QueryUnbufferedAsync(sql, parameters, commandTimeout: timeout, cancellationToken: token))
        {
            token.ThrowIfCancellationRequested();
            rows.Add(row);
        }
        token.ThrowIfCancellationRequested();
        return rows;
    }

    internal const string ListSql =
        "SELECT \"name\", \"title\", \"description\", \"grain\", \"row_cap\"\n" +
        "FROM \"gateway_source\"\n" +
        "WHERE \"is_active\" = 1;";

    internal const string AvailableSql =
        "SELECT \"name\" FROM \"gateway_source\" WHERE \"is_active\" = 1;";

    internal const string ReadSql =
        "SELECT s.\"id\", s.\"name\", s.\"title\", s.\"description\", s.\"grain\", s.\"row_cap\", s.\"query_timeout_seconds\", s.\"object_name\", s.\"connection_string\",\n" +
        "       c.\"column_name\", c.\"ordinal\", c.\"title\" AS \"column_title\",\n" +
        "       c.\"description\" AS \"column_description\", c.\"data_type\", c.\"unit\",\n" +
        "       c.\"magnitude\", c.\"display_format\", c.\"can_select\", c.\"can_filter\",\n" +
        "       c.\"can_sort\", c.\"can_group\", c.\"can_aggregate\"\n" +
        "FROM \"gateway_source\" AS s\n" +
        "LEFT JOIN \"gateway_column\" AS c ON c.\"datasource_fk\" = s.\"id\"\n" +
        "WHERE s.\"name\" COLLATE NOCASE = @source AND s.\"is_active\" = 1\n" +
        "ORDER BY c.\"ordinal\", c.\"column_name\";";
}
