using DataGateway.Core.Catalog;
using SqlKata.JsonQuery;

namespace DataGateway.Core.Queries;

/// <summary>Plans how many rows a validated query keeps: the caller's limit, or else the source's row cap.</summary>
/// <remarks>The query library has already refused a limit outside 1 through the cap, so an out-of-range value here
/// is a bug and throws.</remarks>
public static class RowCap
{
    /// <summary>The row plan for one query.</summary>
    /// <param name="requestedLimit">The caller's <c>limit</c>, or null when none was given.</param>
    /// <param name="limits">The limits the query was validated under, built by <see cref="QueryLimits"/> so they
    /// carry the row cap.</param>
    /// <returns>The plan: keep the limit or the cap, fetch one more.</returns>
    /// <exception cref="ArgumentException"><paramref name="limits"/> carries no row cap.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The cap is above <see cref="QueryLimits.MaxRowCap"/>, or
    /// <paramref name="requestedLimit"/> is outside 1 through the cap.</exception>
    public static RowLimit Plan(int? requestedLimit, JsonQueryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxLimit is not int cap)
            throw new ArgumentException("build the limits with QueryLimits", nameof(limits));
        if (cap > QueryLimits.MaxRowCap)
            throw new ArgumentOutOfRangeException(nameof(limits));
        if (requestedLimit < 1 || requestedLimit > cap)
            throw new ArgumentOutOfRangeException(nameof(requestedLimit));
        return new RowLimit(requestedLimit ?? cap);
    }
}
