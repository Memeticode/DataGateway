using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Data.Catalog;
using DataGateway.Data.SqlServer;
using Microsoft.Data.SqlClient;

namespace DataGateway.Web.Catalog;

/// <summary>The example catalog reader for a SQL Server catalog: it reads the <c>dbo</c> projection views and maps
/// their rows through <see cref="CatalogTableReader"/>.</summary>
/// <remarks>
/// <para>Catalog storage is the application's choice, not the library's; this schema is this host's. The reader
/// selects only the projection views, never the tables behind them. Every read goes to the catalog afresh, so a
/// catalog edit applies on the next request.</para>
/// <para>The SQL statements are the reader's whole contract with the schema. A test pins each one's exact bytes,
/// line endings included, so a change to them is deliberate.</para>
/// </remarks>
public sealed class SqlServerCatalog : CatalogService<SqlConnection>
{
    /// <summary>The command timeout for catalog reads, in seconds.</summary>
    private readonly int catalogTimeoutSeconds;

    /// <summary>Creates the reader over a catalog context.</summary>
    /// <param name="db">The catalog context; the application owns it.</param>
    /// <param name="catalogTimeoutSeconds">The command timeout for catalog reads only, in seconds; positive. Source
    /// queries use each source's own <c>query_timeout_seconds</c>.</param>
    public SqlServerCatalog(DbContext<SqlConnection> db, int catalogTimeoutSeconds = 15) : base(db)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(catalogTimeoutSeconds);
        this.catalogTimeoutSeconds = catalogTimeoutSeconds;
    }

    /// <inheritdoc/>
    public override Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(CancellationToken cancellationToken) =>
        CatalogTableReader.ListAsync(Db, ListSql, catalogTimeoutSeconds, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>The name matches under a case-insensitive collation. A row the database matched for a different
    /// name is dropped by <see cref="CatalogTableReader.Matching"/>, so the answer is an unknown source. Details
    /// carry <see cref="SqlServerEngine.ProviderName"/>, and a flag may be a boolean or the integer 0 or 1.</remarks>
    public override async Task<CatalogDetail?> QueryCatalogDetailAsync(
        string sourceName, CancellationToken cancellationToken) =>
        CatalogTableReader.Matching(await CatalogTableReader.DetailAsync(Db, ReadSql, AvailableSql, sourceName,
            SqlServerEngine.ProviderName, true, catalogTimeoutSeconds, cancellationToken).ConfigureAwait(false), sourceName);

    /// <inheritdoc/>
    public override Task<McpMetadata> QueryMcpMetadataAsync(CancellationToken cancellationToken) =>
        CatalogTableReader.MetadataAsync(Db, McpServerSql, McpToolsSql, catalogTimeoutSeconds, cancellationToken);

    /// <summary>Reads the one MCP server row.</summary>
    internal const string McpServerSql = "SELECT [id], [title], [about] FROM [dbo].[vMcpServer];";
    /// <summary>Reads the MCP tool rows.</summary>
    internal const string McpToolsSql = "SELECT [tool_name], [title], [description] FROM [dbo].[vMcpTool];";

    /// <summary>Reads the active sources for the list.</summary>
    internal const string ListSql =
        "SELECT [name], [title], [description], [grain], [row_cap]\r\n" +
        "FROM [dbo].[vDataSource]\r\n" +
        "WHERE [is_active] = 1;";

    /// <summary>Reads the active names, after a detail read matched nothing.</summary>
    internal const string AvailableSql = "SELECT [name] FROM [dbo].[vDataSource] WHERE [is_active] = 1;";

    /// <summary>Reads one active source joined with its columns; a source with no column yields one row with null
    /// column cells.</summary>
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
