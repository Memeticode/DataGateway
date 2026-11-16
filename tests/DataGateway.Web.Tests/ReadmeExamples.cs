using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataGateway.Web.Tests;

public sealed record ReadmeExample(string Name, string? Route, string? Request, int? Status, JsonElement? Answer, bool Anonymous);
public sealed record ReadmeError(string Code, int Status, string When, IReadOnlyList<string> Paths);

public static class ReadmeExamples
{
    public static IReadOnlyList<ReadmeExample> Parse(string markdown)
    {
        var examples = new Dictionary<string, ReadmeExample>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = markdown.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i] != "```json") continue;
            var marker = i > 0 ? Regex.Match(lines[i - 1], @"^<!-- example name=(\S+) kind=(request|answer)(.*?) -->$") : Match.Empty;
            if (!marker.Success) throw new FormatException("JSON block has no immediately preceding example marker.");
            var name = marker.Groups[1].Value;
            var kind = marker.Groups[2].Value;
            if (!seen.Add(name + ":" + kind)) throw new FormatException("Duplicate example " + name + " " + kind);
            var options = Regex.Matches(marker.Groups[3].Value, @"(\w+)=(\S+)").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            var start = ++i;
            while (i < lines.Length && lines[i] != "```") i++;
            if (i == lines.Length) throw new FormatException("Unclosed JSON block.");
            var text = string.Join("\n", lines[start..i]);
            using var json = JsonDocument.Parse(text);
            if (!examples.TryGetValue(name, out var example)) example = new(name, null, null, null, null, false);
            if (kind == "request")
            {
                if (!options.TryGetValue("route", out var route)) throw new FormatException("Request lacks route.");
                example = example with { Route = route, Request = text };
            }
            else
            {
                if (!options.TryGetValue("status", out var status) || !int.TryParse(status, out var number)) throw new FormatException("Answer lacks status.");
                example = example with { Status = number, Answer = json.RootElement.Clone(),
                    Route = options.GetValueOrDefault("route") ?? example.Route, Anonymous = options.GetValueOrDefault("signin") == "none" };
            }
            examples[name] = example;
        }
        foreach (var example in examples.Values)
            if (example.Request is null && !(example.Name == "challenge" && example.Anonymous && example.Route == "/sources/list"))
                throw new FormatException("Answer has no request: " + example.Name);
        return examples.Values.ToArray();
    }

    public static IReadOnlyList<ReadmeError> ErrorTable(string markdown)
    {
        var marker = markdown.IndexOf("<!-- error-table -->", StringComparison.Ordinal);
        if (marker < 0) throw new FormatException("Missing error table.");
        var lines = markdown[(marker + "<!-- error-table -->".Length)..].Replace("\r", "").Split('\n')
            .SkipWhile(line => !line.StartsWith('|')).TakeWhile(line => line.StartsWith('|')).ToArray();
        if (lines.Length < 3 || lines[0] != "| Code | Status | When | path |") throw new FormatException("Invalid error table header.");
        return lines.Skip(2).Select(line =>
        {
            var cells = line.Split('|').Skip(1).SkipLast(1).Select(s => s.Trim()).ToArray();
            if (cells.Length != 4 || !int.TryParse(cells[1], out var status)) throw new FormatException("Invalid error table row.");
            var paths = cells[3].Split(", ").Select(cell =>
                cell.StartsWith('`') && cell.EndsWith('`') ? cell[1..^1] : throw new FormatException("Invalid path cell.")).ToArray();
            return new ReadmeError(cells[0], status, cells[2], paths);
        }).ToArray();
    }
}
