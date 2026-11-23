using DataGateway.Core.Errors;
using SqlKata.JsonQuery;

namespace DataGateway.Core.Catalog;

/// <summary>Pure catalog rows -> immutable source contract, without I/O or ambient settings.</summary>
/// <remarks>
/// <para>Runs after <see cref="CatalogValidator"/> has checked the rows' shape; this step checks what the columns
/// mean. Every refusal is one <c>InvalidCatalog</c> failure that tells the data steward what to correct, except an
/// inactive source, which is an unknown source to the caller.</para>
/// <para>Mapping stops at the first problem: a contract is built whole or not at all.</para>
/// </remarks>
public static class CatalogMapping
{
    /// <summary>Maps one source's catalog rows to the contract that describe and query use.</summary>
    /// <remarks>Columns are taken in <c>Ordinal</c> order, then by name, so the contract's column order is total
    /// even when ordinals repeat. A column with every flag off stays in the described metadata but is not
    /// queryable.</remarks>
    /// <param name="rows">The source row and its column rows, already shape-checked.</param>
    /// <returns>The source contract, or a failure: <c>UnknownSource</c> for an inactive source, otherwise
    /// <c>InvalidCatalog</c> at <c>$.source</c>.</returns>
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

    /// <summary>The query operations a column's five catalog flags switch on, as the query library's flag set.</summary>
    /// <param name="row">The catalog column.</param>
    /// <returns>The combined capabilities; <see cref="QueryCapability.None"/> when every flag is off.</returns>
    private static QueryCapability Capabilities(DataSourceColumnRow row) =>
        (row.CanSelect ? QueryCapability.Select : QueryCapability.None) |
        (row.CanFilter ? QueryCapability.Filter : QueryCapability.None) |
        (row.CanSort ? QueryCapability.Sort : QueryCapability.None) |
        (row.CanGroup ? QueryCapability.Group : QueryCapability.None) |
        (row.CanAggregate ? QueryCapability.Aggregate : QueryCapability.None);

    /// <summary>An <c>InvalidCatalog</c> refusal at <c>$.source</c> with the steward's correction.</summary>
    /// <param name="message">What to correct in the catalog.</param>
    /// <returns>The failed result.</returns>
    private static CatalogResult<SourceContract> Reject(string message) =>
        CatalogResult<SourceContract>.Reject(new("InvalidCatalog", "$.source", message));

    /// <summary>Whether a column name is a bare ASCII SQL identifier: a letter or underscore, then letters, digits
    /// or underscores.</summary>
    /// <param name="name">The catalog column name.</param>
    /// <returns><see langword="true"/> for a usable identifier.</returns>
    private static bool IsIdentifier(string name) =>
        name.Length > 0 && IsLeading(name[0]) && name.All(c => IsLeading(c) || char.IsAsciiDigit(c));

    /// <summary>Whether a character may start an identifier: an ASCII letter or underscore.</summary>
    /// <param name="value">The character to test.</param>
    /// <returns><see langword="true"/> for an ASCII letter or <c>_</c>.</returns>
    private static bool IsLeading(char value) => char.IsAsciiLetter(value) || value == '_';
}
