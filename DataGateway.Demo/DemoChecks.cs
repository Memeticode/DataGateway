using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataGateway.Demo;

/// <summary>One HTTP answer, as the checks see it.</summary>
/// <param name="Status">The HTTP status code.</param>
/// <param name="ContentType">The <c>Content-Type</c> header, or null when absent.</param>
/// <param name="Challenge">The <c>WWW-Authenticate</c> values joined with commas; empty when there are none.</param>
/// <param name="Body">The body text.</param>
public sealed record DemoAnswer(int Status, string? ContentType, string? Challenge, string Body);

/// <summary>The demo's checks of each answer against the documented wire format.</summary>
/// <remarks>Each check returns a list of problems rather than throwing, so one bad answer is reported in full and
/// the run goes on. Member names and their order are checked exactly, because the interface promises both.</remarks>
public static class DemoChecks
{
    /// <summary>The members of a source in list and describe answers, in order.</summary>
    private static readonly string[] SourceMembers = ["name", "title", "description", "grain", "rowCap"];
    /// <summary>The members of a column in a describe answer, in order; the last five are the capability flags.</summary>
    private static readonly string[] ColumnMembers = ["name", "title", "description", "dataType", "unit", "magnitude", "displayFormat", "canSelect", "canFilter", "canSort", "canGroup", "canAggregate"];
    /// <summary>The exact body of the anonymous challenge: three members and no incident id, since an anonymous
    /// challenge writes no failure line for an id to name.</summary>
    public const string ChallengeBody = """{"code":"NotAuthenticated","path":"$","message":"Sign in with Windows to call DataGateway: send the request with Windows credentials (HTTP Negotiate)."}""";
    /// <summary>The exact message of the refusal to group by a column whose <c>canGroup</c> is false.</summary>
    public const string GroupMessage = "The column at path cannot be grouped. Group by a column whose canGroup is true.";

    /// <summary>The checks every answer shares (status, content type, a JSON object body), then the answer's own
    /// check.</summary>
    /// <remarks>A missing member, a wrong JSON type or a bad number inside <paramref name="check"/> becomes a
    /// problem instead of an exception.</remarks>
    /// <param name="answer">The answer.</param>
    /// <param name="status">The expected status.</param>
    /// <param name="check">The answer's own check, given the body and the problem list.</param>
    /// <returns>Every problem found; empty when the answer passed.</returns>
    private static IReadOnlyList<string> Check(DemoAnswer answer, int status, Action<JsonElement, List<string>> check)
    {
        var problems = new List<string>();
        if (answer.Status != status) problems.Add($"status: expected {status}, got {answer.Status}");
        if (answer.ContentType != "application/json; charset=utf-8") problems.Add("Content-Type: expected application/json; charset=utf-8");
        try
        {
            using var document = JsonDocument.Parse(answer.Body);
            var body = document.RootElement;
            if (body.ValueKind != JsonValueKind.Object) problems.Add("body must be a JSON object");
            else check(body, problems);
        }
        catch (JsonException) { problems.Add("body is not valid JSON"); }
        catch (KeyNotFoundException error) { problems.Add("missing member: " + error.Message); }
        catch (InvalidOperationException error) { problems.Add("member has wrong JSON type: " + error.Message); }
        catch (FormatException) { problems.Add("member has invalid numeric value"); }
        catch (OverflowException) { problems.Add("member numeric value is out of range"); }
        return problems;
    }

    /// <summary>Requires exactly these member names, in this order.</summary>
    private static void Members(JsonElement body, IEnumerable<string> expected, List<string> problems)
    {
        var names = expected.ToArray();
        if (body.ValueKind != JsonValueKind.Object || !body.EnumerateObject().Select(p => p.Name).SequenceEqual(names))
            problems.Add("members: expected " + string.Join(", ", names) + " in that order");
    }
    /// <summary>Adds <paramref name="problem"/> when <paramref name="condition"/> is false.</summary>
    private static void Require(bool condition, string problem, List<string> problems) { if (!condition) problems.Add(problem); }
    /// <summary>Checks one source object: its members, four strings, and a row cap from 1 to 2147483646.</summary>
    private static void Source(JsonElement body, List<string> problems)
    {
        Members(body, SourceMembers, problems);
        foreach (var name in SourceMembers.Take(4)) Require(body.GetProperty(name).ValueKind == JsonValueKind.String, name + " must be a string", problems);
        var cap = body.GetProperty("rowCap").GetInt32();
        Require(cap >= 1 && cap <= int.MaxValue - 1, "rowCap must be an integer from 1 to 2147483646", problems);
    }
    /// <summary>Checks a list answer: at least one source, each one well formed.</summary>
    /// <remarks>An empty list is a problem, with the setup step that fixes it, because the rest of the run needs a
    /// source.</remarks>
    public static IReadOnlyList<string> List(DemoAnswer answer) => Check(answer, 200, (body, problems) =>
    {
        Members(body, ["sources"], problems);
        var sources = body.GetProperty("sources");
        Require(sources.GetArrayLength() > 0, "sources: register a view: README, Get started, step 4", problems);
        foreach (var source in sources.EnumerateArray()) Source(source, problems);
    });

