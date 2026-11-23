using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataGateway.Web.Tests;

/// <summary>One worked example from the host README: a request and the answer it gets, joined by the example's name.</summary>
/// <param name="Name">The example's name, from its marker.</param>
/// <param name="Route">The route the request is posted to.</param>
/// <param name="Request">The request body as written, or null for an answer shown without a request.</param>
/// <param name="Status">The HTTP status the answer documents.</param>
/// <param name="Answer">The documented answer body.</param>
/// <param name="Anonymous">Whether the example is sent without sign-in (marker option <c>signin=none</c>).</param>
public sealed record ReadmeExample(string Name, string? Route, string? Request, int? Status, JsonElement? Answer, bool Anonymous);
/// <summary>One row of the README's error table.</summary>
/// <param name="Code">The error code.</param>
/// <param name="Status">The HTTP status the code is sent with.</param>
/// <param name="When">The table's plain-English cause.</param>
/// <param name="Paths">Every JSON path the code can name, from the path cell.</param>
public sealed record ReadmeError(string Code, int Status, string When, IReadOnlyList<string> Paths);

/// <summary>Reads the machine-checkable parts of a README: the marked JSON examples and the error table, so tests can
/// replay them against a real host.</summary>
/// <remarks>Each JSON block must sit directly under an <c>&lt;!-- example ... --&gt;</c> marker, and a malformed block
/// throws <see cref="FormatException"/> instead of being skipped. A skipped block would leave a documented example
/// untested and free to drift from what the gateway really answers.</remarks>
public static class ReadmeExamples
{
    /// <summary>Parses every marked JSON example in <paramref name="markdown"/>, pairing each request with its answer.</summary>
    /// <remarks>Only the 401 sign-in challenge on the list route may appear as an answer with no request, since it
    /// documents what any unsigned call gets.</remarks>
    /// <param name="markdown">The README text.</param>
    /// <returns>One example per name.</returns>
    /// <exception cref="FormatException">A block has no marker, a marker is repeated, a block is unclosed, a
    /// request lacks its route, an answer lacks its status, or an answer has no request.</exception>
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

    /// <summary>Parses the table that follows the <c>&lt;!-- error-table --&gt;</c> marker.</summary>
    /// <param name="markdown">The README text.</param>
    /// <returns>The table's rows, in order.</returns>
    /// <exception cref="FormatException">The marker, header or a row is missing or malformed, or a path cell is not code.</exception>
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
