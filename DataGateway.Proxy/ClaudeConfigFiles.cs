using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataGateway.Proxy;

internal static class ClaudeConfigFiles
{
    internal const string Filename = "claude_desktop_config.json";
    internal sealed record Target(string Path, bool TierA, bool Fallback = false);
    internal sealed record Discovery(IReadOnlyList<Target> Targets, IReadOnlyList<string> Errors);
    internal sealed class FilePlan(Target target)
    {
        internal Target Target { get; } = target;
        internal byte[]? Original { get; set; }
        internal byte[]? Content { get; set; }
        internal string? Reason { get; set; }
        internal bool Barrier { get; set; }
        internal string Status { get; set; } = "";
    }
    internal sealed record Result(bool Success, string Status);

    internal static Discovery Discover(RegistrationContext context, bool register)
    {
        var files = context.Files;
        var targets = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var inspected = new Dictionary<string, FileAttributes?>(StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Error(string path, string reason = "cannot inspect candidate folder")
        {
            path = ClaudeDesktopEnvironment.FullCommandPath(path) ?? path;
            if (failed.Add(path)) errors.Add($"{ProxyRegistration.Q(path)}: refused: {reason}");
        }
        bool DirectoryPresent(string path)
        {
            if (failed.Contains(path)) return false;
            try
            {
                if (!inspected.TryGetValue(path, out var attributes))
                    inspected[path] = attributes = files.Attributes(path);
                if (attributes is null) return false;
                if ((attributes.Value & FileAttributes.Directory) != 0) return true;
                Error(path);
            }
            catch (Exception) { Error(path); }
            return false;
        }
        void Candidate(string directory, bool tierA)
        {
            directory = Path.GetFullPath(directory);
            if (ExcludedRelocation(directory, context) || !DirectoryPresent(directory)) return;
            var path = Path.GetFullPath(Path.Combine(directory, Filename));
            if (tierA)
            {
                targets[path] = new(path, true);
                return;
            }
            try
            {
                if (files.Attributes(path) is not null && !targets.ContainsKey(path)) targets[path] = new(path, false);
            }
            catch (Exception) { Error(directory); }
        }
        IReadOnlyList<string> Children(string parent)
        {
            if (!DirectoryPresent(parent)) return [];
            try { return files.Directories(parent).ToArray(); }
            catch (Exception) { Error(parent); return []; }
        }

        if (!string.IsNullOrEmpty(context.ClaudeUserDataDirectory))
        {
            var relocation = ClaudeDesktopEnvironment.FullCommandPath(context.ClaudeUserDataDirectory);
            if (relocation is null || ExcludedRelocation(relocation, context))
                Error(context.ClaudeUserDataDirectory, "invalid or excluded relocation directory");
            else Candidate(relocation, true);
        }
        Candidate(Path.Combine(context.AppData, "Claude"), true);
        var packageRoot = Path.Combine(context.LocalAppData, "Packages");
        foreach (var package in Children(packageRoot).Where(path => ClaudeDesktopEnvironment.PackageName(Path.GetFileName(path))))
        {
            Candidate(Path.Combine(package, "LocalCache", "Roaming", "Claude"), true);
            Candidate(Path.Combine(package, "LocalCache", "Local", "Claude"), false);
            Candidate(Path.Combine(package, "LocalCache", "Local", "Claude-3p"), false);
            Candidate(Path.Combine(package, "LocalCache", "Roaming", "Claude-3p"), false);
        }
        Candidate(Path.Combine(context.LocalAppData, "Claude-3p"), true);
        Candidate(Path.Combine(context.AppData, "Claude-3p"), true);
        var dataRoot = Path.Combine(context.LocalAppData, "Claude-Data");
        Candidate(dataRoot, false);
        foreach (var child in Children(dataRoot)) Candidate(child, false);
        foreach (var name in new[] { "Claude", "Claude Nest", "Claude Nest-3p" })
            Candidate(Path.Combine(context.LocalAppData, name), false);
        if (register && targets.Count == 0 && errors.Count == 0)
        {
            var fallback = Path.GetFullPath(Path.Combine(context.AppData, "Claude", Filename));
            if (!ExcludedRelocation(Path.GetDirectoryName(fallback)!, context))
                targets[fallback] = new(fallback, true, true);
        }
        return new(targets.Values.OrderBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.Path, StringComparer.Ordinal).ToArray(), errors);
    }

