using System.Data.Common;
using System.Net;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataGateway.Core.Queries;
using Xunit.Sdk;

namespace DataGateway.Web.Tests;

internal enum PartKind { Connection, Server, Host, User, Password }
internal enum Rule { DataGateway, Caller, Foreign, Driver }
internal sealed record ConnectionPart(string Label, PartKind Kind, string Value, bool Distinctive)
{
    public override string ToString() => Label;
}

// Pure, independent oracle: it deliberately uses the BCL parser, not the driver's parser.
internal sealed class LeakScan
{
    internal const int ShortestPart = 4;
    internal static readonly Regex KeywordForm = new(
        @"\b(?:data\s+source|server|address|addr|network\s+address|user\s+id|uid|user|password|pwd|initial\s+catalog|database)\s*=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PasswordForm = new(@"\b(?:password|pwd)\s*=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly List<ConnectionPart> driverParts = [];
    private static readonly Regex Incident = new(@"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant);
    // The incident ID inside tool guidance, as in ToolWire.Guidance ("report incident ID {0}").
    private static readonly Regex GuidanceIncident = new(@"(?<=\bincident ID )[0-9a-f]{32}(?![0-9a-f])", RegexOptions.CultureInvariant);
    internal IReadOnlyList<ConnectionPart> Parts { get; }
    internal IReadOnlyList<ConnectionPart> DriverParts => driverParts.AsReadOnly();
    private LeakScan(List<ConnectionPart> parts) => Parts = parts.AsReadOnly();
    internal static LeakScan Real(params (string Name, string? Connection)[] connections) => Build(true, connections);
    internal static LeakScan Synthetic(params (string Name, string? Connection)[] connections) => Build(false, connections);
    public override string ToString() => $"LeakScan ({Parts.Count} parts)";

    private static LeakScan Build(bool real, (string Name, string? Connection)[] connections)
    {
        var parts = new List<ConnectionPart>();
        var invalid = new List<string>();
        var driverParts = new List<ConnectionPart>();
        foreach (var (name, connection) in connections)
        {
            if (string.IsNullOrWhiteSpace(connection)) continue;
            Add(PartKind.Connection, connection);
            DbConnectionStringBuilder parsed;
            try { parsed = new() { ConnectionString = connection }; }
            catch (Exception error) when (error is ArgumentException or FormatException)
            {
                if (real) invalid.Add(name + ": cannot parse connection; details suppressed");
                continue;
            }
            foreach (string key in parsed.Keys)
            {
                var value = Convert.ToString(parsed[key], CultureInfo.InvariantCulture) ?? "";
                var normalized = Regex.Replace(key.Trim(), @"\s+", " ").ToLowerInvariant();
                switch (normalized)
                {
                    case "data source": case "server": case "address": case "addr": case "network address":
                        Add(PartKind.Server, value);
                        var host = value;
                        foreach (var prefix in new[] { "tcp:", "np:", "lpc:", "admin:" })
                            if (host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { host = host[prefix.Length..]; break; }
                        var end = host.IndexOfAny([',', '\\']);
                        Add(PartKind.Host, end < 0 ? host : host[..end]);
                        break;
                    case "user id": case "uid": case "user": Add(PartKind.User, value); break;
                    case "password": case "pwd": Add(PartKind.Password, value); break;
                }
            }
            void Add(PartKind kind, string value)
            {
                var label = name + " " + kind.ToString().ToLowerInvariant();
                if (kind is PartKind.Connection or PartKind.Password && value.Length > 0)
                    driverParts.Add(new(label, kind, value, true));
                if (value.Length < ShortestPart)
                {
                    if (real && kind is PartKind.User or PartKind.Password && !string.IsNullOrWhiteSpace(value))
                        invalid.Add(label + ": 3 characters or fewer, so no scan can tell it from ordinary text");
                    return;
                }
                var loopback = kind is PartKind.Server or PartKind.Host &&
                    new[] { "127.0.0.1", "::1", "localhost" }.Contains(value, StringComparer.OrdinalIgnoreCase);
                parts.Add(new(label, kind, value, real ? kind == PartKind.Connection : !loopback));
            }
        }
        if (invalid.Count != 0) throw new XunitException(string.Join("; ", invalid) + ".");
        var result = new LeakScan(parts);
        result.driverParts.AddRange(driverParts);
        return result;
    }

    internal IReadOnlyList<string> Found(string text, Rule rule)
    {
        if (rule == Rule.Driver)
        {
            var found = driverParts.Where(p => text.Contains(p.Value, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Label).Distinct().ToList();
            if (PasswordForm.IsMatch(text)) found.Add("a password keyword form");
            return found.AsReadOnly();
        }
        var labels = Parts.Where(p => (rule != Rule.Foreign || p.Distinctive) &&
            text.Contains(p.Value, StringComparison.OrdinalIgnoreCase)).Select(p => p.Label).Distinct().ToList();
        if (rule != Rule.Caller && KeywordForm.IsMatch(text)) labels.Add("a connection keyword form");
        return labels.AsReadOnly();
    }
    internal void Text(string where, string? text, Rule rule)
    {
        if (text is null) return;
        var found = Found(text, rule);
        if (found.Count != 0) throw new XunitException($"Connection text in {where}: {string.Join(", ", found)}.");
    }
    // Guidance is DataGateway's own text with one random incident ID in it. The ID is checked by its form, as the body's
    // incidentId is, and kept out of the match: a short password can occur inside random hex by chance. Everything
    // else in the guidance is scanned as DataGateway text.
    internal void Guidance(string where, string? text)
    {
        if (text is null) return;
        Text(where, GuidanceIncident.Replace(text, "<incident>"), Rule.DataGateway);
    }
    internal void Json(string where, JsonElement value, Rule rule)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                Text(where, property.Name, rule);
                Json(where + "." + property.Name, property.Value, rule);
            }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) Json(where + "[" + index++ + "]", item, rule);
        }
        else if (value.ValueKind == JsonValueKind.String) Text(where, value.GetString(), rule);
    }
    internal void Body(string where, string body)
    {
        using var json = Parse(where, body);
        var root = json.RootElement;
        Pin(where, "body", root.ValueKind == JsonValueKind.Object);
        var error = root.TryGetProperty("code", out var code);
        var refusal = error && code.ValueKind == JsonValueKind.String && QueryAnswer.IsRefusal(code.GetString()!);
        foreach (var property in root.EnumerateObject())
        {
            Text(where, property.Name, Rule.DataGateway);
            var place = where + "." + property.Name;
            if (error && property.Name == "incidentId") { Id(place, property.Value); continue; }
            var rule = error && property.Name == "path" && refusal ? Rule.Caller :
                !error && property.Name == "rows" ? Rule.Foreign : Rule.DataGateway;
            Json(place, property.Value, rule);
        }
    }
    internal void Response(string where, HttpResponseMessage response, string body)
    {
        Body(where + " body", body);
        Headers(where, response);
    }
    internal void TextResponse(string where, HttpResponseMessage response, string text)
    {
        Text(where + " body", WebUtility.HtmlDecode(text), Rule.DataGateway);
        Headers(where, response);
    }
    internal void Asset(string where, HttpResponseMessage response, byte[] served, byte[] packaged)
    {
        Pin(where, "package asset", served.AsSpan().SequenceEqual(packaged));
        Headers(where, response);
    }
    private void Headers(string where, HttpResponseMessage response)
    {
        foreach (var header in response.Headers.Concat(response.Content.Headers).Concat(response.TrailingHeaders))
        {
            Text(where + " headers", header.Key, Rule.Foreign);
            foreach (var value in header.Value) Text(where + " headers." + header.Key, value,
                header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ? Rule.DataGateway : Rule.Foreign);
        }
    }
    internal void LogLine(string where, JsonElement line)
    {
        Pin(where, "line", line.ValueKind == JsonValueKind.Object);
        var failure = line.TryGetProperty("SourceContext", out var source) && source.ValueKind == JsonValueKind.String && source.GetString() == "DataGateway.Failure";
        var refusal = line.TryGetProperty("Code", out var code) && code.ValueKind == JsonValueKind.String && QueryAnswer.IsRefusal(code.GetString()!);
        foreach (var property in line.EnumerateObject())
        {
            Text(where, property.Name, failure ? Rule.DataGateway : Rule.Foreign);
            var place = where + "." + property.Name;
            if (!failure) { Json(place, property.Value, property.Name == "@x" ? Rule.DataGateway : Rule.Foreign); continue; }
            switch (property.Name)
            {
                case "IncidentId": Id(place, property.Value); break;
                case "RequestBytes": case "SqlNumber": case "SourceId":
                    Pin(where, property.Name, property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.Null); break;
                case "RequestCut":
                    Pin(where, property.Name, property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null); break;
                default:
                    var rule = property.Name is "DriverMessage" or "ExceptionText" ? Rule.Driver :
                        property.Name is "Request" or "SqlParameters" || property.Name == "Path" && refusal ? Rule.Caller :
                        IsForeign(property.Name) ? Rule.Foreign : Rule.DataGateway;
                    Json(place, property.Value, rule);
                    break;
            }
        }
    }
    private static bool IsForeign(string name) => name is "SourceName" or "Caller" or "RequestId" or "RequestPath" or "ConnectionId" or "TraceId" or "SpanId" or "ParentId" or "Scope" ||
        name.StartsWith('@') && name is not "@mt" and not "@x";