    /// <summary>Checks a describe answer: the source, its columns with their types and flags, and a query limit
    /// equal to the row cap.</summary>
    /// <param name="answer">The answer.</param>
    /// <param name="requested">The source name sent; the answer must carry the same spelling.</param>
    public static IReadOnlyList<string> Describe(DemoAnswer answer, string requested) => Check(answer, 200, (body, problems) =>
    {
        Members(body, ["source", "querySchema"], problems);
        Source(body.GetProperty("source"), problems);
        Require(body.GetProperty("source").GetProperty("name").GetString() == requested, "source.name differs from request", problems);
        var columns = body.GetProperty("querySchema").GetProperty("columns");
        Require(columns.GetArrayLength() > 0, "columns must not be empty", problems);
        foreach (var column in columns.EnumerateArray())
        {
            Members(column, ColumnMembers, problems);
            foreach (var name in new[] { "name", "title", "description" })
                Require(column.GetProperty(name).ValueKind == JsonValueKind.String, name + " must be a string", problems);
            Require(column.GetProperty("dataType").GetString() is "text" or "number" or "date" or "bool", "dataType invalid", problems);
            foreach (var name in new[] { "unit", "displayFormat" })
                Require(column.GetProperty(name).ValueKind is JsonValueKind.String or JsonValueKind.Null, name + " must be string or null", problems);
            Require(column.GetProperty("magnitude").GetString() is null or "none" or "thousands" or "millions" or "billions", "magnitude invalid", problems);
            foreach (var name in ColumnMembers.Skip(7)) Require(column.GetProperty(name).ValueKind is JsonValueKind.True or JsonValueKind.False, name + " must be boolean", problems);
        }
        Require(DemoPlan.RowCap(body) == body.GetProperty("source").GetProperty("rowCap").GetInt32(),
            "querySchema limit.maximum must equal source.rowCap", problems);
    });

    /// <summary>Checks a query answer: columns as requested, cell types, row count against the limit or cap,
    /// <c>more</c>, and the exact hint.</summary>
    /// <remarks>The expected hint is rebuilt from the answer's own <c>rowCount</c> and <c>more</c>, as the interface
    /// defines it, so a wrong hint for the actual result is caught.</remarks>
    /// <param name="answer">The answer.</param>
    /// <param name="source">The source name sent.</param>
    /// <param name="expected">The columns the answer must have, in order.</param>
    /// <param name="cap">The source's row cap.</param>
    /// <param name="limit">The limit sent, or null when the query had none.</param>
    /// <param name="filter">The column of an equality filter, if any; every row must hold <paramref name="value"/>.</param>
    /// <param name="value">The filter's value.</param>
    /// <param name="descendingCounts">Whether the last column must not increase from row to row.</param>
    /// <param name="ordered">Whether the query had an order, which decides the capped hint's ending.</param>
    /// <param name="hasInFilter">Whether the query had an <c>in</c> filter, which decides the complete hint.</param>
    public static IReadOnlyList<string> Rows(DemoAnswer answer, string source, IReadOnlyList<DemoColumn> expected,
        int cap, int? limit, DemoColumn? filter = null, JsonElement? value = null, bool descendingCounts = false,
        bool ordered = true, bool hasInFilter = false) => Check(answer, 200, (body, problems) =>
    {
        Members(body, ["source", "columns", "rows", "rowCount", "more", "hint"], problems);
        Require(body.GetProperty("source").GetString() == source, "source differs from request", problems);
        var columns = body.GetProperty("columns");
        Require(columns.GetArrayLength() == expected.Count, "columns count differs from request", problems);
        for (var i = 0; i < Math.Min(columns.GetArrayLength(), expected.Count); i++)
        {
            Members(columns[i], ["name", "dataType"], problems);
            Require(columns[i].GetProperty("name").GetString() == expected[i].Name, "columns.name differs from request", problems);
            Require(columns[i].GetProperty("dataType").GetString() == expected[i].DataType, "columns.dataType differs from describe", problems);
        }
        var rows = body.GetProperty("rows");
        var count = body.GetProperty("rowCount").GetInt32();
        var more = body.GetProperty("more").GetBoolean();
        var hint = more ? "The result set was capped at " + count + (count == 1 ? " row." : " rows.") : count == 0 ? "Nothing found that matched your query."
            : hasInFilter ? "All matching rows returned. in values that matched no row are left out: compare the values you asked for with the rows." : "Complete result set.";
        if (more && !ordered) hint += " Rows are in no particular order.";
        Require(body.GetProperty("hint").ValueKind == JsonValueKind.String && body.GetProperty("hint").GetString() == hint, "hint must be '" + hint + "'", problems);
        Require(rows.GetArrayLength() == count, "rowCount differs from rows length", problems);
        Require(count <= (limit ?? cap), "rowCount exceeds limit or cap", problems);
        Require(!more || count == (limit ?? cap), "more disagrees with limit or cap", problems);
        decimal? previous = null;
        foreach (var row in rows.EnumerateArray())
        {
            Members(row, expected.Select(c => c.Name), problems);
            foreach (var column in expected)
            {
                var cell = row.GetProperty(column.Name);
                Require(cell.ValueKind == JsonValueKind.Null || column.DataType switch
                {
                    "number" => cell.ValueKind == JsonValueKind.Number, "bool" => cell.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "text" or "date" => cell.ValueKind == JsonValueKind.String, _ => false
                }, "row value type differs at " + column.Name, problems);
            }
            if (filter is not null && value.HasValue)
                Require(EqualValue(row.GetProperty(filter.Name), value.Value, filter.DataType), "filter value differs at " + filter.Name, problems);
            if (descendingCounts)
            {
                var current = row.GetProperty(expected[^1].Name).GetDecimal();
                Require(previous is null || current <= previous, "group counts increase", problems);
                previous = current;
            }
        }
        if (filter is not null) Require(count >= 1, "filter returned no rows", problems);
    });

