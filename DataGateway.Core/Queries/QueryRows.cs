using System.Text.Json.Serialization;

namespace DataGateway.Core.Queries;

/// <summary>The rows portion of a query response.</summary>
public sealed class QueryRows
{
    public QueryRows(IReadOnlyList<IDictionary<string, object?>> rows, bool more, bool noMatchingRows = false)
    {
        Rows = rows;
        More = more;
        NoMatchingRows = noMatchingRows;
    }

    public IReadOnlyList<IDictionary<string, object?>> Rows { get; }
    public int RowCount => Rows.Count;
    public bool More { get; }
    [JsonIgnore]
    public bool NoMatchingRows { get; }
}
