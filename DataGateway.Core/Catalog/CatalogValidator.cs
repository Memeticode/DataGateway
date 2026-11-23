using System.Globalization;

namespace DataGateway.Core.Catalog;

/// <summary>Checks application catalog result shapes before column mapping.</summary>
/// <remarks>Column semantics are checked by CatalogMapping. Inputs must remain stable during each call.</remarks>
public static class CatalogValidator
{
    /// <summary>Checks the source list a catalog reader returned, entry by entry.</summary>
    /// <remarks>Inactive rows are checked too, and every problem is reported, not only the first. Title,
    /// description and grain must be non-null but may be empty.</remarks>
    /// <param name="sources">The list rows, as returned.</param>
    /// <returns>Every <c>InvalidCatalog</c> failure found, with its path under <c>$.sources</c>; empty when the list
    /// is usable.</returns>
    public static IReadOnlyList<CatalogFailure> ValidateList(IReadOnlyList<SourceListRow?>? sources)
    {
        var failures = new List<CatalogFailure>();
        if (sources is null)
        {
            failures.Add(Failure("$.sources", "Return a non-null source list."));
            return failures.AsReadOnly();
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < sources.Count; index++)
        {
            var path = "$.sources[" + index.ToString(CultureInfo.InvariantCulture) + "]";
            var row = sources[index];
            if (row is null)
            {
                failures.Add(Failure(path, "Replace the null source entry with a source row."));
                continue;
            }

            var sourceName = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name;
            if (string.IsNullOrWhiteSpace(row.Name))
                failures.Add(Failure(path + ".name", "Set name to a non-blank value.", sourceName));
            else if (!StringComparer.Ordinal.Equals(row.Name, row.Name.Trim()))
                failures.Add(Failure(path + ".name", "Remove surrounding whitespace from name.", sourceName));

            if (row.Name is not null && !names.Add(row.Name))
                failures.Add(Failure(path + ".name", "Use a source name that is unique ignoring case.", sourceName));

            CheckRowCap(failures, path + ".rowCap", row.RowCap, sourceName);
            if (row.Title is null)
                failures.Add(Failure(path + ".title", "Set title to a non-null value.", sourceName));
            if (row.Description is null)
                failures.Add(Failure(path + ".description", "Set description to a non-null value.", sourceName));
            if (row.Grain is null)
                failures.Add(Failure(path + ".grain", "Set grain to a non-null value.", sourceName));
        }

        return failures.AsReadOnly();
    }

    /// <summary>Checks one source's detail as a catalog reader returned it for <paramref name="requestedName"/>.</summary>
    /// <remarks>A null detail is not a fault here: it means the source does not exist, which the caller reports
    /// as an unknown source. The query timeout is checked only here, because list rows do not carry it.</remarks>
    /// <param name="requestedName">The source name the caller asked for.</param>
    /// <param name="detail">The detail, or null when the reader found none.</param>
    /// <returns>Every <c>InvalidCatalog</c> failure found, with its path under <c>$.detail</c>; empty when the
    /// detail is usable or absent.</returns>
    public static IReadOnlyList<CatalogFailure> ValidateDetail(string requestedName, CatalogDetail? detail)
    {
        ArgumentNullException.ThrowIfNull(requestedName);
        var failures = new List<CatalogFailure>();
        if (detail is null)
            return failures.AsReadOnly();

        if (string.IsNullOrWhiteSpace(detail.Provider))
            failures.Add(Failure("$.detail.provider", "Set provider to a non-blank value.", requestedName));

        var rows = detail.Rows;
        if (rows is null)
        {
            failures.Add(Failure("$.detail.rows", "Return non-null rows for this detail.", requestedName));
            return failures.AsReadOnly();
        }

        var source = rows.Source;
        if (source is null)
            failures.Add(Failure("$.detail.rows.source", "Return a non-null source row for this detail.", requestedName));
        else
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(source.Name, requestedName))
                failures.Add(Failure("$.detail.rows.source.name",
                    "Return the requested source name, matching it ignoring case.", requestedName));
            if (string.IsNullOrWhiteSpace(source.ConnectionString))
                failures.Add(Failure("$.detail.rows.source.connectionString",
                    "Set connectionString to a non-blank value.", requestedName));
            if (string.IsNullOrWhiteSpace(source.ObjectName))
                failures.Add(Failure("$.detail.rows.source.objectName",
                    "Set objectName to a non-blank value.", requestedName));
            CheckRowCap(failures, "$.detail.rows.source.rowCap", source.RowCap, requestedName);
            if (source.QueryTimeoutSeconds is < 1 or > 86400)
                failures.Add(InvalidQueryTimeoutException.Failure(requestedName));
            // The same non-null checks as ValidateList, so list and describe agree about one source.
            if (source.Title is null)
                failures.Add(Failure("$.detail.rows.source.title", "Set title to a non-null value.", requestedName));
            if (source.Description is null)
                failures.Add(Failure("$.detail.rows.source.description", "Set description to a non-null value.", requestedName));
            if (source.Grain is null)
                failures.Add(Failure("$.detail.rows.source.grain", "Set grain to a non-null value.", requestedName));
        }

        for (var index = 0; index < rows.Columns.Count; index++)
        {
            var path = "$.detail.rows.columns[" + index.ToString(CultureInfo.InvariantCulture) + "]";
            var column = rows.Columns[index];
            if (column is null)
                failures.Add(Failure(path, "Replace the null column entry with a column row.", requestedName));
            else if (column.ColumnName is null)
                failures.Add(Failure(path + ".columnName", "Set columnName to a non-null value.", requestedName));
        }

        return failures.AsReadOnly();
    }

    // The ceiling has one home (QueryLimits.MaxRowCap); the correction is built from it, never restated.
    /// <summary>The correction for a row cap out of range.</summary>
    private static readonly string RowCapCorrection =
        "Set rowCap to an integer from 1 through " + QueryLimits.MaxRowCap.ToString(CultureInfo.InvariantCulture) + ".";

    /// <summary>Adds a failure when a row cap is outside 1 through <see cref="QueryLimits.MaxRowCap"/>.</summary>
    /// <param name="failures">The list to add to.</param>
    /// <param name="path">The JSON path of the row cap.</param>
    /// <param name="rowCap">The row cap to check.</param>
    /// <param name="sourceName">The source the row belongs to, for the message; null when unknown.</param>
    private static void CheckRowCap(List<CatalogFailure> failures, string path, int rowCap, string? sourceName)
    {
        if (rowCap < 1 || rowCap > QueryLimits.MaxRowCap)
            failures.Add(Failure(path, RowCapCorrection, sourceName));
    }

    /// <summary>An <c>InvalidCatalog</c> failure, its message prefixed with the source it concerns when known.</summary>
    /// <param name="path">The JSON path at fault.</param>
    /// <param name="instruction">What to correct.</param>
    /// <param name="sourceName">The source the fault belongs to; null when the row has no usable name.</param>
    /// <returns>The failure.</returns>
    private static CatalogFailure Failure(string path, string instruction, string? sourceName = null) =>
        new("InvalidCatalog", path, sourceName is null ? instruction : $"Source '{sourceName}'. {instruction}")
        {
            SourceName = sourceName
        };
}
