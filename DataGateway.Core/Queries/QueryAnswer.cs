using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataGateway.Core.Catalog;
using SqlKata;
using SqlKata.JsonQuery;

namespace DataGateway.Core.Queries;

/// <summary>The request's source name and exact JSON text of its query member (the library's input, untouched).</summary>
public sealed record QueryRequest(string Source, string Query);

/// <summary>The public answer, with hint last. Private execution facts never appear on the wire.</summary>
public sealed record QueryResponse(string Source, IReadOnlyList<OutputColumn> Columns,
    IReadOnlyList<IDictionary<string, object?>> Rows, int RowCount, bool More)
{
    public const string NothingFound = "Nothing found that matched your query.";
    public const string CompleteSet = "Complete result set.";
    public const string InValuesUnmatched = "All matching rows returned. in values that matched no row are left out: compare the values you asked for with the rows.";
    public const string InValuesHiddenByAggregate = "All matching rows aggregated. in values that matched no row are left out and an aggregate hides them: check the in values first.";
    public const string NoParticularOrder = "Rows are in no particular order.";

    [JsonIgnore]
    public bool NoMatchingRows { get; init; }
    [JsonIgnore]
    public bool Unordered { get; init; }
    [JsonIgnore]
    public bool HasInFilter { get; init; }
    [JsonIgnore]
    public bool HasAggregate { get; init; }

    public static string CappedAt(int rowCount) => "The result set was capped at " + rowCount.ToString(CultureInfo.InvariantCulture) + (rowCount == 1 ? " row." : " rows.");

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
    private static readonly HashSet<string> Refusals = new(Enum.GetNames<JsonQueryErrorCode>(), StringComparer.Ordinal);

    public static CatalogFailure InvalidJson { get; } = new("InvalidJson", "$",
        "The request body is not valid JSON. Send {\"source\": \"<name>\", \"query\": {...}} with a name from list sources.")
        { Step = "request" };

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

    public static bool IsRefusal(string code) => Refusals.Contains(code);

    public static CatalogFailure RootPath(CatalogFailure failure)
    {
        if (!IsRefusal(failure.Code)) return failure;
        var path = failure.Path;
        return failure with { Path = path is "$" or "" ? "$.query" :
            path.StartsWith('[') ? "$.query" + path : "$.query." + path };
    }

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

    private static bool HasInFilter(Query tree) => tree.GetComponents<AbstractCondition>("where").Any(condition =>
        condition is InCondition<object> { IsNot: false } ||
        condition is NestedCondition<Query> nested && HasInFilter(nested.Query));

    private static InvalidOperationException MismatchedRow() => new("The query row does not match its output columns.");

    private static CatalogResult<QueryRequest> InvalidObject() => CatalogResult<QueryRequest>.Reject(new(
        "InvalidStructure", "$",
        "Send a JSON object with exactly two properties, source and query: {\"source\": \"<name>\", \"query\": {...}}. Take the name from list sources and the query vocabulary from describe source.")
        { Step = "request" });
}
