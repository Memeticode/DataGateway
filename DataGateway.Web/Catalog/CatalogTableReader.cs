using System.Data.Common;
using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Data.Catalog;

namespace DataGateway.Web.Catalog;

/// <summary>The row mapping shared by the two example catalog readers, <see cref="SqlServerCatalog"/> and
/// <see cref="PostgreSqlCatalog"/>: it runs their SQL and turns the rows into the library's catalog types.</summary>
/// <remarks>
/// <para>The mapping is strict. Every expected cell must be present and of the expected type; a missing cell, a
/// wrong type or an out-of-range number throws instead of being guessed or defaulted. A damaged catalog then fails
/// the request loudly, as CatalogUnavailable, rather than serving made-up metadata.</para>
/// <para>A bad cell throws an <see cref="InvalidCastException"/> with the fixed message <see cref="InvalidCell"/>,
/// which names neither the cell nor its value: a catalog row holds the source's connection string, credentials
/// included. Two faults have exceptions of their own: <see cref="InvalidMcpMetadataException"/> names the MCP field
/// at fault, and <see cref="InvalidQueryTimeoutException"/> becomes an InvalidCatalog answer that tells the steward
/// what to set.</para>
/// <para>Every read follows the catalog-reader contract of <see cref="CatalogService{TConnection}"/>: results are
/// buffered and copied before they are returned, nothing is kept between calls, and cancellation is checked after
/// each await so a cancelled request never returns a result.</para>
/// </remarks>
internal static class CatalogTableReader
{
    /// <summary>Reads the MCP presentation text: the server's title and about text, and each tool's title and
    /// description.</summary>
    /// <remarks>The server projection must hold exactly one row, with id 1; anything else is a fault in
    /// <c>mcpserver.id</c>. Both queries run in one connection scope. Which tool names must be present is checked
    /// by the gateway, not here.</remarks>
    /// <param name="db">The catalog context.</param>
    /// <param name="serverSql">The query for the server row.</param>
    /// <param name="toolsSql">The query for the tool rows.</param>
    /// <param name="timeout">The command timeout, in seconds.</param>
    /// <param name="token">Cancels the read.</param>
    /// <returns>The presentation text, detached from the reader.</returns>
    internal static async Task<McpMetadata> MetadataAsync<TConnection>(DbContext<TConnection> db,
        string serverSql, string toolsSql, int timeout, CancellationToken token)
        where TConnection : DbConnection, new()
    {
        token.ThrowIfCancellationRequested();
        var result = await db.BatchAsync<McpMetadata>(async session =>
        {
            var servers = await session.QueryAsync(serverSql, commandTimeout: timeout, cancellationToken: token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (servers.Count != 1) throw new InvalidMcpMetadataException("mcpserver.id");
            var presentation = Row((object?)servers[0]);
            if (MetadataId(presentation) != 1) throw new InvalidMcpMetadataException("mcpserver.id");
            var tools = await session.QueryAsync(toolsSql, commandTimeout: timeout, cancellationToken: token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new McpMetadata(MetadataText(presentation, "title", "mcpserver"), MetadataText(presentation, "about", "mcpserver"), tools.Select(raw =>
            {
                var row = Row((object?)raw);
                return new McpToolMetadata(MetadataText(row, "tool_name", "mcptool"), MetadataText(row, "title", "mcptool"), MetadataText(row, "description", "mcptool"));
            }).ToArray());
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>One required text cell of an MCP projection row.</summary>
    /// <param name="row">The row.</param>
    /// <param name="field">The cell's column name.</param>
    /// <param name="table">The projection's name, for the fault's field path.</param>
    private static string MetadataText(IDictionary<string, object?> row, string field, string table)
    {
        if (!row.TryGetValue(field, out var value) || value is not string text)
            throw new InvalidMcpMetadataException(table + "." + field);
        return text;
    }

    /// <summary>The server row's id; a malformed id is an MCP metadata fault rather than a bad cell.</summary>
    private static int MetadataId(IDictionary<string, object?> row)
    {
        try { return Integer(Value(row, "id")); }
        catch (InvalidCastException) { throw new InvalidMcpMetadataException("mcpserver.id"); }
    }

    /// <summary>The message of every bad-cell exception; deliberately the same for every cell.</summary>
    internal const string InvalidCell = "The catalog cell shape is invalid.";
    /// <summary>The column cells of a detail row; all null when the source has no column row (the LEFT JOIN
    /// sentinel, see <see cref="MapDetail"/>).</summary>
    private static readonly string[] ColumnFields =
    [
        "column_name", "ordinal", "column_title", "column_description", "data_type", "unit",
        "magnitude", "display_format", "can_select", "can_filter", "can_sort", "can_group", "can_aggregate"
    ];

    /// <summary>Reads the list of active sources.</summary>
    /// <param name="db">The catalog context.</param>
    /// <param name="sql">The list query.</param>
    /// <param name="timeout">The command timeout, in seconds.</param>
    /// <param name="token">Cancels the read.</param>
    /// <returns>One row per active source, in the order the query returns them.</returns>
    internal static async Task<IReadOnlyList<SourceListRow>> ListAsync<TConnection>(
        DbContext<TConnection> db, string sql, int timeout, CancellationToken token)
        where TConnection : DbConnection, new()
    {
        token.ThrowIfCancellationRequested();
        var rows = await db.QueryAsync(sql, commandTimeout: timeout,
            cancellationToken: token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var result = MapList(rows);
        token.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Reads one source with its columns, or null when no active source matches the name.</summary>
    /// <remarks>On a miss, the active names are read in the same connection scope and the shape of each name cell
    /// is checked; the names themselves are not returned.</remarks>
    /// <param name="db">The catalog context.</param>
    /// <param name="readSql">The joined source and column query, taking <c>@source</c>.</param>
    /// <param name="availableSql">The query for the active names, run only on a miss.</param>
    /// <param name="sourceName">The requested name, as the caller sent it.</param>
    /// <param name="provider">The provider name the detail carries, which selects the engine for its queries.</param>
    /// <param name="allowIntegralFlags">Whether a flag cell may be the integer 0 or 1 as well as a boolean.</param>
    /// <param name="timeout">The command timeout, in seconds.</param>
    /// <param name="token">Cancels the read.</param>
    /// <returns>The detail, or null for an unknown source.</returns>
    internal static async Task<CatalogDetail?> DetailAsync<TConnection>(
        DbContext<TConnection> db, string readSql, string availableSql, string sourceName,
        string provider, bool allowIntegralFlags, int timeout, CancellationToken token)
        where TConnection : DbConnection, new()
    {
        token.ThrowIfCancellationRequested();
        var result = await db.BatchAsync<CatalogDetail?>(async session =>
        {
            var rows = await session.QueryAsync(readSql, new { source = sourceName },
                commandTimeout: timeout, cancellationToken: token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (rows.Count == 0)
            {
                var names = await session.QueryAsync(availableSql, commandTimeout: timeout,
                    cancellationToken: token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ValidateAvailable(names);
                return null;
            }
            return MapDetail(rows, provider, allowIntegralFlags);
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Keeps a detail only when its source name equals the requested name, ignoring case.</summary>
    /// <remarks>The database may match more loosely than DataGateway does (SQL Server ignores trailing spaces and folds accents
    /// under its collation); a row for another name is an unknown source, not a catalog fault.</remarks>
    /// <param name="detail">The detail the database matched, or null.</param>
    /// <param name="sourceName">The requested name.</param>
    /// <returns>The detail, or null when it is for another name.</returns>
    internal static CatalogDetail? Matching(CatalogDetail? detail, string sourceName) =>
        detail is not null && StringComparer.OrdinalIgnoreCase.Equals(detail.Rows.Source.Name, sourceName) ? detail : null;

    /// <summary>Maps list rows to <see cref="SourceListRow"/> values.</summary>
    /// <remarks>Every row is marked active: the list query reads active sources only.</remarks>
    internal static IReadOnlyList<SourceListRow> MapList(IReadOnlyList<dynamic> rows)
    {
        var result = new List<SourceListRow>(rows.Count);
        foreach (object? raw in rows)
        {
            var row = Row(raw);
            result.Add(new SourceListRow(Text(row, "name"), Text(row, "title"),
                Text(row, "description"), Text(row, "grain"), true, RowCap(row)));
        }
        return result.AsReadOnly();
    }

    /// <summary>Checks that every active-name row has a text <c>name</c> cell.</summary>
    internal static void ValidateAvailable(IReadOnlyList<dynamic> rows)
    {
        foreach (object? raw in rows)
            _ = Text(Row(raw), "name");
    }

    /// <summary>Maps the joined source and column rows to a <see cref="CatalogDetail"/>.</summary>
    /// <remarks>TERM: the LEFT JOIN sentinel. A source with no column row still yields one joined row, with every
    /// column cell null. That row adds no column. A row with a null column name but any other column cell set is a
    /// bad cell: it is neither a column nor the sentinel. The source cells are checked on every row, not only on the
    /// first.</remarks>
    /// <param name="rows">The joined rows, at least one.</param>
    /// <param name="provider">The provider name the detail carries.</param>
    /// <param name="allowIntegralFlags">Whether a flag cell may be the integer 0 or 1.</param>
    internal static CatalogDetail MapDetail(
        IReadOnlyList<dynamic> rows, string provider, bool allowIntegralFlags)
    {
        var source = Source(Row((object?)rows[0]));
        var columns = new List<DataSourceColumnRow>();
        foreach (object? raw in rows)
        {
            var row = Row(raw);
            _ = Source(row);
            if (Value(row, "column_name") is null)
            {
                foreach (var field in ColumnFields)
                    if (Value(row, field) is not null)
                        throw BadCell();
            }
            else
            {
                columns.Add(new DataSourceColumnRow(
                    Text(row, "column_name"), Integer(Value(row, "ordinal")),
                    Text(row, "column_title"), Text(row, "column_description"), Text(row, "data_type"),
                    NullableText(row, "unit"), NullableText(row, "magnitude"), NullableText(row, "display_format"),
                    Flag(row, "can_select", allowIntegralFlags), Flag(row, "can_filter", allowIntegralFlags),
                    Flag(row, "can_sort", allowIntegralFlags), Flag(row, "can_group", allowIntegralFlags),
                    Flag(row, "can_aggregate", allowIntegralFlags)));
            }
        }
        return new CatalogDetail(provider, new CatalogRows(source, columns));
    }

    /// <summary>The source part of a detail row, active by construction: the detail query reads active sources
    /// only.</summary>
    private static DataSourceRow Source(IDictionary<string, object?> row) =>
        new(Text(row, "object_name"), Text(row, "connection_string"))
        {
            Id = Integer(Value(row, "id")),
            Name = Text(row, "name"),
            Title = Text(row, "title"),
            Description = Text(row, "description"),
            Grain = Text(row, "grain"),
            IsActive = true,
            RowCap = RowCap(row),
            QueryTimeoutSeconds = QueryTimeoutSeconds(row)
        };

    /// <summary>One row as the name-to-value map Dapper returns for an untyped query.</summary>
    private static IDictionary<string, object?> Row(object? raw) =>
        raw is IDictionary<string, object?> row ? row : throw BadCell();

    /// <summary>A cell that must be present, with a database null read as null.</summary>
    private static object? Value(IDictionary<string, object?> row, string field)
    {
        if (!row.TryGetValue(field, out var value))
            throw BadCell();
        return value is DBNull ? null : value;
    }

    /// <summary>A required, non-null text cell.</summary>
    private static string Text(IDictionary<string, object?> row, string field) =>
        Value(row, field) is string text ? text : throw BadCell();

    /// <summary>An optional text cell: null, or text.</summary>
    private static string? NullableText(IDictionary<string, object?> row, string field)
    {
        var value = Value(row, field);
        if (value is null)
            return null;
        return value is string text ? text : throw BadCell();
    }

    /// <summary>The source's <c>row_cap</c>: an integer from 1 to <see cref="QueryLimits.MaxRowCap"/>.</summary>
    private static int RowCap(IDictionary<string, object?> row)
    {
        var cap = Integer(Value(row, "row_cap"));
        if (cap < 1 || cap > QueryLimits.MaxRowCap)
            throw BadCell();
        return cap;
    }

    /// <summary>The source's <c>query_timeout_seconds</c>: an integer from 1 through 86400.</summary>
    /// <remarks>Every fault here, a missing cell included, throws <see cref="InvalidQueryTimeoutException"/> rather
    /// than a bad cell, because the gateway turns only that exception into the answer that tells the steward to set
    /// the value.</remarks>
    private static int QueryTimeoutSeconds(IDictionary<string, object?> row)
    {
        if (!row.TryGetValue("query_timeout_seconds", out var value))
            throw new InvalidQueryTimeoutException();
        int seconds;
        try { seconds = Integer(value); }
        catch (InvalidCastException) { throw new InvalidQueryTimeoutException(); }
        if (seconds is < 1 or > 86400)
            throw new InvalidQueryTimeoutException();
        return seconds;
    }

    /// <summary>One capability flag: a boolean, or 0 or 1 when <paramref name="allowIntegralFlags"/> is true.</summary>
    private static bool Flag(IDictionary<string, object?> row, string field, bool allowIntegralFlags)
    {
        var value = Value(row, field);
        if (value is bool flag)
            return flag;
        if (!allowIntegralFlags)
            throw BadCell();
        return Integer(value) switch
        {
            0 => false,
            1 => true,
            _ => throw BadCell()
        };
    }

    /// <summary>An integer cell of any integral type a driver returns, which must fit an <see cref="int"/>.</summary>
    private static int Integer(object? value)
    {
        try
        {
            return value switch
            {
                sbyte number => number,
                byte number => number,
                short number => number,
                ushort number => number,
                int number => number,
                uint number => checked((int)number),
                long number => checked((int)number),
                ulong number => checked((int)number),
                _ => throw BadCell()
            };
        }
        catch (OverflowException)
        {
            throw BadCell();
        }
    }

    /// <summary>The bad-cell exception, carrying only <see cref="InvalidCell"/>.</summary>
    private static InvalidCastException BadCell() => new(InvalidCell);
}
