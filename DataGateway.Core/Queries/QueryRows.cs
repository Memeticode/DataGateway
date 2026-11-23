using System.Text.Json.Serialization;

namespace DataGateway.Core.Queries;

/// <summary>The rows portion of a query response.</summary>
/// <remarks>Rows are kept, not streamed: an engine reads them inside its connection scope and returns them detached,
/// so they outlive the connection.</remarks>
public sealed class QueryRows
{
    /// <summary>Creates the rows portion.</summary>
    /// <param name="rows">The kept rows, each keyed by output column name.</param>
    /// <param name="more">Whether the source had rows past those kept.</param>
    /// <param name="noMatchingRows">Whether no source row matched, though an ungrouped aggregate still returned
    /// its one row.</param>
    public QueryRows(IReadOnlyList<IDictionary<string, object?>> rows, bool more, bool noMatchingRows = false)
    {
        Rows = rows;
        More = more;
        NoMatchingRows = noMatchingRows;
    }

    /// <summary>The kept rows, each keyed by output column name.</summary>
    public IReadOnlyList<IDictionary<string, object?>> Rows { get; }
    /// <summary>The number of kept rows.</summary>
    public int RowCount => Rows.Count;
    /// <summary>Whether the source had rows past those kept.</summary>
    public bool More { get; }
    /// <summary>Whether no source row matched; read from the private matched-row count
    /// (<see cref="CompiledQuery.MatchedRowCountColumn"/>).</summary>
    [JsonIgnore]
    public bool NoMatchingRows { get; }
}
