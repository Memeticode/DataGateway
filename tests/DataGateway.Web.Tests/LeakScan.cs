using System.Data.Common;
using System.Net;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataGateway.Core.Queries;
using Xunit.Sdk;

namespace DataGateway.Web.Tests;

/// <summary>The kinds of connection-string part the scan looks for: the whole string, the server value, the host
/// name inside it, the user and the password.</summary>
internal enum PartKind { Connection, Server, Host, User, Password }
/// <summary>Who owns a piece of text, which decides what the scan rejects in it.</summary>
/// <remarks><c>DataGateway</c>: the gateway's own text; any part or keyword form fails. <c>Caller</c>: text that
/// echoes the caller's input; any part fails, keyword forms pass. <c>Foreign</c>: text owned by data, headers or the
/// framework; only distinctive parts and keyword forms fail. <c>Driver</c>: driver messages and exception text in
/// the log; only the whole connection, the password and a password keyword form fail.</remarks>
internal enum Rule { DataGateway, Caller, Foreign, Driver }
/// <summary>One part to look for.</summary>
/// <param name="Label">The safe name printed in a failure, such as <c>fixture password</c>.</param>
/// <param name="Kind">The kind of part.</param>
/// <param name="Value">The secret text itself; never printed.</param>
/// <param name="Distinctive">True when the value is unlikely to occur in ordinary text, so even foreign text must
/// not hold it.</param>
internal sealed record ConnectionPart(string Label, PartKind Kind, string Value, bool Distinctive)
{
    /// <summary>The label only, so an assertion that prints a part never prints its value.</summary>
    public override string ToString() => Label;
}

