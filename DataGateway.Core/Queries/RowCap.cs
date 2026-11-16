using DataGateway.Core.Catalog;
using SqlKata.JsonQuery;

namespace DataGateway.Core.Queries;

public static class RowCap
{
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
