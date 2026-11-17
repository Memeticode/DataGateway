using System.Text.Json.Serialization;

namespace DataGateway.Core.Catalog;

// Classes deliberately avoid record-generated ToString methods for secret-bearing data.
public sealed class DataSourceRow(string objectName, string connectionString)
{
    [JsonIgnore] public int Id { get; init; }
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Grain { get; init; }
    public required bool IsActive { get; init; }
    public required int RowCap { get; init; }
    public required int QueryTimeoutSeconds { get; init; }
    [JsonIgnore] public string ObjectName { get; } = objectName;
    [JsonIgnore] public string ConnectionString { get; } = connectionString;
}

public sealed record DataSourceColumnRow(
    string ColumnName, int Ordinal, string Title, string Description, string DataType,
    string? Unit, string? Magnitude, string? DisplayFormat,
    bool CanSelect, bool CanFilter, bool CanSort, bool CanGroup, bool CanAggregate);

public sealed class CatalogRows(DataSourceRow source, IReadOnlyList<DataSourceColumnRow> columns)
{
    public DataSourceRow Source { get; } = source;
    public IReadOnlyList<DataSourceColumnRow> Columns { get; } = Array.AsReadOnly(columns.ToArray());
}
