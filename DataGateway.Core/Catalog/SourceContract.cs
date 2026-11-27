using System.Text.Json.Serialization;
using SqlKata.JsonQuery;

namespace DataGateway.Core.Catalog;

public sealed record SourceMetadata(string Name, string Title, string Description, string Grain,
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)] int RowCap);

public sealed record ColumnMetadata(
    string Name, string Title, string Description, string DataType,
    string? Unit, string? Magnitude, string? DisplayFormat,
    bool CanSelect, bool CanFilter, bool CanSort, bool CanGroup, bool CanAggregate);

/// <summary>A column's catalog <c>store_type</c> text and its <c>store_type_override</c>, kept as written.</summary>
public sealed record CatalogStoreType(string Text, bool Override);

/// <summary>Server-side contract; transport projections must use reasoning metadata only.</summary>
public sealed class SourceContract
{
    internal SourceContract(DataSourceRow source, QueryColumns queryColumns, ColumnMetadata[] columns,
        IReadOnlyDictionary<string, CatalogStoreType> storeTypes)
    {
        Id = source.Id;
        QueryTimeoutSeconds = source.QueryTimeoutSeconds;
        Source = new(source.Name, source.Title, source.Description, source.Grain, source.RowCap);
        ObjectName = source.ObjectName;
        ConnectionString = source.ConnectionString;
        QueryColumns = queryColumns;
        Columns = Array.AsReadOnly(columns);
        StoreTypes = storeTypes;
    }

    [JsonIgnore] public int Id { get; }
    [JsonIgnore] public int QueryTimeoutSeconds { get; }
    public SourceMetadata Source { get; }
    public IReadOnlyList<ColumnMetadata> Columns { get; }
    [JsonIgnore] public string ObjectName { get; }
    [JsonIgnore] public string ConnectionString { get; }
    [JsonIgnore] public QueryColumns QueryColumns { get; }

    /// <summary>Each column's catalog <c>store_type</c> as written, with its <c>store_type_override</c>, keyed by column
    /// name ignoring case; a column whose <c>store_type</c> is null is absent.</summary>
    /// <remarks>Server-side only, like the connection: no answer shows it. It is the source database's own spelling, so
    /// it stays text here; the engine for that database decides what it means (the SQL Server engine parses it and
    /// checks it against the view; other engines ignore it).</remarks>
    [JsonIgnore] public IReadOnlyDictionary<string, CatalogStoreType> StoreTypes { get; }
}