    private static bool ExcludedRelocation(string path, RegistrationContext context)
    {
        foreach (var root in new[] { Path.Combine(context.AppData, "Claude-modified"),
            Path.Combine(context.ProgramFiles, "WindowsApps"), Path.Combine(context.LocalAppData, "AnthropicClaude") })
            if (ClaudeDesktopEnvironment.SameExecutable(path.TrimEnd(Path.DirectorySeparatorChar), root) || ClaudeDesktopEnvironment.Under(path, root)) return true;
        // Installation, managed configuration and other clients are never relocation targets.
        var segments = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => segment.Equals(".vscode", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".claude", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("Claude Code", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("Cursor", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("managed", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("managed-config", StringComparison.OrdinalIgnoreCase) ||
            segment.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains(".json.", StringComparison.OrdinalIgnoreCase));
    }

    internal static FilePlan Plan(IRegistrationFiles files, Target target, RegistrationRequest request, string executable)
    {
        var plan = new FilePlan(target);
        try
        {
            var attributes = files.Attributes(target.Path);
            if (attributes is not null && (attributes.Value & FileAttributes.Directory) != 0)
            { plan.Reason = "config path is not a file"; return plan; }
            if (attributes is null)
            {
                if (!target.TierA) { plan.Reason = "discovered config file is no longer present"; return plan; }
                if (!request.Register) { plan.Status = "skipped: config file not present"; return plan; }
            }
            else plan.Original = files.ReadAllBytes(target.Path);
        }
        catch (Exception) { plan.Reason = "cannot read config file"; return plan; }
        try
        {
            var rootRaw = new Dictionary<string, string>(StringComparer.Ordinal);
            var serverRaw = new Dictionary<string, string>(StringComparer.Ordinal);
            JsonObject merged;
            if (plan.Original is null) merged = new();
            else
            {
                var bytes = plan.Original.AsSpan();
                if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
                var text = new UTF8Encoding(false, true).GetString(bytes);
                using var document = JsonDocument.Parse(text);
                CheckDuplicateNames(document.RootElement);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                { plan.Reason = "top-level JSON is not an object"; return plan; }
                foreach (var property in document.RootElement.EnumerateObject()) rootRaw[property.Name] = property.Value.GetRawText();
                if (document.RootElement.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object)
                    foreach (var property in servers.EnumerateObject()) serverRaw[property.Name] = property.Value.GetRawText();
                merged = JsonNode.Parse(text)!.AsObject();
            }
            var originalNode = merged.DeepClone();
            var hasServers = merged.TryGetPropertyValue("mcpServers", out var map);
            if (hasServers && map is not JsonObject)
            { plan.Reason = "mcpServers is not a JSON object"; plan.Barrier = true; return plan; }
            var serverObject = map as JsonObject;
            var aliases = new List<string>();
            if (request.Register)
            {
                if (serverObject is null) merged["mcpServers"] = serverObject = new();
                var present = serverObject.TryGetPropertyValue(request.Key, out var old);
                if (present && !ClaudeDesktopEnvironment.SameExecutable(Command(old), executable))
                {
                    plan.Reason = $"name {ProxyRegistration.Q(request.Key)} belongs to {ProxyRegistration.Q(Command(old) ?? "<missing or non-string command>")}; choose another --name or use --unregister first";
                    plan.Barrier = true;
                    return plan;
                }
                var oldUrl = UrlOf(old);
                // An old alias is an entry for this proxy AND the same gateway URL; another URL is another gateway, kept.
                foreach (var server in serverObject.ToArray())
                    if (server.Key != request.Key && ClaudeDesktopEnvironment.SameExecutable(Command(server.Value), executable)
                        && SameUrl(UrlOf(server.Value), request.Url))
                    { aliases.Add(server.Key); serverObject.Remove(server.Key); }
                serverObject[request.Key] = new JsonObject
                {
                    ["command"] = executable,
                    ["args"] = new JsonArray(JsonValue.Create(request.Url!))
                };
                plan.Status = present ? $"updated from {ProxyRegistration.Q(oldUrl ?? "<missing URL>")}" : $"registered {ProxyRegistration.Q(request.Key)}";
            }
            else
            {
                var removed = serverObject?.Remove(request.Key) == true;
                plan.Status = removed ? $"removed {ProxyRegistration.Q(request.Key)}" : $"skipped: key {ProxyRegistration.Q(request.Key)} not present";
            }
            if (JsonNode.DeepEquals(originalNode, merged))
            {
                if (request.Register) plan.Status = "skipped: already registered";
                return plan;
            }
            foreach (var alias in aliases.Order(StringComparer.Ordinal)) plan.Status += $"; removed {ProxyRegistration.Q(alias)}";
            plan.Content = Serialize(merged, rootRaw, serverRaw, request.Register ? request.Key : null);
            return plan;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException or InvalidOperationException)
        {
            return new(target) { Reason = "not valid JSON or duplicate JSON member" };
        }
    }

    private static string? StringValue(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    private static string? Command(JsonNode? value) => value is JsonObject entry && entry.TryGetPropertyValue("command", out var command) ? StringValue(command) : null;
    // The same gateway address as Uri compares it: scheme and host case, default port and dot segments do not matter.
    private static bool SameUrl(string? stored, string? requested) =>
        Uri.TryCreate(stored, UriKind.Absolute, out var a) && Uri.TryCreate(requested, UriKind.Absolute, out var b) && a == b;
    private static string? UrlOf(JsonNode? entry) =>
        entry is JsonObject value && value["args"] is JsonArray args && args.Count > 0 ? StringValue(args[0]) : null;
    private static void CheckDuplicateNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                CheckDuplicateNames(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckDuplicateNames(child);
        else if (element.ValueKind == JsonValueKind.String)
            _ = element.GetString();
    }

    private static byte[] Serialize(JsonObject merged, Dictionary<string, string> originalRootRaw,
        Dictionary<string, string> originalServerRaw, string? changedServerKey)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var property in merged)
            {
                writer.WritePropertyName(property.Key);
                if (property.Key == "mcpServers" && property.Value is JsonObject servers)
                {
                    writer.WriteStartObject();
                    foreach (var server in servers)
                    {
                        writer.WritePropertyName(server.Key);
                        if (server.Key != changedServerKey && originalServerRaw.TryGetValue(server.Key, out var serverRaw))
                            writer.WriteRawValue(serverRaw, skipInputValidation: true);
                        else if (server.Value is null) writer.WriteNullValue();
                        else server.Value.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                else if (originalRootRaw.TryGetValue(property.Key, out var rootRaw)) writer.WriteRawValue(rootRaw, skipInputValidation: true);
                else if (property.Value is null) writer.WriteNullValue();
                else property.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    internal static Result Commit(IRegistrationFiles files, FilePlan plan)
    {
        if (plan.Reason is not null) return new(false, "refused: " + plan.Reason);
        if (plan.Content is null) return new(true, plan.Status);
        string? backup = null;
        string? temp = null;
        string? failure = null;
        var owned = false;
        bool MatchesSnapshot()
        {
            try
            {
                var attributes = files.Attributes(plan.Target.Path);
                if (plan.Original is null) return attributes is null;
                return attributes is not null && (attributes.Value & FileAttributes.Directory) == 0 &&
                    files.ReadAllBytes(plan.Target.Path).AsSpan().SequenceEqual(plan.Original);
            }
            catch (Exception) { return false; }
        }
        try
        {
            if (!MatchesSnapshot()) { failure = "config changed since it was read"; return Finish(); }
            if (plan.Target.Fallback)
            {
                try { files.CreateDirectory(Path.GetDirectoryName(plan.Target.Path)!); }
                catch (Exception) { failure = "cannot create classic config directory"; return Finish(); }
            }
            if (plan.Original is not null)
            {
                try
                {
                    backup = plan.Target.Path + "." + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N") + ".bak";
                    files.CopyFile(plan.Target.Path, backup);
                    if (!files.ReadAllBytes(backup).AsSpan().SequenceEqual(plan.Original)) throw new IOException();
                }
                catch (Exception) { failure = "cannot create or verify backup"; return Finish(); }
            }
            temp = Path.Combine(Path.GetDirectoryName(plan.Target.Path)!, "." + Filename + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try { files.WriteNewFile(temp, plan.Content); owned = true; }
            catch (RegistrationFiles.TemporaryCleanupException)
            { failure = "cannot write temporary config; cannot remove temporary config"; return Finish(); }
            catch (Exception) { failure = "cannot write temporary config"; return Finish(); }
            if (!MatchesSnapshot()) failure = "config changed since it was read";
            else
            {
                try { files.MoveFile(temp, plan.Target.Path, overwrite: plan.Original is not null); owned = false; }
                catch (Exception) { failure = "cannot replace config file"; }
            }
        }
        finally
        {
            if (owned && temp is not null)
            {
                try { files.DeleteFile(temp); }
                catch (Exception) { failure = (failure is null ? "" : failure + "; ") + "cannot remove temporary config"; }
            }
        }
        return Finish();

        Result Finish() => failure is not null ? new(false, "refused: " + failure) :
            new(true, plan.Status + (backup is null ? "" : $"; backup {ProxyRegistration.Q(backup)}") +
                (plan.Target.Fallback ? "; created classic config directory" : ""));
    }
}
