using System.Globalization;
using System.Text.Json;

namespace DataGateway.Proxy;

/// <summary>A parsed registration command.</summary>
/// <param name="Register">True for <c>--register</c>, false for <c>--unregister</c>.</param>
/// <param name="Key">The server key under <c>mcpServers</c>: ASCII letters, digits, underscores and hyphens.</param>
/// <param name="Url">The gateway's <c>/mcp</c> URL as typed, written into the entry; null when unregistering.</param>
/// <param name="Endpoint">The same URL parsed, for listing the gateway's tools; null when unregistering.</param>
internal sealed record RegistrationRequest(bool Register, string Key, string? Url, Uri? Endpoint);
/// <summary>A running process that may be Claude Desktop.</summary>
/// <param name="Name">The process name.</param>
/// <param name="ExecutablePath">Its executable's path, or null when it cannot be read.</param>
internal sealed record DesktopProcess(string Name, string? ExecutablePath);
/// <summary>The <c>isLocalDevMcpEnabled</c> policy values, as stored; null where no policy is set.</summary>
/// <param name="MachineValue">The machine-wide value, which wins when present.</param>
/// <param name="UserValue">The current user's value.</param>
internal sealed record DesktopPolicy(object? MachineValue, object? UserValue);
/// <summary>Everything registration reads from the machine: paths, policy, processes, the gateway and the file
/// system.</summary>
/// <remarks>Each piece is a value or a delegate, so tests run the whole registration against a made-up machine,
/// with no Windows profile, Claude Desktop or gateway. <see cref="ClaudeDesktopEnvironment.CreateContext"/>
/// supplies the real ones.</remarks>
/// <param name="ExecutablePath">This program's path, written as the entry's <c>command</c>.</param>
/// <param name="IsWindows">Whether the machine runs Windows; registration refuses elsewhere.</param>
/// <param name="AppData">The roaming application-data folder.</param>
/// <param name="LocalAppData">The local application-data folder.</param>
/// <param name="ProgramFiles">The program-files folder, which holds packaged apps under <c>WindowsApps</c>.</param>
/// <param name="ClaudeUserDataDirectory">The <c>CLAUDE_USER_DATA_DIR</c> relocation, or null.</param>
/// <param name="ReadPolicy">Reads the organization policy.</param>
/// <param name="ReadProcesses">Lists the processes that may be Claude Desktop.</param>
/// <param name="ListToolsAsync">Lists the gateway's tool names, signed in as the current user.</param>
/// <param name="Files">The file operations.</param>
internal sealed record RegistrationContext(
    string? ExecutablePath,
    bool IsWindows,
    string AppData,
    string LocalAppData,
    string ProgramFiles,
    string? ClaudeUserDataDirectory,
    Func<DesktopPolicy> ReadPolicy,
    Func<IReadOnlyList<DesktopProcess>> ReadProcesses,
    Func<Uri, CancellationToken, Task<IReadOnlyList<string>>> ListToolsAsync,
    IRegistrationFiles Files);

/// <summary>The file operations registration uses, so tests can replace the file system.</summary>
internal interface IRegistrationFiles
{
    /// <summary>The attributes of a file or folder, or null when nothing exists there.</summary>
    FileAttributes? Attributes(string path);
    /// <summary>The folders directly inside a folder.</summary>
    IReadOnlyList<string> Directories(string path);
    /// <summary>The whole content of a file.</summary>
    byte[] ReadAllBytes(string path);
    /// <summary>Creates a folder and any missing parents.</summary>
    void CreateDirectory(string path);
    /// <summary>Copies a file to a path that must not exist yet.</summary>
    void CopyFile(string source, string destination);
    /// <summary>Writes content to a file that must not exist yet, removing the partial file if the write fails.</summary>
    void WriteNewFile(string path, byte[] content);
    /// <summary>Moves a file, replacing the destination when <paramref name="overwrite"/> is true.</summary>
    void MoveFile(string source, string destination, bool overwrite);
    /// <summary>Deletes a file.</summary>
    void DeleteFile(string path);
}

