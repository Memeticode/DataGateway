using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Data.Catalog;
using DataGateway.Web.Catalog;
using Microsoft.Data.Sqlite;

namespace DataGateway.Web.Tests;

/// <summary>An app-owned catalog reader over <see cref="SqliteFixture"/>'s tables, written only against the public
/// <see cref="CatalogService{TConnection}"/> API.</summary>
/// <remarks>It shows that an application can bring its own catalog for another database without any product
/// change: the gateway sees only list rows and details, never the SQL. Its SQL texts are pinned by length and hash
/// in <see cref="SqliteOperationsTests"/>, so they cannot drift unnoticed.</remarks>
internal sealed class SqliteTestCatalog : CatalogService<SqliteConnection>
{
    /// <summary>Returns the shared test MCP metadata; the fixture holds no metadata table.</summary>
    public override Task<global::DataGateway.Core.Catalog.McpMetadata> QueryMcpMetadataAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(global::DataGateway.Web.Tests.McpMetadataFixture.Seed);
    }

    /// <summary>The command timeout of each catalog read, in seconds.</summary>
    private readonly int timeout;
    /// <summary>The engine name each detail carries.</summary>
    private readonly string provider;

    /// <summary>Creates the reader over <paramref name="db"/>.</summary>
    /// <param name="db">The connection context the application owns.</param>
    /// <param name="timeout">The command timeout of each read, in seconds; must be positive.</param>
    /// <param name="provider">The engine name to put on each detail.</param>
    internal SqliteTestCatalog(DbContext<SqliteConnection> db, int timeout, string provider) : base(db)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeout);
        this.timeout = timeout;
        this.provider = provider;
    }

    /// <summary>Returns the active sources, mapped by the host's own table reader.</summary>
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

    /// <summary>Returns one active source's detail, matched without regard to case, or null when there is none.</summary>
    /// <remarks>On a miss it also reads the active names in the same session, so a malformed name row is reported as
    /// a catalog fault rather than hidden behind an unknown source. Flags are read with integral values allowed,
    /// because SQLite stores booleans as integers.</remarks>
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

    /// <summary>Runs one catalog query and buffers its rows, checking <paramref name="token"/> around every row.</summary>
    /// <remarks>Microsoft.Data.Sqlite runs its async calls synchronously and its <c>Cancel</c> does nothing, so
    /// checking the token between rows is the only way a cancelled read stops early. The unbuffered reader also closes
    /// the reader and then the command, and reads one result set, which the tests' probes check.</remarks>
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

    /// <summary>The list query: every active source's listing fields.</summary>
    internal const string ListSql =
        "SELECT \"name\", \"title\", \"description\", \"grain\", \"row_cap\"\n" +
        "FROM \"gateway_source\"\n" +
        "WHERE \"is_active\" = 1;";

    /// <summary>The names of the active sources, read after a detail miss.</summary>
    internal const string AvailableSql =
        "SELECT \"name\" FROM \"gateway_source\" WHERE \"is_active\" = 1;";

    /// <summary>The detail query: one active source joined to its columns, one row per column.</summary>
    /// <remarks>The left join keeps a source with no columns as a single row with null column cells, which the
    /// mapper turns into a source with no columns instead of an unknown source.</remarks>
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
