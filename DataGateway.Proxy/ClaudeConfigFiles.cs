using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataGateway.Proxy;

/// <summary>Finds every Claude Desktop configuration file on the machine, plans the change to each, and writes
/// each change safely.</summary>
/// <remarks>
/// <para>TERM: tier A and tier B. A tier A folder is a known Claude Desktop data folder: the
/// relocated data folder, the classic <c>%APPDATA%\Claude</c>, a packaged install's roaming <c>Claude</c> folder,
/// and the <c>Claude-3p</c> folders directly under the two profile roots. When a tier A folder exists, registering
/// writes its file, creating the file if needed. A tier B folder is one that may hold a configuration file; only a
/// file that already exists there is updated, never created.</para>
/// <para>Some places are never touched: Claude Desktop's own install and package folders, managed configuration,
/// backups and templates, and other clients' folders (<see cref="ExcludedRelocation"/>).</para>
/// <para>Planning reads and parses every file before anything is written (<see cref="Plan"/>). Writing
/// (<see cref="Commit"/>) goes through a temporary file, backs up an existing file first and verifies the backup,
/// and replaces a file only if it is still exactly what was read.</para>
/// </remarks>
internal static class ClaudeConfigFiles
{
    /// <summary>The configuration file's name in every folder.</summary>
    internal const string Filename = "claude_desktop_config.json";
    /// <summary>One configuration file to plan.</summary>
    /// <param name="Path">Its full path.</param>
    /// <param name="TierA">Whether it may be created when absent (see the type's remarks).</param>
    /// <param name="Fallback">Whether it is the classic file chosen because nothing else was found, whose folder
    /// may need creating.</param>
    internal sealed record Target(string Path, bool TierA, bool Fallback = false);
    /// <summary>The files found, and the candidate folders that could not be inspected.</summary>
    /// <param name="Targets">The files, ordered by path.</param>
    /// <param name="Errors">One printable refusal line per folder that could not be inspected.</param>
    internal sealed record Discovery(IReadOnlyList<Target> Targets, IReadOnlyList<string> Errors);
    /// <summary>The planned change to one file: its bytes as read, its new bytes, or why it is refused.</summary>
    internal sealed class FilePlan(Target target)
    {
        /// <summary>The file.</summary>
        internal Target Target { get; } = target;
        /// <summary>The bytes as read; null when the file did not exist.</summary>
        internal byte[]? Original { get; set; }
        /// <summary>The bytes to write; null when nothing changes.</summary>
        internal byte[]? Content { get; set; }
        /// <summary>Why this file is refused; null when it can proceed.</summary>
        internal string? Reason { get; set; }
        /// <summary>Whether this refusal stops every other file too.</summary>
        /// <remarks>Set for a key owned by another program and for an <c>mcpServers</c> that is not an object.
        /// An unreadable or unparseable file only stops itself.</remarks>
        internal bool Barrier { get; set; }
        /// <summary>The status line for a file that proceeds, such as registered, updated, removed or skipped.</summary>
        internal string Status { get; set; } = "";
    }
    /// <summary>The outcome of committing one plan.</summary>
    /// <param name="Success">Whether the file was written or rightly skipped.</param>
    /// <param name="Status">The status line to print.</param>
    internal sealed record Result(bool Success, string Status);

    /// <summary>Finds the configuration files to plan, in tier A and tier B folders.</summary>
    /// <remarks>
    /// <para>A file found by two routes is planned once, as tier A if either route is. A candidate folder that
    /// cannot be inspected, or that is a file, becomes one error line and is left out; the other folders still
    /// count.</para>
    /// <para>When registering finds no file and no error, the classic <c>%APPDATA%\Claude</c> file is the target,
    /// and its folder is created if needed.</para>
    /// </remarks>
    /// <param name="context">The machine.</param>
    /// <param name="register">Whether this is a registration, which alone may fall back to the classic file.</param>
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

    /// <summary>Whether a folder must never be written: inside Claude Desktop's install or package folders or its
    /// modified-copy folder, or with a path segment that marks another client, managed configuration, a backup or a
    /// template.</summary>
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

    /// <summary>Reads one file and plans its change, without writing anything.</summary>
    /// <remarks>
    /// <para>The file must be UTF-8 JSON (a BOM is allowed) whose root is an object, with no duplicate member at
    /// any depth and no invalid escaped string; anything else refuses this file only, and the file stays as it
    /// is.</para>
    /// <para>Registering refuses a key whose entry runs a different program. It replaces this proxy's other
    /// entries for the same gateway URL (old names for the same thing), and keeps entries for other URLs, which
    /// are other gateways. A file that would come out identical gets no new content.</para>
    /// </remarks>
    /// <param name="files">The file operations.</param>
    /// <param name="target">The file.</param>
    /// <param name="request">The command.</param>
    /// <param name="executable">This program's full path, the entry's <c>command</c>.</param>
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

    /// <summary>A JSON string value, or null for anything else.</summary>
    private static string? StringValue(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    /// <summary>An entry's <c>command</c>, or null when it has no string command.</summary>
    private static string? Command(JsonNode? value) => value is JsonObject entry && entry.TryGetPropertyValue("command", out var command) ? StringValue(command) : null;
    /// <summary>Whether a stored entry's URL is the requested gateway URL.</summary>
    /// <remarks>The same gateway address as Uri compares it: scheme and host case, default port and dot segments do not matter.</remarks>
    private static bool SameUrl(string? stored, string? requested) =>
        Uri.TryCreate(stored, UriKind.Absolute, out var a) && Uri.TryCreate(requested, UriKind.Absolute, out var b) && a == b;
    /// <summary>An entry's first argument, which for this proxy is the gateway URL.</summary>
    private static string? UrlOf(JsonNode? entry) =>
        entry is JsonObject value && value["args"] is JsonArray args && args.Count > 0 ? StringValue(args[0]) : null;
    /// <summary>Throws a <see cref="JsonException"/> for a duplicate member name at any depth.</summary>
    /// <remarks>Reading each string also throws for an invalid escaped surrogate, so a file with one is refused
    /// too.</remarks>
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

    /// <summary>Writes the merged configuration, indented, copying every member that did not change as its
    /// original raw text.</summary>
    /// <remarks>Only the changed server entry is written from the parsed model. Every other root member and server
    /// entry keeps its original text, so number formats, escapes and spacing inside the user's other settings come
    /// through untouched.</remarks>
    /// <param name="merged">The configuration after the change.</param>
    /// <param name="originalRootRaw">The original raw text of each root member.</param>
    /// <param name="originalServerRaw">The original raw text of each server entry.</param>
    /// <param name="changedServerKey">The key that was written, or null when unregistering.</param>
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

    /// <summary>Writes one plan, or reports why it was not written.</summary>
    /// <remarks>
    /// <para>The file is replaced in steps: check it is still the bytes that were read; if it exists, copy it to a
    /// timestamped backup beside it and check the copy; write the new content to a temporary file in the same
    /// folder; check the original once more; then move the temporary file over it. Any failed step stops the write
    /// and leaves the original in place.</para>
    /// <para>The second check catches a change made while the temporary file was written. A temporary file left
    /// behind by a failed step is removed, and a failed removal is reported with the failure.</para>
    /// </remarks>
    /// <param name="files">The file operations.</param>
    /// <param name="plan">The plan.</param>
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
