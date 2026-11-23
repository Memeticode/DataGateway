using DataGateway.Core.Catalog;
using SqlKata;
using SqlKata.JsonQuery;

namespace DataGateway.Core.Queries;

/// <summary>Pure validation and row planning over a cataloged view.</summary>
/// <remarks>The query library does the validating; this type seeds it with the source's view and columns, turns
/// its refusals into failures with fix-it text (<see cref="RefusalMessage"/>), and works out the row plan and the
/// output columns of an accepted query.</remarks>
public static class QueryBuilding
{
    /// <summary>Validates a caller's query text against one source and plans its rows.</summary>
    /// <param name="contract">The source contract: its view and queryable columns.</param>
    /// <param name="query">The raw JSON text of the caller's query.</param>
    /// <param name="limits">The limits the source's queries run under, with its row cap.</param>
    /// <returns>The validated query, or a failure: a library refusal at its path, with step <c>query</c>, or
    /// <c>InvalidCatalog</c> when the source's object name is unusable.</returns>
    public static CatalogResult<ValidatedQuery> Validate(SourceContract contract, string query, JsonQueryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(limits);

        var seed = Seed(contract);
        global::SqlKata.Query composed;
        try
        {
            composed = seed.WithJsonQuery(query, contract.QueryColumns, limits);
        }
        catch (JsonQueryException e)
        {
            return CatalogResult<ValidatedQuery>.Reject(new CatalogFailure(e.Code.ToString(), e.Path,
                RefusalMessage.For(e.Code, e.Path, e.Detail))
                { Step = "query" });
        }
        catch (ArgumentException e) when (e.ParamName == "query")
        {
            return CatalogResult<ValidatedQuery>.Reject(new CatalogFailure("InvalidCatalog", "$.source",
                "Correct the catalog object_name: name the view as schema.view or view, with bare ASCII SQL identifiers.")
                { Step = "seed" });
        }

        int? requested = composed.GetOneComponent<LimitClause>("limit")?.Limit;
        RowLimit rowLimit = RowCap.Plan(requested, limits);
        var catalog = contract.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var columns = composed.GetComponents<AbstractColumn>("select")
            .Select(clause => MapColumn(clause, catalog)).ToArray();
        return CatalogResult<ValidatedQuery>.Success(new ValidatedQuery(composed, rowLimit, columns));
    }

    /// <summary>The starting query over the source's view, before the caller's clauses are added.</summary>
    /// <remarks>The query library refuses an object name that is not a bare <c>schema.view</c> or <c>view</c> with
    /// an <see cref="ArgumentException"/> for parameter <c>query</c>; <see cref="Validate"/> reports that as a catalog
    /// fault, not the caller's.</remarks>
    /// <param name="contract">The source contract.</param>
    /// <returns>The seed query.</returns>
    private static global::SqlKata.Query Seed(SourceContract contract) => new(contract.ObjectName);

    /// <summary>The output column for one select clause of an accepted query.</summary>
    /// <remarks>A plain column keeps its catalog type. An aggregate arrives as <c>col as fn_col</c>; its output takes
    /// the alias, and its type is <c>number</c> for count, sum and avg, or the column's own type for min and max.
    /// Any other shape throws: it means the query library changed in a way this mapping does not know, and a guessed
    /// type would mislabel the answer.</remarks>
    /// <param name="clause">The select clause.</param>
    /// <param name="catalog">The source's columns by name, ignoring case.</param>
    /// <returns>The output column.</returns>
    /// <exception cref="InvalidOperationException">The clause has a shape this mapping does not know.</exception>
    private static OutputColumn MapColumn(AbstractColumn clause, IReadOnlyDictionary<string, ColumnMetadata> catalog)
    {
        if (clause is Column { Name: var name } && catalog.TryGetValue(name, out var detail))
            return new(name, detail.DataType);

        if (clause is AggregatedColumn { Aggregate: var fn, Column: Column { Name: var expression } })
        {
            var split = expression.LastIndexOf(" as ", StringComparison.Ordinal);
            if (split > 0 && split + 4 < expression.Length &&
                catalog.TryGetValue(expression[..split], out var column))
            {
                var dataType = fn switch
                {
                    "count" or "sum" or "avg" => "number",
                    "min" or "max" => column.DataType,
                    _ => throw UnmappedSelect()
                };
                return new(expression[(split + 4)..], dataType);
            }
        }

        throw UnmappedSelect();
    }

    /// <summary>The exception for a select clause this mapping does not know.</summary>
    /// <returns>The exception.</returns>
    private static InvalidOperationException UnmappedSelect() =>
        new("SqlKata.JsonQuery produced a select clause DataGateway does not map; check the library version.");
}
