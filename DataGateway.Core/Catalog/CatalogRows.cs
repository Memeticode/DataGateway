using System.Text.Json.Serialization;

namespace DataGateway.Core.Catalog;

// Classes deliberately avoid record-generated ToString methods for secret-bearing data.
/// <summary>One catalog source as the application's catalog reader returns it: its public description, its row
/// cap and timeout, and the server-side facts needed to run a query.</summary>
/// <remarks>Server-side facts (<see cref="Id"/>, <see cref="ObjectName"/>, <see cref="ConnectionString"/>) are
/// excluded from JSON, so serializing a row never puts them on the wire.</remarks>
/// <param name="objectName">The view or table the source's queries read, as <c>schema.view</c> or <c>view</c>.</param>
/// <param name="connectionString">The connection string for the source database; it can carry a password.</param>
public sealed class DataSourceRow(string objectName, string connectionString)
{
    /// <summary>The catalog's identifier for the source; optional, for diagnostics only.</summary>
    [JsonIgnore] public int Id { get; init; }
    /// <summary>The source name callers use; unique ignoring case.</summary>
    public required string Name { get; init; }
    /// <summary>A short human title for the source.</summary>
    public required string Title { get; init; }
    /// <summary>What the source holds, for a caller choosing a source.</summary>
    public required string Description { get; init; }
    /// <summary>What one row of the source represents.</summary>
    public required string Grain { get; init; }
    /// <summary>Whether the source is offered; an inactive source is an unknown source to callers.</summary>
    public required bool IsActive { get; init; }
    /// <summary>The most rows one query may return, from 1 through <see cref="QueryLimits.MaxRowCap"/>.</summary>
    public required int RowCap { get; init; }
    /// <summary>The deadline for one query on this source, in seconds, from 1 through 86400.</summary>
    public required int QueryTimeoutSeconds { get; init; }
    /// <summary>The view or table the source's queries read.</summary>
    [JsonIgnore] public string ObjectName { get; } = objectName;
    /// <summary>The connection string for the source database.</summary>
    [JsonIgnore] public string ConnectionString { get; } = connectionString;
}

/// <summary>One catalog column: its description, display hints and the five flags that switch query operations
/// on.</summary>
/// <remarks>Each flag is independent: no flag implies another, and none is inferred from the data type
/// (<see cref="CatalogMapping"/>).</remarks>
/// <param name="ColumnName">The column's name in the source view; a bare ASCII SQL identifier.</param>
/// <param name="Ordinal">The column's position in described output.</param>
/// <param name="Title">A short human title for the column.</param>
/// <param name="Description">What the column means.</param>
/// <param name="DataType">One of <c>text</c>, <c>number</c>, <c>date</c> or <c>bool</c>.</param>
/// <param name="Unit">The unit of the values, if any.</param>
/// <param name="Magnitude">The scale of the values, such as millions, if any.</param>
/// <param name="DisplayFormat">How to display the values, if any; never a filter format.</param>
/// <param name="CanSelect">Whether the column may be selected.</param>
/// <param name="CanFilter">Whether the column may be filtered on.</param>
/// <param name="CanSort">Whether the column may be sorted by.</param>
/// <param name="CanGroup">Whether the column may be grouped by.</param>
/// <param name="CanAggregate">Whether the column may be aggregated.</param>
public sealed record DataSourceColumnRow(
    string ColumnName, int Ordinal, string Title, string Description, string DataType,
    string? Unit, string? Magnitude, string? DisplayFormat,
    bool CanSelect, bool CanFilter, bool CanSort, bool CanGroup, bool CanAggregate);

/// <summary>One source's catalog row and its column rows, together.</summary>
/// <param name="source">The source row.</param>
/// <param name="columns">The column rows; copied on construction.</param>
public sealed class CatalogRows(DataSourceRow source, IReadOnlyList<DataSourceColumnRow> columns)
{
    /// <summary>The source row.</summary>
    public DataSourceRow Source { get; } = source;
    /// <summary>A read-only copy of the column rows.</summary>
    /// <remarks>Copied so a reader that keeps or reuses its own list cannot change the rows after validation.</remarks>
    public IReadOnlyList<DataSourceColumnRow> Columns { get; } = Array.AsReadOnly(columns.ToArray());
}
