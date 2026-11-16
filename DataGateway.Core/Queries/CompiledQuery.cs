using System.Collections.ObjectModel;

namespace DataGateway.Core.Queries;

/// <summary>Adapter-produced SQL, named bindings, row facts and public output metadata. Server-side only.</summary>
public sealed class CompiledQuery
{
    public const string MatchedRowCountColumn = "__dg_matched_rows";

    public CompiledQuery(string sql, IReadOnlyDictionary<string, object> parameters, int keep,
        bool hasMatchedRowCount, IReadOnlyList<OutputColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keep);
        Sql = sql;
        Parameters = new ReadOnlyDictionary<string, object>(
            parameters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        Keep = keep;
        HasMatchedRowCount = hasMatchedRowCount;
        Columns = Array.AsReadOnly(columns.ToArray());
    }

    public int Keep { get; }
    public string Sql { get; }
    public IReadOnlyDictionary<string, object> Parameters { get; }
    public bool HasMatchedRowCount { get; }
    public IReadOnlyList<OutputColumn> Columns { get; }
}
