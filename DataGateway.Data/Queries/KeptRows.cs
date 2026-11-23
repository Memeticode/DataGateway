using System.Runtime.ExceptionServices;
using DataGateway.Core.Queries;

namespace DataGateway.Data.Queries;

/// <summary>The one place a query's rows are kept: at most Keep of them; one row more only tells that more exist.</summary>
internal static class KeptRows
{
    // expired is asked once the rows are kept, before cleanup: true when the query's deadline passed while a provider
    // finished a read without honouring the cancelled token. The late rows are then refused, not returned.
    /// <summary>Reads rows until <see cref="CompiledQuery.Keep"/> are kept and one more shows that rows were left
    /// out, or until the rows end.</summary>
    /// <remarks>Reading stops at the extra row, so the rest is never read. For an ungrouped aggregate, the private
    /// matched-row count is taken out of the row and becomes <see cref="QueryRows.NoMatchingRows"/>.</remarks>
    /// <param name="source">The source rows, enumerated once.</param>
    /// <param name="query">The compiled query: its keep count and whether rows carry the matched-row count.</param>
    /// <param name="token">Cancels the reads.</param>
    /// <param name="expired">Asked once the rows are kept: true when the query's deadline has passed; null to skip
    /// the check.</param>
    /// <returns>The kept rows.</returns>
    /// <exception cref="GatewayInvariantException">A row lacks a valid matched-row count.</exception>
    /// <exception cref="TimeoutException">The deadline passed while the rows were read.</exception>
    internal static async Task<QueryRows> KeepAsync(
        IAsyncEnumerable<IDictionary<string, object?>> source, CompiledQuery query, CancellationToken token,
        Func<bool>? expired = null)
    {
        var rows = new List<IDictionary<string, object?>>();
        var noMatchingRows = false;
        var reader = source.GetAsyncEnumerator(token);
        ExceptionDispatchInfo? failure = null;
        try
        {
            while (await reader.MoveNextAsync().ConfigureAwait(false))
            {
                if (rows.Count == query.Keep)
                    return Kept(new QueryRows(rows, more: true, noMatchingRows: noMatchingRows), expired);
                var values = reader.Current;
                if (query.HasMatchedRowCount)
                {
                    if (!values.TryGetValue(CompiledQuery.MatchedRowCountColumn, out var value) ||
                        value is not long matched || matched < 0)
                        throw new GatewayInvariantException("The query row is missing its matched-row count.");
                    noMatchingRows = matched == 0;
                    values.Remove(CompiledQuery.MatchedRowCountColumn);
                }
                rows.Add(values);
            }
            return Kept(new QueryRows(rows, more: false, noMatchingRows: noMatchingRows), expired);
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error);
            throw;
        }
        finally
        {
            try { await reader.DisposeAsync().ConfigureAwait(false); }
            finally { failure?.Throw(); }
        }
    }

    /// <summary>Returns the rows, unless the deadline passed while they were read.</summary>
    /// <param name="rows">The kept rows.</param>
    /// <param name="expired">The deadline check; null to skip it.</param>
    /// <returns><paramref name="rows"/>.</returns>
    /// <exception cref="TimeoutException">The deadline has passed.</exception>
    private static QueryRows Kept(QueryRows rows, Func<bool>? expired) =>
        expired?.Invoke() == true ? throw new TimeoutException("The query's deadline passed while its rows were read.") : rows;
}
