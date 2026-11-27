using SqlKata;
using SqlKata.Compilers;

namespace DataGateway.Data.SqlServer;

/// <summary>SqlKata's SQL Server compiler, recording the column of every filter value it binds and padding IN lists.
/// </summary>
/// <remarks>
/// <para>Recording: each condition kind a JSON query produces (comparison, LIKE, IN, BETWEEN) is compiled by the base,
/// and every binding it added in the meantime belongs to that condition's column. Nested and/or groups compile through
/// the same methods, so their values are recorded too. A condition kind not overridden here adds no recorded binding,
/// and its values bind without a store type.</para>
/// <para>Padding: an IN or NOT IN list is padded to Entity Framework 10's bucket sizes by repeating its last value, so
/// lists of nearby lengths share one plan instead of each length compiling its own. Repeating a member changes neither
/// IN nor NOT IN.</para>
/// <para>Padding never costs a query that would run unpadded: SQL Server takes at most 2100 parameters in one request,
/// so a list is left as it is when padding it would bring the query's parameters, every one counted (TOP included),
/// above <see cref="MaxPaddedParameters"/>. The count comes from a first, unpadded compile; when no list needs padding
/// that compile is the result, so only a query with a list to pad compiles twice. Lists are padded in the order they
/// compile, each while the total still allows it.</para>
/// <para>One instance compiles one query: the engine creates a compiler per compile.</para>
/// </remarks>
internal sealed class SqlServerColumnCompiler : SqlServerCompiler, IColumnBindingCompiler
{
    /// <summary>The most parameters padding may bring a query to: SQL Server's 2100 per request, less two kept spare.</summary>
    internal const int MaxPaddedParameters = 2098;

    private readonly List<(SqlResult Context, int Index, object Value, string Column)> recorded = [];

    // Null during the counting compile, which pads nothing; then how many parameters padding may still add.
    private int? paddingBudget;
    private bool wantsPadding;

    public SqlServerColumnCompiler() => UseLegacyPagination = true;

    public override SqlResult Compile(Query query)
    {
        recorded.Clear();
        paddingBudget = null;
        wantsPadding = false;
        var unpadded = base.Compile(query);
        if (!wantsPadding) return unpadded;
        recorded.Clear();
        paddingBudget = MaxPaddedParameters - unpadded.Bindings.Count;
        return base.Compile(query);
    }

    /// <inheritdoc/>
    /// <remarks>Each recorded binding must still be the same object at the same index of the same result: a SqlKata
    /// version that moves or copies bindings after a condition compiles would otherwise attach a store type to another
    /// value, so that case throws instead.</remarks>
    public IReadOnlyDictionary<string, string> BindingColumns(SqlResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.NamedBindings.Count != result.Bindings.Count) throw Moved();
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (context, index, value, column) in recorded)
        {
            if (!ReferenceEquals(context, result) || index >= result.Bindings.Count ||
                !ReferenceEquals(result.Bindings[index], value))
                throw Moved();
            columns.Add(parameterPrefix + index, column);
        }
        return columns;
    }

    /// <summary>EF Core 10's IN-list bucket size for <paramref name="count"/> values: up to 5 as they are, up to 150 rounded
    /// up to a multiple of 10, up to 750 to a multiple of 50, beyond that to a multiple of 100.</summary>
    internal static int PaddedCount(int count) => count switch
    {
        <= 5 => count,
        <= 150 => RoundUp(count, 10),
        <= 750 => RoundUp(count, 50),
        _ => RoundUp(count, 100)
    };

    protected override string CompileBasicCondition(SqlResult ctx, BasicCondition x) =>
        Recorded(ctx, x.Column, () => base.CompileBasicCondition(ctx, x));

    protected override string CompileBasicStringCondition(SqlResult ctx, BasicStringCondition x) =>
        Recorded(ctx, x.Column, () => base.CompileBasicStringCondition(ctx, x));

    protected override string CompileBetweenCondition<T>(SqlResult ctx, BetweenCondition<T> item) =>
        Recorded(ctx, item.Column, () => base.CompileBetweenCondition(ctx, item));

    protected override string CompileInCondition<T>(SqlResult ctx, InCondition<T> item)
    {
        var padded = Padded(item);
        return Recorded(ctx, item.Column, () => base.CompileInCondition(ctx, padded));
    }

    // A copy, so the caller's condition (and its list) is never changed.
    private InCondition<T> Padded<T>(InCondition<T> item)
    {
        var values = item.Values.ToList();
        var extra = PaddedCount(values.Count) - values.Count;
        if (extra == 0) return item;
        wantsPadding = true;
        if (paddingBudget is not { } budget || extra > budget) return item;
        paddingBudget = budget - extra;
        var copy = (InCondition<T>)item.Clone();
        copy.Values = values.Concat(Enumerable.Repeat(values[^1], extra)).ToList();
        return copy;
    }

    private string Recorded(SqlResult ctx, string column, Func<string> compile)
    {
        var first = ctx.Bindings.Count;
        var sql = compile();
        for (var index = first; index < ctx.Bindings.Count; index++)
            recorded.Add((ctx, index, ctx.Bindings[index], column));
        return sql;
    }

    private static int RoundUp(int count, int step) => (count + step - 1) / step * step;

    private static InvalidOperationException Moved() =>
        new("SqlKata moved a filter value after compiling its condition; check the SqlKata version.");
}
