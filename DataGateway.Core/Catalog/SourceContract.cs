using System.Text.Json.Serialization;
using SqlKata.JsonQuery;

namespace DataGateway.Core.Catalog;

/// <summary>The public description of a source, as list and describe return it.</summary>
/// <param name="Name">The source name callers use.</param>
/// <param name="Title">A short human title.</param>
/// <param name="Description">What the source holds.</param>
/// <param name="Grain">What one row represents.</param>
/// <param name="RowCap">The most rows one query returns; strictly a JSON number, whatever the serializer's number handling.</param>
public sealed record SourceMetadata(string Name, string Title, string Description, string Grain,
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)] int RowCap);

/// <summary>The public description of one column, as describe returns it.</summary>
/// <remarks>Carries all five flags even for a column with none set, so a caller sees every column the catalog
/// documents.</remarks>
/// <param name="Name">The column's name in rows and queries.</param>
/// <param name="Title">A short human title.</param>
/// <param name="Description">What the column means.</param>
/// <param name="DataType">One of <c>text</c>, <c>number</c>, <c>date</c> or <c>bool</c>.</param>
/// <param name="Unit">The unit of the values, if any.</param>
/// <param name="Magnitude">The scale of the values, such as millions, if any.</param>
/// <param name="DisplayFormat">How to display the values, if any.</param>
/// <param name="CanSelect">Whether the column may be selected.</param>
/// <param name="CanFilter">Whether the column may be filtered on.</param>
/// <param name="CanSort">Whether the column may be sorted by.</param>
/// <param name="CanGroup">Whether the column may be grouped by.</param>
/// <param name="CanAggregate">Whether the column may be aggregated.</param>
public sealed record ColumnMetadata(
    string Name, string Title, string Description, string DataType,
    string? Unit, string? Magnitude, string? DisplayFormat,
    bool CanSelect, bool CanFilter, bool CanSort, bool CanGroup, bool CanAggregate);

/// <summary>Server-side contract; transport projections must use reasoning metadata only.</summary>
/// <remarks>
/// <para>One mapped source: the public <see cref="Source"/> and <see cref="Columns"/> a caller reasons with, plus
/// the server-side facts a query needs (connection, view, timeout, queryable columns), each excluded from
/// JSON.</para>
/// <para>The constructor is internal: only <see cref="CatalogMapping.Map"/> builds one, so every contract has passed
/// catalog mapping.</para>
/// </remarks>
public sealed class SourceContract
{
    /// <summary>Copies the source row's facts and the mapped columns into the contract.</summary>
    /// <param name="source">The validated source row.</param>
    /// <param name="queryColumns">The columns the query library may use, with their capabilities.</param>
    /// <param name="columns">Every described column.</param>
    internal SourceContract(DataSourceRow source, QueryColumns queryColumns, ColumnMetadata[] columns)
    {
        Id = source.Id;
        QueryTimeoutSeconds = source.QueryTimeoutSeconds;
        Source = new(source.Name, source.Title, source.Description, source.Grain, source.RowCap);
        ObjectName = source.ObjectName;
        ConnectionString = source.ConnectionString;
        QueryColumns = queryColumns;
        Columns = Array.AsReadOnly(columns);
    }

    /// <summary>The catalog's identifier for the source, for diagnostics.</summary>
    [JsonIgnore] public int Id { get; }
    /// <summary>The deadline for one query on this source, in seconds.</summary>
    [JsonIgnore] public int QueryTimeoutSeconds { get; }
    /// <summary>The source's public description.</summary>
    public SourceMetadata Source { get; }
    /// <summary>Every described column, in catalog order.</summary>
    public IReadOnlyList<ColumnMetadata> Columns { get; }
    /// <summary>The view or table the source's queries read.</summary>
    [JsonIgnore] public string ObjectName { get; }
    /// <summary>The connection string for the source database.</summary>
    [JsonIgnore] public string ConnectionString { get; }
    /// <summary>The columns the query library validates a query against: only those with at least one flag
    /// set.</summary>
    [JsonIgnore] public QueryColumns QueryColumns { get; }
}