/// <summary>The <c>--register</c> and <c>--unregister</c> commands: add or remove this proxy's entry in every
/// Claude Desktop configuration file on the machine.</summary>
/// <remarks>
/// <para>Claude Desktop can keep its configuration in several places, depending on how it was installed and
/// whether its data folder was relocated, so registration finds and updates every eligible file
/// (<see cref="ClaudeConfigFiles.Discover"/>) rather than one fixed path.</para>
/// <para>It refuses, writing nothing, when the conditions for a safe edit are missing: Claude Desktop is running,
/// organization policy turns local MCP servers off, the program runs through the <c>dotnet</c> host (the entry
/// would name <c>dotnet</c>, not this program), or, when registering, the gateway cannot be reached or the key would
/// make a tool name too long.</para>
/// </remarks>
internal static class ProxyRegistration
{
    /// <summary>A value quoted as a JSON string, so a path or key prints unambiguously, odd characters included.</summary>
    internal static string Q(string value) => JsonSerializer.Serialize(value);

    /// <summary>Runs a registration command when the arguments are one, and returns its exit code; returns null
    /// otherwise, so the program goes on to server mode.</summary>
    /// <remarks>
    /// <para>Any first argument that starts with <c>-</c> is a registration command, valid or not; a gateway URL
    /// never starts with one.</para>
    /// <para>Before registering, the gateway's tools are listed with the user's Windows sign-in. Claude Desktop's
    /// remote bridge names a tool <c>mcp__remote-devices__&lt;key&gt;__&lt;tool&gt;</c>, at most 64 characters, so
    /// the key must leave room for the longest tool name: 21 characters of prefix, the key, 2 of separator, then the
    /// tool.</para>
    /// <para>A running Claude Desktop keeps its configuration in memory and writes it back over the file, silently
    /// undoing any outside edit, so registration refuses while any Desktop process runs. The app must be quit from
    /// its tray icon, because closing the window leaves it running.</para>
    /// <para>The running-Desktop check runs twice: first, before any work, and again after every file has been
    /// read and planned, just before writing, so a Desktop started meanwhile still stops the write. A plan that
    /// sets a barrier (a key owned by another program, or an <c>mcpServers</c> that is not an object) stops every
    /// file from being written; any other refused file only stops itself.</para>
    /// </remarks>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="createContext">Supplies the machine; null for the real one.</param>
    /// <param name="output">Receives the status lines; null for standard output.</param>
    /// <param name="error">Receives the usage text; null for standard error.</param>
    /// <param name="cancellationToken">Cancels the tool listing.</param>
    /// <returns>Null when the arguments are not a registration command; otherwise 0 when every file was written or
    /// skipped, 1 for a refusal or failure, and 2 for invalid arguments.</returns>
    internal static async Task<int?> TryHandleAsync(
        string[] args,
        Func<RegistrationContext>? createContext = null,
        TextWriter? output = null,
        TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || !args[0].StartsWith('-')) return null;
        output ??= Console.Out;
        error ??= Console.Error;
        var request = Parse(args);
        if (request is null)
        {
            error.WriteLine("Usage: DataGateway.Proxy --register <URL of the server's /mcp endpoint> --name <server key>");
            error.WriteLine("Usage: DataGateway.Proxy --unregister --name <server key>");
            return 2;
        }
        int Refuse(string reason)
        {
            output.WriteLine("refused: " + reason);
            output.WriteLine("Start Claude Desktop to load it.");
            return 1;
        }
        RegistrationContext context;
        try { context = (createContext ?? ClaudeDesktopEnvironment.CreateContext)(); }
        catch (Exception) { return Refuse("cannot resolve the required Windows profile folders"); }
        var executable = ClaudeDesktopEnvironment.FullCommandPath(context.ExecutablePath);
        if (executable is null) return Refuse("cannot determine an absolute apphost executable path");
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            return Refuse("launched via 'dotnet' host; run the apphost executable instead");
        if (!context.IsWindows) return Refuse("Claude Desktop registration is supported on Windows only");
        if (new[] { context.AppData, context.LocalAppData, context.ProgramFiles }
            .Any(root => ClaudeDesktopEnvironment.FullCommandPath(root) is null))
            return Refuse("cannot resolve the required Windows profile folders");
        try
        {
            var policy = context.ReadPolicy();
            if (ClaudeDesktopEnvironment.Disabled(policy.MachineValue ?? policy.UserValue))
                return Refuse("your organization's policy disables local MCP servers in Claude Desktop; ask your administrator");
        }
        catch (Exception) { return Refuse("cannot read Claude Desktop policy"); }
        string? ProcessRefusal()
        {
            try
            {
                return context.ReadProcesses().Any(process => ClaudeDesktopEnvironment.IsDesktop(process, context))
                    ? "quit Claude Desktop from the tray icon (not just the window), then run the command again" : null;
            }
            catch (Exception) { return "cannot inspect running Claude Desktop processes"; }
        }
        if (ProcessRefusal() is { } firstProcessError) return Refuse(firstProcessError);
        if (request.Register)
        {
            IReadOnlyList<string> names;
            try
            {
                names = await context.ListToolsAsync(request.Endpoint!, cancellationToken);
                if (names is null || names.Any(string.IsNullOrEmpty)) throw new InvalidDataException();
            }
            catch (Exception)
            {
                return Refuse("gateway could not be reached or its tools could not be listed; check the URL and your Windows access");
            }
            var longest = names.OrderByDescending(name => name.Length)
                .ThenBy(name => name, StringComparer.Ordinal).FirstOrDefault();
            var maximum = 64 - 21 - 2 - (longest?.Length ?? 0);
            if (maximum < request.Key.Length)
            {
                var lead = $"longest tool {Q(longest ?? "<none>")} leaves room for at most {maximum.ToString(CultureInfo.InvariantCulture)} key characters; ";
                return Refuse(lead + (maximum > 0
                    ? $"use {Q(request.Key[..maximum])} or a shorter --name" : "no nonempty --name fits"));
            }
        }
        var discovery = ClaudeConfigFiles.Discover(context, request.Register);
        var plans = discovery.Targets.Select(target => ClaudeConfigFiles.Plan(context.Files, target, request, executable)).ToArray();
        var barrier = plans.Any(plan => plan.Barrier);
        // All reads and planning precede this last operational guard, including no-op plans.
        if (ProcessRefusal() is { } lastProcessError)
        {
            foreach (var line in discovery.Errors) output.WriteLine(line);
            if (plans.Length == 0) return Refuse(lastProcessError);
            foreach (var plan in plans)
                output.WriteLine($"{Q(plan.Target.Path)}: refused: {lastProcessError}");
            output.WriteLine("Start Claude Desktop to load it.");
            return 1;
        }
        var failed = discovery.Errors.Count != 0;
        foreach (var line in discovery.Errors) output.WriteLine(line);
        foreach (var plan in plans)
        {
            if (barrier && plan.Reason is null)
                plan.Reason = "another config has a name conflict or invalid mcpServers; no configs were changed";
            var result = ClaudeConfigFiles.Commit(context.Files, plan);
            failed |= !result.Success;
            output.WriteLine($"{Q(plan.Target.Path)}: {result.Status}");
        }
        if (!request.Register && plans.Length == 0) output.WriteLine("skipped: no Claude Desktop configs found");
        output.WriteLine("Start Claude Desktop to load it.");
        return failed ? 1 : 0;
    }

    /// <summary>Parses exactly <c>--register &lt;url&gt; --name &lt;key&gt;</c> or
    /// <c>--unregister --name &lt;key&gt;</c>.</summary>
    /// <returns>The request, or null for any other shape, a URL that is not absolute http or https, or a key with
    /// other characters.</returns>
    private static RegistrationRequest? Parse(string[] args)
    {
        var register = args[0] == "--register";
        string key;
        string? url = null;
        Uri? endpoint = null;
        if (register && args.Length == 4 && args[2] == "--name")
        {
            key = args[3];
            url = args[1];
            if (!Uri.TryCreate(url, UriKind.Absolute, out endpoint) || endpoint.Scheme is not ("http" or "https")) return null;
        }
        else if (args[0] == "--unregister" && args.Length == 3 && args[1] == "--name") key = args[2];
        else return null;
        if (key.Length == 0 || key.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))) return null;
        return new(register, key, url, endpoint);
    }
}
