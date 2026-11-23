using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Data.Catalog;
using DataGateway.Data.PostgreSql;
using Npgsql;

namespace DataGateway.Web.Catalog;

/// <summary>The example catalog reader for a PostgreSQL catalog: it reads the <c>datagateway</c> schema's projection
/// views and maps their rows through <see cref="CatalogTableReader"/>.</summary>
/// <remarks>
/// <para>Catalog storage is the application's choice, not the library's; this schema is this host's. Every read
/// goes to the catalog afresh, so a catalog edit applies on the next request.</para>
/// <para>The SQL statements are the reader's whole contract with the schema. A test pins each one's exact bytes, so
/// a change to them is deliberate.</para>
/// </remarks>
public sealed class PostgreSqlCatalog : CatalogService<NpgsqlConnection>
{
    /// <summary>The command timeout for catalog reads, in seconds.</summary>
    private readonly int catalogTimeoutSeconds;

    /// <summary>Creates the reader over a catalog context.</summary>
    /// <param name="db">The catalog context; the application owns it.</param>
    /// <param name="catalogTimeoutSeconds">The command timeout for catalog reads only, in seconds; positive. Source
    /// queries use each source's own <c>query_timeout_seconds</c>.</param>
    public PostgreSqlCatalog(DbContext<NpgsqlConnection> db, int catalogTimeoutSeconds = 15) : base(db)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(catalogTimeoutSeconds);
        this.catalogTimeoutSeconds = catalogTimeoutSeconds;
    }

    /// <inheritdoc/>
    public override Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(CancellationToken cancellationToken) =>
        CatalogTableReader.ListAsync(Db, ListSql, catalogTimeoutSeconds, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>The name matches ignoring ASCII case. A row the database matched for a different name is dropped by
    /// <see cref="CatalogTableReader.Matching"/>, so the answer is an unknown source. Details carry
    /// <see cref="PostgreSqlEngine.ProviderName"/>, and flags must be true booleans.</remarks>
    public override async Task<CatalogDetail?> QueryCatalogDetailAsync(
        string sourceName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        // A blank name matches no source: unknown (404), as the SQL Server reader answers, not a catalog failure.
        if (string.IsNullOrWhiteSpace(sourceName)) return null;
        return CatalogTableReader.Matching(await CatalogTableReader.DetailAsync(Db, ReadSql, AvailableSql, sourceName,
            PostgreSqlEngine.ProviderName, false, catalogTimeoutSeconds, cancellationToken).ConfigureAwait(false), sourceName);
    }

    /// <inheritdoc/>
    public override Task<McpMetadata> QueryMcpMetadataAsync(CancellationToken cancellationToken) =>
        CatalogTableReader.MetadataAsync(Db, McpServerSql, McpToolsSql, catalogTimeoutSeconds, cancellationToken);

    /// <summary>Reads the one MCP server row.</summary>
    internal const string McpServerSql = "SELECT id, title, about FROM datagateway.vmcpserver;";
    /// <summary>Reads the MCP tool rows.</summary>
    internal const string McpToolsSql = "SELECT tool_name, title, description FROM datagateway.vmcptool;";

    /// <summary>Reads the active sources for the list.</summary>
    internal const string ListSql = "SELECT name, title, description, grain, row_cap\n" +
        "FROM datagateway.vdatasource\nWHERE is_active = TRUE;";
    /// <summary>Reads the active names, after a detail read matched nothing.</summary>
    internal const string AvailableSql = "SELECT name FROM datagateway.vdatasource WHERE is_active = TRUE;";
    /// <summary>Reads one active source joined with its columns; a source with no column yields one row with null
    /// column cells.</summary>
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
