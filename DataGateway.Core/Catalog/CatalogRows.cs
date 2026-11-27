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
    bool CanSelect, bool CanFilter, bool CanSort, bool CanGroup, bool CanAggregate)
{
    /// <summary>The catalog column's <c>expression</c> cell: null for a column read from the source as stored.</summary>
    /// <remarks>
    /// <para>Reserved for a later version. <see cref="CatalogMapping.Map"/> refuses any column whose expression is not
    /// null, empty and whitespace included, so a computed column belongs in the source's view for now. Readers set it so
    /// a filled-in cell is refused instead of silently dropped.</para>
    /// <para>An init property rather than a fourteenth constructor parameter, so existing callers of the thirteen-argument
    /// constructor and its Deconstruct keep compiling. It is never serialized, and the record's ToString leaves it out, so
    /// the expression text cannot reach an answer or a log line.</para>
    /// </remarks>
    [JsonIgnore] public string? Expression { get; init; }

    /// <summary>The catalog column's <c>store_type</c> cell: the source database's own spelling of the column's type, such
    /// as <c>varchar(20)</c>, or null to bind filter values as before.</summary>
    /// <remarks>
    /// <para>Written in the source database's own spelling, so only that database's engine reads it: the contract keeps
    /// the text as it is (<see cref="SourceContract.StoreTypes"/>), and the SQL Server engine parses it and checks it
    /// against the source's view. Other engines ignore it, and text the SQL Server engine cannot read is logged and
    /// binds as if empty; it never refuses the source.</para>
    /// <para>An init property for the same reason as <see cref="Expression"/>: the thirteen-argument constructor, its
    /// Deconstruct, the JSON shape and ToString stay as they were.</para>
    /// </remarks>
    [JsonIgnore] public string? StoreType { get; init; }

    /// <summary>The catalog column's <c>store_type_override</c> cell: true keeps <see cref="StoreType"/> even where the
    /// source's view reports another type; false (the default) lets the view's type decide.</summary>
    [JsonIgnore] public bool StoreTypeOverride { get; init; }

    // Matches the compiler-generated member list for the thirteen positional properties, without Expression, StoreType or StoreTypeOverride.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("ColumnName = ").Append(ColumnName)
            .Append(", Ordinal = ").Append(Ordinal)
            .Append(", Title = ").Append(Title)
            .Append(", Description = ").Append(Description)
            .Append(", DataType = ").Append(DataType)
            .Append(", Unit = ").Append(Unit)
            .Append(", Magnitude = ").Append(Magnitude)
            .Append(", DisplayFormat = ").Append(DisplayFormat)
            .Append(", CanSelect = ").Append(CanSelect)
            .Append(", CanFilter = ").Append(CanFilter)
            .Append(", CanSort = ").Append(CanSort)
            .Append(", CanGroup = ").Append(CanGroup)
            .Append(", CanAggregate = ").Append(CanAggregate);
        return true;
    }
}

public sealed class CatalogRows(DataSourceRow source, IReadOnlyList<DataSourceColumnRow> columns)
{
    public DataSourceRow Source { get; } = source;
    public IReadOnlyList<DataSourceColumnRow> Columns { get; } = Array.AsReadOnly(columns.ToArray());
}