    /// <summary>Whether a returned cell equals the filter value.</summary>
    /// <remarks>Text compares ignoring case and trailing spaces, as case-insensitive SQL Server collations do, so a row the
    /// database rightly matched is not reported as a mismatch.</remarks>
    private static bool EqualValue(JsonElement left, JsonElement right, string type) => type switch
    {
        "number" => left.ValueKind == JsonValueKind.Number && left.TryGetDecimal(out var a) && right.TryGetDecimal(out var b) && a == b,
        "text" => left.ValueKind == JsonValueKind.String && string.Equals(left.GetString()!.TrimEnd(' '), right.GetString()!.TrimEnd(' '), StringComparison.OrdinalIgnoreCase),
        "bool" => left.ValueKind == right.ValueKind, _ => false
    };

    /// <summary>Checks an error answer: status, code, path, a message, and a 32-digit lowercase hex incident id.</summary>
    /// <param name="answer">The answer.</param>
    /// <param name="status">The expected status.</param>
    /// <param name="code">The expected code.</param>
    /// <param name="path">The expected path.</param>
    /// <param name="message">The exact expected message, or null to require only a nonblank one.</param>
    public static IReadOnlyList<string> Refusal(DemoAnswer answer, int status, string code, string path, string? message = null) => Check(answer, status, (body, problems) =>
    {
        Members(body, ["code", "path", "message", "incidentId"], problems);
        Require(body.GetProperty("code").GetString() == code, "code differs from expected " + code, problems);
        Require(body.GetProperty("path").GetString() == path, "path differs from expected " + path, problems);
        var text = body.GetProperty("message").GetString();
        Require(!string.IsNullOrWhiteSpace(text) && (message is null || text == message), "message differs or is empty", problems);
        Require(Regex.IsMatch(body.GetProperty("incidentId").GetString() ?? "", "^[0-9a-f]{32}$", RegexOptions.CultureInvariant), "incidentId must be 32 lowercase hex digits", problems);
    });
    /// <summary>The unknown-source message the gateway sends, naming the listed sources.</summary>
    public static string UnknownSourceMessage(IEnumerable<string> listed) => "Choose an active source. Available sources: " + string.Join(", ", listed) + ".";
    /// <summary>Checks the anonymous challenge: 401, a Negotiate offer, and exactly <see cref="ChallengeBody"/>.</summary>
    public static IReadOnlyList<string> Anonymous(DemoAnswer answer) => Check(answer, 401, (body, problems) =>
    {
        Require((answer.Challenge ?? "").Split(',').Any(c => c.Trim().Equals("Negotiate", StringComparison.OrdinalIgnoreCase)), "WWW-Authenticate must include Negotiate", problems);
        Require(JsonSerializer.Serialize(body) == ChallengeBody, "challenge members or text differ", problems);
    });
}