    internal void Entry(string where, string category, string rendered, IReadOnlyDictionary<string, object?>? fields, string? exception)
    {
        Text(where + " category", category, category == "DataGateway.Failure" ? Rule.DataGateway : Rule.Foreign);
        Text(where + " exception", exception, Rule.DataGateway);
        if (category != "DataGateway.Failure") { Text(where + " rendered", rendered, Rule.Foreign); return; }
        Pin(where, "fields", fields is not null);
        if (fields!.TryGetValue("SourceContext", out var context)) Json(where + ".SourceContext", JsonSerializer.SerializeToElement(context), Rule.DataGateway);
        var values = new Dictionary<string, object?>(fields, StringComparer.Ordinal) { ["SourceContext"] = category };
        var line = JsonSerializer.SerializeToElement(values);
        LogLine(where, line);
        Pin(where, "rendered summary", rendered == Summary(line));
    }
    internal IReadOnlySet<string> Console(string where, IReadOnlyList<string> lines, IReadOnlyList<JsonElement> failureLines)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in failureLines)
        {
            LogLine(where + " file", line);
            var id = line.GetProperty("IncidentId").GetString()!;
            Pin(where, "file incident uniqueness", expected.TryAdd(id, Summary(line)));
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            const string category = "] DataGateway.Failure: ";
            var offset = line.IndexOf(category, StringComparison.Ordinal);
            if (offset < 0) { Text(where, line, Rule.Foreign); continue; }
            Text(where + " prefix", line[..offset], Rule.Foreign);
            var summary = line[(offset + category.Length)..];
            var match = Regex.Match(summary, @"\AFailure ([0-9a-f]{32}): ", RegexOptions.CultureInvariant);
            Pin(where, "summary incident", match.Success);
            var id = match.Groups[1].Value;
            Pin(where, "summary file", expected.TryGetValue(id, out var value));
            Pin(where, "summary", summary == value);
            Pin(where, "summary uniqueness", seen.Add(id));
        }
        Pin(where, "summary completeness", seen.SetEquals(expected.Keys));
        return seen;
    }
    internal void Notice(string where, string line)
    {
        const string prefix = "DataGateway.Failure: incident ";
        if (!line.StartsWith("DataGateway.Failure:", StringComparison.Ordinal)) { Text(where, line, Rule.Foreign); return; }
        Pin(where, "notice prefix", line.StartsWith(prefix, StringComparison.Ordinal));
        var tail = line[prefix.Length..];
        Pin(where, "notice incident", tail.Length >= 34 && Incident.IsMatch(tail[..32]) && tail[32..34] == ": ");
        Text(where + " notice", tail[34..], Rule.DataGateway);
    }
    internal static string Summary(JsonElement line)
    {
        string Field(string name)
        {
            Pin("summary", name, line.TryGetProperty(name, out var value));
            Pin("summary", name, value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null);
            return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        }
        return $"Failure {Field("IncidentId")}: {Field("Operation")} for {Field("Caller")}: {Field("Code")}: {Field("Message")} ({Field("Step")}, {Field("ExceptionType")}, {Field("SqlNumber")})";
    }
    internal void Armed()
    {
        Pin("scan", "holds no connection part", Parts.Count > 0);
        foreach (var part in Parts)
            foreach (var rule in new[] { Rule.DataGateway, Rule.Caller, Rule.Foreign })
                if (rule != Rule.Foreign || part.Distinctive)
                    Pin("scan", part.Label, Found(part.Value, rule).Contains(part.Label));
    }
    private static JsonDocument Parse(string where, string text)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { throw new XunitException("Invalid JSON in " + where + "; details suppressed."); }
    }
    private static void Id(string where, JsonElement value) =>
        Pin(where, "incidentId", value.ValueKind == JsonValueKind.String && Incident.IsMatch(value.GetString()!));
    private static void Pin(string where, string field, bool valid)
    {
        if (!valid) throw new XunitException("Invalid " + field + " in " + where + ".");
    }
}
