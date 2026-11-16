namespace DataGateway.Core.Queries;

/// <summary>A caller query accepted by SqlKata.JsonQuery over the contract's view, with its row plan and output columns.</summary>
public sealed class ValidatedQuery
{
    internal ValidatedQuery(global::SqlKata.Query query, RowLimit rowLimit, OutputColumn[] columns)
    {
        Query = query;
        RowLimit = rowLimit;
        Columns = Array.AsReadOnly(columns);
    }

    private global::SqlKata.Query Query { get; }

    /// <summary>Returns an independent tree for an adapter to compile or modify.</summary>
    public global::SqlKata.Query CloneQuery() => Detach(Query.Clone(), null);

    private static global::SqlKata.Query Detach(global::SqlKata.Query copy, global::SqlKata.AbstractQuery? parent)
    {
        copy.Parent = parent;
        // Validated JSON queries have no includes or variables. Clone their containers too.
        copy.Includes = [.. copy.Includes];
        copy.Variables = new(copy.Variables);
        foreach (var nested in copy.GetComponents<global::SqlKata.AbstractCondition>("where")
                     .OfType<global::SqlKata.NestedCondition<global::SqlKata.Query>>())
            Detach(nested.Query, copy);
        return copy;
    }
    public RowLimit RowLimit { get; }
    public IReadOnlyList<OutputColumn> Columns { get; }
}
