using DataGateway.Core.Catalog;
using SqlKata;
using SqlKata.JsonQuery;

namespace DataGateway.Core.Queries;

/// <summary>Pure validation and row planning over a cataloged view.</summary>
public static class QueryBuilding
{
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

    private static global::SqlKata.Query Seed(SourceContract contract) => new(contract.ObjectName);

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

    private static InvalidOperationException UnmappedSelect() =>
        new("SqlKata.JsonQuery produced a select clause DataGateway does not map; check the library version.");
}
