using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataGateway.Core.Catalog;
using SqlKata;
using SqlKata.JsonQuery;

namespace DataGateway.Core.Queries;

/// <summary>The request's source name and exact JSON text of its query member (the library's input, untouched).</summary>
/// <param name="Source">The source name, as sent.</param>
/// <param name="Query">The raw JSON text of the <c>query</c> member.</param>
public sealed record QueryRequest(string Source, string Query);

/// <summary>The public answer, with hint last. Private execution facts never appear on the wire.</summary>
/// <remarks>The <see cref="Hint"/> says what the rows are: capped, empty or complete, with a caution when an
/// <c>in</c> filter can hide values that matched nothing. The init-only flags feed the hint and are never
/// serialized.</remarks>
/// <param name="Source">The source name.</param>
/// <param name="Columns">The output columns, in order.</param>
/// <param name="Rows">The rows, each keyed by output column name.</param>
/// <param name="RowCount">The number of rows returned.</param>
/// <param name="More">Whether rows were left out because of the limit or row cap.</param>
public sealed record QueryResponse(string Source, IReadOnlyList<OutputColumn> Columns,
    IReadOnlyList<IDictionary<string, object?>> Rows, int RowCount, bool More)
{
    /// <summary>The hint when no row matched.</summary>
    public const string NothingFound = "Nothing found that matched your query.";
    /// <summary>The hint when every matching row was returned.</summary>
    public const string CompleteSet = "Complete result set.";
    /// <summary>The hint for a complete set filtered with <c>in</c>: values that matched nothing are silently
    /// absent.</summary>
    public const string InValuesUnmatched = "All matching rows returned. in values that matched no row are left out: compare the values you asked for with the rows.";
    /// <summary>The hint for a complete aggregate filtered with <c>in</c>, where unmatched values cannot be seen
    /// at all.</summary>
    public const string InValuesHiddenByAggregate = "All matching rows aggregated. in values that matched no row are left out and an aggregate hides them: check the in values first.";
    /// <summary>Added to a capped hint when the query had no <c>order_by</c>: which rows came back is not
    /// defined.</summary>
    public const string NoParticularOrder = "Rows are in no particular order.";

    /// <summary>Whether no source row matched, even if an ungrouped aggregate still returned its one row.</summary>
    [JsonIgnore]
    public bool NoMatchingRows { get; init; }
    /// <summary>Whether the query had no <c>order_by</c>.</summary>
    [JsonIgnore]
    public bool Unordered { get; init; }
    /// <summary>Whether the query filters with <c>in</c>, at any depth.</summary>
    [JsonIgnore]
    public bool HasInFilter { get; init; }
    /// <summary>Whether the query selects an aggregate.</summary>
    [JsonIgnore]
    public bool HasAggregate { get; init; }

    /// <summary>The hint for a capped set, with <c>1 row</c> in the singular.</summary>
    /// <param name="rowCount">The number of rows returned.</param>
    /// <returns>The hint text.</returns>
    public static string CappedAt(int rowCount) => "The result set was capped at " + rowCount.ToString(CultureInfo.InvariantCulture) + (rowCount == 1 ? " row." : " rows.");

    /// <summary>The one-line fact about the rows: capped, nothing found, or complete.</summary>
    /// <remarks>Nothing found is judged by rows matched, not rows returned, so an aggregate over no matching rows
    /// is not called complete. Serialized last.</remarks>
    [JsonPropertyOrder(1)]
    public string Hint => More
        ? CappedAt(RowCount) + (Unordered ? " " + NoParticularOrder : "")
        : RowCount == 0 || NoMatchingRows ? NothingFound
        : HasInFilter ? (HasAggregate ? InValuesHiddenByAggregate : InValuesUnmatched) : CompleteSet;
}

/// <summary>
/// Pure query bricks: request body to source and query text; library refusal to rooted path; rows to answer.
/// Values retain their CLR representation except midnight DateTime values, rendered as invariant yyyy-MM-dd.
/// Catalog date columns are SQL date. The library accepts date filters only as yyyy-MM-dd, so returned
/// dates can be pasted into a follow-up filter without losing their value. The rejected alternative,
/// default System.Text.Json midnight output such as 2024-12-31T00:00:00, would be refused with InvalidValue.
/// Non-midnight DateTime values keep System.Text.Json's default ISO representation; other values are unchanged.
/// </summary>
public static class QueryAnswer
{
    /// <summary>Every refusal code the query library can raise.</summary>
    private static readonly HashSet<string> Refusals = new(Enum.GetNames<JsonQueryErrorCode>(), StringComparer.Ordinal);

    /// <summary>The refusal for a query body that is not valid JSON.</summary>
    public static CatalogFailure InvalidJson { get; } = new("InvalidJson", "$",
        "The request body is not valid JSON. Send {\"source\": \"<name>\", \"query\": {...}} with a name from list sources.")
        { Step = "request" };

