using DataGateway.Core.Errors;
using SqlKata.JsonQuery;

namespace DataGateway.Core.Catalog;

/// <summary>Pure catalog rows -> immutable source contract, without I/O or ambient settings.</summary>
public static class CatalogMapping
{
    public static CatalogResult<SourceContract> Map(CatalogRows rows)
    {
        if (!rows.Source.IsActive)
            return CatalogResult<SourceContract>.Reject(new(DataGatewayError.UnknownSource.Code, "$.source",
                "Choose an active source returned by list sources."));

        if (rows.Columns.Count == 0)
            return Reject("Catalog an accessible column before using this source.");

        var builder = QueryColumns.CreateBuilder();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadata = new List<ColumnMetadata>();
        foreach (var row in rows.Columns.OrderBy(column => column.Ordinal)
                     .ThenBy(column => column.ColumnName, StringComparer.Ordinal))
        {
            if (!IsIdentifier(row.ColumnName))
                return Reject("Correct the catalog column name: use a bare ASCII SQL identifier.");
            if (!names.Add(row.ColumnName))
                return Reject($"Column '{row.ColumnName}' is duplicated ignoring case. Correct the catalog.");

            // No defaults or type-based capability inference: each catalog bit is independent.
            var capabilities = Capabilities(row);
            if (row.DataType is not ("text" or "number" or "date" or "bool"))
                return Reject($"Column '{row.ColumnName}' has an unsupported data_type. " +
                    "Set it to text, number, date or bool in the catalog.");
            // Documented-only columns remain metadata, but never enter the library contract.
            if (capabilities != QueryCapability.None)
            {
                switch (row.DataType)
                {
                    case "text": builder.Text(row.ColumnName, capabilities); break;
                    case "number": builder.Number(row.ColumnName, capabilities); break;
                    case "date": builder.Date(row.ColumnName, capabilities); break;
                    case "bool": builder.Bool(row.ColumnName, capabilities); break;
                }
            }
            metadata.Add(new(row.ColumnName, row.Title, row.Description, row.DataType,
                row.Unit, row.Magnitude, row.DisplayFormat,
                row.CanSelect, row.CanFilter, row.CanSort, row.CanGroup, row.CanAggregate));
        }
        if (!rows.Columns.Any(row => Capabilities(row) != QueryCapability.None))
            return Reject("Switch on at least one flag in the catalog before using this source.");

        return CatalogResult<SourceContract>.Success(new(rows.Source, builder.Build(), metadata.ToArray()));
    }

    private static QueryCapability Capabilities(DataSourceColumnRow row) =>
        (row.CanSelect ? QueryCapability.Select : QueryCapability.None) |
        (row.CanFilter ? QueryCapability.Filter : QueryCapability.None) |
        (row.CanSort ? QueryCapability.Sort : QueryCapability.None) |
        (row.CanGroup ? QueryCapability.Group : QueryCapability.None) |
        (row.CanAggregate ? QueryCapability.Aggregate : QueryCapability.None);

    private static CatalogResult<SourceContract> Reject(string message) =>
        CatalogResult<SourceContract>.Reject(new("InvalidCatalog", "$.source", message));

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && IsLeading(name[0]) && name.All(c => IsLeading(c) || char.IsAsciiDigit(c));

    private static bool IsLeading(char value) => char.IsAsciiLetter(value) || value == '_';
}
