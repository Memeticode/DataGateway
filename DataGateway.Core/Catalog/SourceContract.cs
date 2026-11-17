using System.Text.Json.Serialization;
using SqlKata.JsonQuery;

namespace DataGateway.Core.Catalog;

public sealed record SourceMetadata(string Name, string Title, string Description, string Grain,
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)] int RowCap);

public sealed record ColumnMetadata(
    string Name, string Title, string Description, string DataType,
    string? Unit, string? Magnitude, string? DisplayFormat,
    bool CanSelect, bool CanFilter, bool CanSort, bool CanGroup, bool CanAggregate);

/// <summary>Server-side contract; transport projections must use reasoning metadata only.</summary>
public sealed class SourceContract
{
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

    [JsonIgnore] public int Id { get; }
    [JsonIgnore] public int QueryTimeoutSeconds { get; }
    public SourceMetadata Source { get; }
    public IReadOnlyList<ColumnMetadata> Columns { get; }
    [JsonIgnore] public string ObjectName { get; }
    [JsonIgnore] public string ConnectionString { get; }
    [JsonIgnore] public QueryColumns QueryColumns { get; }
}
