using System.Collections.ObjectModel;

namespace DataGateway.Core.Queries;

/// <summary>Adapter-produced SQL, named bindings, row facts and public output metadata. Server-side only.</summary>
/// <remarks>The constructor copies the bindings and columns, so later changes to the collections passed in do not
/// reach it.</remarks>
public sealed class CompiledQuery
{
    /// <summary>The name of the private column an ungrouped aggregate adds to count the rows it matched.</summary>
    /// <remarks>An aggregate without <c>group_by</c> always returns one row, even when no row matched; this count
    /// tells the two apart so the answer can say nothing was found. The column is removed before rows reach a
    /// caller.</remarks>
    public const string MatchedRowCountColumn = "__dg_matched_rows";

    /// <summary>Creates a compiled query.</summary>
    /// <param name="sql">The SQL to run.</param>
    /// <param name="parameters">The named parameter values the SQL refers to; copied.</param>
    /// <param name="keep">The most rows the answer keeps; at least 1.</param>
    /// <param name="hasMatchedRowCount">Whether each row carries <see cref="MatchedRowCountColumn"/>.</param>
    /// <param name="columns">The output columns the caller sees; copied.</param>
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

    /// <summary>The most rows the answer keeps; the SQL asks for one more, to tell whether rows were left out.</summary>
    public int Keep { get; }
    /// <summary>The SQL to run.</summary>
    public string Sql { get; }
    /// <summary>The named parameter values, keyed by name compared ordinally.</summary>
    public IReadOnlyDictionary<string, object> Parameters { get; }
    /// <summary>Whether each row carries <see cref="MatchedRowCountColumn"/>.</summary>
    public bool HasMatchedRowCount { get; }
    /// <summary>The output columns the caller sees, in order.</summary>
    public IReadOnlyList<OutputColumn> Columns { get; }
}