    /// <summary>Reads the source name and the raw query text from a query body, which must have exactly the two
    /// members <c>source</c> and <c>query</c>.</summary>
    /// <remarks>The query member is kept as raw JSON text, not parsed here, so the query library validates exactly
    /// what the caller sent.</remarks>
    /// <param name="body">The parsed request body.</param>
    /// <returns>The request, or an <c>InvalidStructure</c> or <c>InvalidJson</c> refusal.</returns>
    public static CatalogResult<QueryRequest> ParseRequest(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return InvalidObject();
        var count = 0;
        var sources = 0;
        var queries = 0;
        JsonElement source = default, query = default;
        foreach (var property in body.EnumerateObject())
        {
            count++;
            if (property.NameEquals("source")) { sources++; source = property.Value; }
            else if (property.NameEquals("query")) { queries++; query = property.Value; }
        }
        if (count != 2 || sources != 1 || queries != 1) return InvalidObject();
        if (source.ValueKind != JsonValueKind.String)
            return CatalogResult<QueryRequest>.Reject(new("InvalidStructure", "$.source",
                "source must be a JSON string: the name of a source from list sources.") { Step = "request" });
        try { return CatalogResult<QueryRequest>.Success(new(source.GetString()!, query.GetRawText())); }
        catch (InvalidOperationException) { return CatalogResult<QueryRequest>.Reject(InvalidJson); }
    }

    /// <summary>Whether a code is one of the query library's refusal codes.</summary>
    /// <param name="code">The failure code, compared ordinally.</param>
    /// <returns><see langword="true"/> for a library refusal.</returns>
    public static bool IsRefusal(string code) => Refusals.Contains(code);

    /// <summary>Roots a library refusal's path under <c>$.query</c>, so it points into the request body.</summary>
    /// <remarks>The library reports paths relative to the query member; any other failure is returned
    /// unchanged.</remarks>
    /// <param name="failure">The failure.</param>
    /// <returns>The failure with its path rooted, or <paramref name="failure"/> itself.</returns>
    public static CatalogFailure RootPath(CatalogFailure failure)
    {
        if (!IsRefusal(failure.Code)) return failure;
        var path = failure.Path;
        return failure with { Path = path is "$" or "" ? "$.query" :
            path.StartsWith('[') ? "$.query" + path : "$.query." + path };
    }

    /// <summary>Builds the public answer from a query's rows.</summary>
    /// <remarks>Rewrites midnight <see cref="DateTime"/> cells in place, as the type's summary explains. The hint's
    /// facts are read from a clone of the validated query.</remarks>
    /// <param name="contract">The source contract.</param>
    /// <param name="query">The validated query the rows answer.</param>
    /// <param name="rows">The rows read from the source.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="InvalidOperationException">The rows' columns do not match the query's output columns: a
    /// bug, not a caller error.</exception>
    public static QueryResponse Map(SourceContract contract, ValidatedQuery query, QueryRows rows)
    {
        if (rows.RowCount > 0 && !rows.Rows[0].Keys.SequenceEqual(query.Columns.Select(c => c.Name), StringComparer.Ordinal))
            throw MismatchedRow(); // One result set has one shape, so its first row speaks for all.
        foreach (var row in rows.Rows)
            foreach (var column in query.Columns)
                if (row[column.Name] is DateTime date && date.TimeOfDay == TimeSpan.Zero)
                    row[column.Name] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var tree = query.CloneQuery();
        return new(contract.Source.Name, query.Columns, rows.Rows, rows.RowCount, rows.More)
        {
            NoMatchingRows = rows.NoMatchingRows,
            Unordered = !tree.HasComponent("order"),
            HasInFilter = HasInFilter(tree),
            HasAggregate = tree.GetComponents<AbstractColumn>("select").Any(column => column is AggregatedColumn)
        };
    }

    /// <summary>Whether a query tree filters with <c>in</c> (not <c>not_in</c>), including inside nested
    /// groups.</summary>
    /// <param name="tree">The query tree.</param>
    /// <returns><see langword="true"/> when an <c>in</c> condition is present.</returns>
    private static bool HasInFilter(Query tree) => tree.GetComponents<AbstractCondition>("where").Any(condition =>
        condition is InCondition<object> { IsNot: false } ||
        condition is NestedCondition<Query> nested && HasInFilter(nested.Query));

    /// <summary>The exception for rows whose columns differ from the query's output columns.</summary>
    /// <returns>The exception.</returns>
    private static InvalidOperationException MismatchedRow() => new("The query row does not match its output columns.");

    /// <summary>The refusal for a body that is not an object with exactly the members <c>source</c> and
    /// <c>query</c>.</summary>
    /// <returns>The <c>InvalidStructure</c> refusal at the root.</returns>
    private static CatalogResult<QueryRequest> InvalidObject() => CatalogResult<QueryRequest>.Reject(new(
        "InvalidStructure", "$",
        "Send a JSON object with exactly two properties, source and query: {\"source\": \"<name>\", \"query\": {...}}. Take the name from list sources and the query vocabulary from describe source.")
        { Step = "request" });
}