/// <summary>A test oracle that fails when any part of a connection string (the whole string, server, host, user or
/// password) appears in text the gateway exposes: replies, headers, pages, log lines and console output.</summary>
/// <remarks>
/// <para>Pure, independent oracle: it deliberately uses the BCL parser, not the driver's parser, so the check never
/// shares the parsing it is checking.</para>
/// <para>TERM — ownership rule (<see cref="Rule"/>): who wrote a piece of text decides how strictly it is judged.
/// The gateway's own text must hold no part and no connection keyword form; text that echoes the caller's input may
/// hold keyword forms the caller typed; text owned by someone else (data rows, headers, framework log fields) is
/// judged only on distinctive parts; driver text in logs only on the whole connection and the password.</para>
/// <para>A failure names a part by its label, never its value, so a real secret never reaches test output.</para>
/// </remarks>
internal sealed class LeakScan
{
    /// <summary>The shortest value worth scanning for; anything shorter matches ordinary text by chance.</summary>
    internal const int ShortestPart = 4;
    /// <summary>A connection-string keyword followed by an equals sign: text that looks like a connection string even
    /// when no known part is in it.</summary>
    internal static readonly Regex KeywordForm = new(
        @"\b(?:data\s+source|server|address|addr|network\s+address|user\s+id|uid|user|password|pwd|initial\s+catalog|database)\s*=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    /// <summary>A password keyword followed by an equals sign, the one keyword form driver text must never hold.</summary>
    private static readonly Regex PasswordForm = new(@"\b(?:password|pwd)\s*=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    /// <summary>The parts judged in driver text: whole connections and passwords, however short.</summary>
    private readonly List<ConnectionPart> driverParts = [];
    /// <summary>The form of an incident id: 32 lowercase hex digits.</summary>
    private static readonly Regex Incident = new(@"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant);
    /// <summary>The incident ID inside tool guidance, as in ToolWire.Guidance ("report incident ID {0}").</summary>
    private static readonly Regex GuidanceIncident = new(@"(?<=\bincident ID )[0-9a-f]{32}(?![0-9a-f])", RegexOptions.CultureInvariant);
    /// <summary>The scannable parts, in the order found.</summary>
    internal IReadOnlyList<ConnectionPart> Parts { get; }
    /// <summary>The parts judged in driver text.</summary>
    internal IReadOnlyList<ConnectionPart> DriverParts => driverParts.AsReadOnly();
    /// <summary>Creates a scan over parsed parts.</summary>
    private LeakScan(List<ConnectionPart> parts) => Parts = parts.AsReadOnly();
    /// <summary>A scan over real connections from the machine's settings.</summary>
    /// <remarks>A real part the scan cannot reliably find (an unparsable connection, a user or password of three
    /// characters or fewer) fails construction instead of passing silently. Only the whole connection counts as
    /// distinctive, because a real server or user name can appear in ordinary text such as a caller's name.</remarks>
    internal static LeakScan Real(params (string Name, string? Connection)[] connections) => Build(true, connections);
    /// <summary>A scan over invented connections; every part except a loopback server or host is distinctive, and
    /// short or unparsable values are skipped rather than refused.</summary>
    internal static LeakScan Synthetic(params (string Name, string? Connection)[] connections) => Build(false, connections);
    /// <summary>A count only, never a value.</summary>
    public override string ToString() => $"LeakScan ({Parts.Count} parts)";

    /// <summary>Parses each connection into parts with the BCL parser, keeping the whole string and each server, host,
    /// user and password value, and refusing real parts that cannot be scanned.</summary>
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

    /// <summary>The labels of every part and keyword form that <paramref name="text"/> holds under
    /// <paramref name="rule"/>; empty when the text is clean.</summary>
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
    /// <summary>Fails, naming <paramref name="where"/> and the labels found, when the text breaks the rule.</summary>
    internal void Text(string where, string? text, Rule rule)
    {
        if (text is null) return;
        var found = Found(text, rule);
        if (found.Count != 0) throw new XunitException($"Connection text in {where}: {string.Join(", ", found)}.");
    }
    /// <summary>Scans an MCP tool's guidance text.</summary>
    /// <remarks>Guidance is DataGateway's own text with one random incident ID in it. The ID is checked by its form, as
    /// the body's incidentId is, and kept out of the match: a short password can occur inside random hex by chance.
    /// Everything else in the guidance is scanned as DataGateway text.</remarks>
    internal void Guidance(string where, string? text)
    {
        if (text is null) return;
        Text(where, GuidanceIncident.Replace(text, "<incident>"), Rule.DataGateway);
    }
    /// <summary>Scans every property name and string value of a JSON element; numbers are never matched, since a
    /// numeric part would match some number by chance.</summary>
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
    /// <summary>Scans a gateway reply body by ownership: an error's incident id by form, a refusal's path as caller
    /// text, success rows as foreign text, everything else as the gateway's own.</summary>
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
    /// <summary>Scans a JSON reply's body and headers.</summary>
    internal void Response(string where, HttpResponseMessage response, string body)
    {
        Body(where + " body", body);
        Headers(where, response);
    }
    /// <summary>Scans a text reply, such as the API page, after decoding HTML entities so an encoded part is found too,
    /// and its headers.</summary>
    internal void TextResponse(string where, HttpResponseMessage response, string text)
    {
        Text(where + " body", WebUtility.HtmlDecode(text), Rule.DataGateway);
        Headers(where, response);
    }
    /// <summary>Checks a served page asset is byte for byte the packaged file, and scans its headers.</summary>
    internal void Asset(string where, HttpResponseMessage response, byte[] served, byte[] packaged)
    {
        Pin(where, "package asset", served.AsSpan().SequenceEqual(packaged));
        Headers(where, response);
    }
    /// <summary>Scans every header, trailers included; only the content type's value is the gateway's own text.</summary>
    private void Headers(string where, HttpResponseMessage response)
    {
        foreach (var header in response.Headers.Concat(response.Content.Headers).Concat(response.TrailingHeaders))
        {
            Text(where + " headers", header.Key, Rule.Foreign);
            foreach (var value in header.Value) Text(where + " headers." + header.Key, value,
                header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ? Rule.DataGateway : Rule.Foreign);
        }
    }
    /// <summary>Scans one structured log line by field: on a gateway failure line, each field by who owns it and the
    /// numeric fields by type; on any other line, everything as foreign text except the exception (<c>@x</c>),
    /// which is judged as strictly as the gateway's own text.</summary>
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
    /// <summary>True for failure-line fields whose text comes from outside the gateway: the source name, caller, request
    /// and trace ids, scope, and the framework's own <c>@</c> fields other than the template and the exception.</summary>
    private static bool IsForeign(string name) => name is "SourceName" or "Caller" or "RequestId" or "RequestPath" or "ConnectionId" or "TraceId" or "SpanId" or "ParentId" or "Scope" ||
        name.StartsWith('@') && name is not "@mt" and not "@x";

    /// <summary>Scans one in-memory log entry; a failure entry is judged as its file line would be, and its rendered
    /// text must be the failure summary exactly.</summary>
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
    /// <summary>Checks console output against the file log: every failure summary on the console matches exactly one
    /// file line, once, and every file line has its summary; other console lines are scanned as foreign text.</summary>
    /// <returns>The incident ids seen on the console.</returns>
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
    /// <summary>Checks a standard-error line: a gateway notice must start with its prefix and incident id and hold no
    /// part; any other line is scanned as foreign text.</summary>
    internal void Notice(string where, string line)
    {
        const string prefix = "DataGateway.Failure: incident ";
        if (!line.StartsWith("DataGateway.Failure:", StringComparison.Ordinal)) { Text(where, line, Rule.Foreign); return; }
        Pin(where, "notice prefix", line.StartsWith(prefix, StringComparison.Ordinal));
        var tail = line[prefix.Length..];
        Pin(where, "notice incident", tail.Length >= 34 && Incident.IsMatch(tail[..32]) && tail[32..34] == ": ");
        Text(where + " notice", tail[34..], Rule.DataGateway);
    }
    /// <summary>The one-line summary the console shows for a failure line, rebuilt from its fields.</summary>
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
    /// <summary>Proves the scan finds each of its own parts under every rule that applies, so a clean result is a real
    /// pass and not a broken oracle.</summary>
    internal void Armed()
    {
        Pin("scan", "holds no connection part", Parts.Count > 0);
        foreach (var part in Parts)
            foreach (var rule in new[] { Rule.DataGateway, Rule.Caller, Rule.Foreign })
                if (rule != Rule.Foreign || part.Distinctive)
                    Pin("scan", part.Label, Found(part.Value, rule).Contains(part.Label));
    }
    /// <summary>Parses JSON, failing without printing the text.</summary>
    private static JsonDocument Parse(string where, string text)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { throw new XunitException("Invalid JSON in " + where + "; details suppressed."); }
    }
    /// <summary>Checks an incident id by its form.</summary>
    private static void Id(string where, JsonElement value) =>
        Pin(where, "incidentId", value.ValueKind == JsonValueKind.String && Incident.IsMatch(value.GetString()!));
    /// <summary>Fails with the field and place only, never the value.</summary>
    private static void Pin(string where, string field, bool valid)
    {
        if (!valid) throw new XunitException("Invalid " + field + " in " + where + ".");
    }
}
