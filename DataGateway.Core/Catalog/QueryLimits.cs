using SqlKata.JsonQuery;

namespace DataGateway.Core.Catalog;

/// <summary>The library limits shared by describe (schema) and query (validator and row cap).</summary>
/// <remarks>Describe and query must read the same limits, or describe would advertise a maximum the validator then
/// refuses. In the limits, <c>MaxLimit</c> holds the source's row cap.</remarks>
public static class QueryLimits
{
    /// <summary>The largest row cap: a query without a limit asks its source for the cap plus one row.</summary>
    public const int MaxRowCap = int.MaxValue - 1;

    /// <summary>The query library's default limits with the given row cap.</summary>
    /// <param name="rowCap">The source's row cap, at most <see cref="MaxRowCap"/>.</param>
    /// <returns>The limits.</returns>
    public static JsonQueryLimits ForRowCap(int rowCap) => Create(rowCap: rowCap,
        maxJsonLength: JsonQueryLimits.Default.MaxJsonLength,
        maxFilterDepth: JsonQueryLimits.Default.MaxFilterDepth,
        maxFilterConditions: JsonQueryLimits.Default.MaxFilterConditions,
        maxInValues: JsonQueryLimits.Default.MaxInValues);

    /// <summary>The configured limits describe and query share; the row cap is the largest requested limit.</summary>
    /// <param name="rowCap">The source's row cap, at most <see cref="MaxRowCap"/>.</param>
    /// <param name="maxJsonLength">The longest query text accepted.</param>
    /// <param name="maxFilterDepth">The deepest nesting of filter groups; 0 allows no groups.</param>
    /// <param name="maxFilterConditions">The most filter conditions in one query.</param>
    /// <param name="maxInValues">The most values in one <c>in</c> or <c>not_in</c> list.</param>
    /// <returns>The limits.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowCap"/> is above
    /// <see cref="MaxRowCap"/>.</exception>
    public static JsonQueryLimits Create(int rowCap, int maxJsonLength, int maxFilterDepth,
        int maxFilterConditions, int maxInValues)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rowCap, MaxRowCap);
        return JsonQueryLimits.Default with
        {
            MaxLimit = rowCap,
            MaxJsonLength = maxJsonLength,
            MaxFilterDepth = maxFilterDepth,
            MaxFilterConditions = maxFilterConditions,
            MaxInValues = maxInValues
        };
    }
}
