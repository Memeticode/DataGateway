using System.Data.Common;
using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Data.Catalog;

namespace DataGateway.Web.Catalog;

internal static class CatalogTableReader
{
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

    private static string MetadataText(IDictionary<string, object?> row, string field, string table)
    {
        if (!row.TryGetValue(field, out var value) || value is not string text)
            throw new InvalidMcpMetadataException(table + "." + field);
        return text;
    }

    private static int MetadataId(IDictionary<string, object?> row)
    {
        try { return Integer(Value(row, "id")); }
        catch (InvalidCastException) { throw new InvalidMcpMetadataException("mcpserver.id"); }
    }

    internal const string InvalidCell = "The catalog cell shape is invalid.";
    private static readonly string[] ColumnFields =
    [
        "column_name", "ordinal", "column_title", "column_description", "data_type", "unit",
        "magnitude", "display_format", "can_select", "can_filter", "can_sort", "can_group", "can_aggregate"
    ];

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

    // The database may match more loosely than DataGateway does (SQL Server ignores trailing spaces and folds accents
    // under its collation); a row for another name is an unknown source, not a catalog fault.
    internal static CatalogDetail? Matching(CatalogDetail? detail, string sourceName) =>
        detail is not null && StringComparer.OrdinalIgnoreCase.Equals(detail.Rows.Source.Name, sourceName) ? detail : null;

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

    internal static void ValidateAvailable(IReadOnlyList<dynamic> rows)
    {
        foreach (object? raw in rows)
            _ = Text(Row(raw), "name");
    }

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

    private static IDictionary<string, object?> Row(object? raw) =>
        raw is IDictionary<string, object?> row ? row : throw BadCell();

    private static object? Value(IDictionary<string, object?> row, string field)
    {
        if (!row.TryGetValue(field, out var value))
            throw BadCell();
        return value is DBNull ? null : value;
    }

    private static string Text(IDictionary<string, object?> row, string field) =>
        Value(row, field) is string text ? text : throw BadCell();

    private static string? NullableText(IDictionary<string, object?> row, string field)
    {
        var value = Value(row, field);
        if (value is null)
            return null;
        return value is string text ? text : throw BadCell();
    }

    private static int RowCap(IDictionary<string, object?> row)
    {
        var cap = Integer(Value(row, "row_cap"));
        if (cap < 1 || cap > QueryLimits.MaxRowCap)
            throw BadCell();
        return cap;
    }

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

    private static InvalidCastException BadCell() => new(InvalidCell);
}
