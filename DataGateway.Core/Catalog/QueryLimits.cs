using SqlKata.JsonQuery;

namespace DataGateway.Core.Catalog;

/// <summary>The library limits shared by describe (schema) and query (validator and row cap).</summary>
public static class QueryLimits
{
    /// <summary>The largest row cap: a query without a limit asks its source for the cap plus one row.</summary>
    public const int MaxRowCap = int.MaxValue - 1;

    public static JsonQueryLimits ForRowCap(int rowCap) => Create(rowCap: rowCap,
        maxJsonLength: JsonQueryLimits.Default.MaxJsonLength,
        maxFilterDepth: JsonQueryLimits.Default.MaxFilterDepth,
        maxFilterConditions: JsonQueryLimits.Default.MaxFilterConditions,
        maxInValues: JsonQueryLimits.Default.MaxInValues);

    /// <summary>The configured limits describe and query share; the row cap is the largest requested limit.</summary>
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
