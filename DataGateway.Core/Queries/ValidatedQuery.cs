namespace DataGateway.Core.Queries;

/// <summary>A caller query accepted by SqlKata.JsonQuery over the contract's view, with its row plan and output columns.</summary>
/// <remarks>The constructor is internal: only <see cref="QueryBuilding.Validate"/> builds one. The query tree is
/// private and handed out only as a clone, so no adapter can change what was validated.</remarks>
public sealed class ValidatedQuery
{
    /// <summary>Wraps an accepted query tree with its plan and output columns.</summary>
    /// <param name="query">The accepted query tree.</param>
    /// <param name="rowLimit">The row plan.</param>
    /// <param name="columns">The output columns.</param>
    internal ValidatedQuery(global::SqlKata.Query query, RowLimit rowLimit, OutputColumn[] columns)
    {
        Query = query;
        RowLimit = rowLimit;
        Columns = Array.AsReadOnly(columns);
    }

    /// <summary>The accepted query tree; never handed out.</summary>
    private global::SqlKata.Query Query { get; }

    /// <summary>Returns an independent tree for an adapter to compile or modify.</summary>
    /// <returns>A fresh clone, with no parent.</returns>
    public global::SqlKata.Query CloneQuery() => Detach(Query.Clone(), null);

    /// <summary>Cuts a clone loose from the original tree: its parent link and shared containers.</summary>
    /// <remarks>A plain SqlKata clone still shares its include and variable containers with the original, and its
    /// nested groups keep the original as their parent. Detaching gives the copy its own, so an engine that changes
    /// its copy (a limit, rewritten LIKE patterns) cannot reach the validated tree or another clone.</remarks>
    /// <param name="copy">The clone to detach.</param>
    /// <param name="parent">The query the clone belongs under; null for the root.</param>
    /// <returns><paramref name="copy"/>, detached.</returns>
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
    /// <summary>The row plan: how many rows to keep and fetch.</summary>
    public RowLimit RowLimit { get; }
    /// <summary>The output columns the caller sees, in order.</summary>
    public IReadOnlyList<OutputColumn> Columns { get; }
}
