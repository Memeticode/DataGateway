using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataGateway.Demo;

public sealed record DemoAnswer(int Status, string? ContentType, string? Challenge, string Body);

public static class DemoChecks
{
    private static readonly string[] SourceMembers = ["name", "title", "description", "grain", "rowCap"];
    private static readonly string[] ColumnMembers = ["name", "title", "description", "dataType", "unit", "magnitude", "displayFormat", "canSelect", "canFilter", "canSort", "canGroup", "canAggregate"];
    public const string ChallengeBody = """{"code":"NotAuthenticated","path":"$","message":"Sign in with Windows to call DataGateway: send the request with Windows credentials (HTTP Negotiate)."}""";
    public const string GroupMessage = "The column at path cannot be grouped. Group by a column whose canGroup is true.";

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

    private static void Members(JsonElement body, IEnumerable<string> expected, List<string> problems)
    {
        var names = expected.ToArray();
        if (body.ValueKind != JsonValueKind.Object || !body.EnumerateObject().Select(p => p.Name).SequenceEqual(names))
            problems.Add("members: expected " + string.Join(", ", names) + " in that order");
    }
    private static void Require(bool condition, string problem, List<string> problems) { if (!condition) problems.Add(problem); }
    private static void Source(JsonElement body, List<string> problems)
    {
        Members(body, SourceMembers, problems);
        foreach (var name in SourceMembers.Take(4)) Require(body.GetProperty(name).ValueKind == JsonValueKind.String, name + " must be a string", problems);
        var cap = body.GetProperty("rowCap").GetInt32();
        Require(cap >= 1 && cap <= int.MaxValue - 1, "rowCap must be an integer from 1 to 2147483646", problems);
    }
    public static IReadOnlyList<string> List(DemoAnswer answer) => Check(answer, 200, (body, problems) =>
    {
        Members(body, ["sources"], problems);
        var sources = body.GetProperty("sources");
        Require(sources.GetArrayLength() > 0, "sources: register a view: README, Get started, step 4", problems);
        foreach (var source in sources.EnumerateArray()) Source(source, problems);
    });

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

    public static IReadOnlyList<string> Rows(DemoAnswer answer, string source, IReadOnlyList<DemoColumn> expected,
        int cap, int? limit, DemoColumn? filter = null, JsonElement? value = null, bool descendingCounts = false,
        bool ordered = true) => Check(answer, 200, (body, problems) =>
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
        var hint = more ? "The result set was capped at " + count + (count == 1 ? " row." : " rows.") : count == 0 ? "Nothing found that matched your query." : "Complete result set.";
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

    private static bool EqualValue(JsonElement left, JsonElement right, string type) => type switch
    {
        "number" => left.ValueKind == JsonValueKind.Number && left.TryGetDecimal(out var a) && right.TryGetDecimal(out var b) && a == b,
        "text" => left.ValueKind == JsonValueKind.String && string.Equals(left.GetString()!.TrimEnd(' '), right.GetString()!.TrimEnd(' '), StringComparison.OrdinalIgnoreCase),
        "bool" => left.ValueKind == right.ValueKind, _ => false
    };

    public static IReadOnlyList<string> Refusal(DemoAnswer answer, int status, string code, string path, string? message = null) => Check(answer, status, (body, problems) =>
    {
        Members(body, ["code", "path", "message", "incidentId"], problems);
        Require(body.GetProperty("code").GetString() == code, "code differs from expected " + code, problems);
        Require(body.GetProperty("path").GetString() == path, "path differs from expected " + path, problems);
        var text = body.GetProperty("message").GetString();
        Require(!string.IsNullOrWhiteSpace(text) && (message is null || text == message), "message differs or is empty", problems);
        Require(Regex.IsMatch(body.GetProperty("incidentId").GetString() ?? "", "^[0-9a-f]{32}$", RegexOptions.CultureInvariant), "incidentId must be 32 lowercase hex digits", problems);
    });
    public static string UnknownSourceMessage(IEnumerable<string> listed) => "Choose an active source. Available sources: " + string.Join(", ", listed) + ".";
    public static IReadOnlyList<string> Anonymous(DemoAnswer answer) => Check(answer, 401, (body, problems) =>
    {
        Require((answer.Challenge ?? "").Split(',').Any(c => c.Trim().Equals("Negotiate", StringComparison.OrdinalIgnoreCase)), "WWW-Authenticate must include Negotiate", problems);
        Require(JsonSerializer.Serialize(body) == ChallengeBody, "challenge members or text differ", problems);
    });
}
