using System.Globalization;
using System.Text.Json;

namespace DataGateway.Proxy;

internal sealed record RegistrationRequest(bool Register, string Key, string? Url, Uri? Endpoint);
internal sealed record DesktopProcess(string Name, string? ExecutablePath);
internal sealed record DesktopPolicy(object? MachineValue, object? UserValue);
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

internal interface IRegistrationFiles
{
    FileAttributes? Attributes(string path);
    IReadOnlyList<string> Directories(string path);
    byte[] ReadAllBytes(string path);
    void CreateDirectory(string path);
    void CopyFile(string source, string destination);
    void WriteNewFile(string path, byte[] content);
    void MoveFile(string source, string destination, bool overwrite);
    void DeleteFile(string path);
}

internal static class ProxyRegistration
{
    internal static string Q(string value) => JsonSerializer.Serialize(value);

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
