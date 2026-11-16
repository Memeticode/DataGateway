using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using ModelContextProtocol.Client;

namespace DataGateway.Proxy;

internal static class ClaudeDesktopEnvironment
{
    internal static RegistrationContext CreateContext()
    {
        var windows = OperatingSystem.IsWindows();
        var relocation = Environment.GetEnvironmentVariable("CLAUDE_USER_DATA_DIR");
        if (string.IsNullOrEmpty(relocation) && windows)
            relocation = Environment.GetEnvironmentVariable("CLAUDE_USER_DATA_DIR", EnvironmentVariableTarget.User);
        return new(Environment.ProcessPath, windows,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), relocation,
            () => OperatingSystem.IsWindows() ? ReadPolicy() : throw new PlatformNotSupportedException(),
            ReadProcesses, ListToolNamesAsync, new RegistrationFiles());
    }

    internal static string? FullCommandPath(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            var path = value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    internal static bool SameExecutable(string? left, string right)
    {
        var a = FullCommandPath(left);
        var b = FullCommandPath(right);
        return a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool Under(string path, string root)
    {
        var normalized = FullCommandPath(path);
        var parent = FullCommandPath(root)?.TrimEnd(Path.DirectorySeparatorChar);
        return normalized is not null && parent is not null &&
            normalized.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool PackageName(string name) =>
        (name.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase) && name.Length > "Claude_".Length) ||
        (name.StartsWith("AnthropicPBC.Claude_", StringComparison.OrdinalIgnoreCase) && name.Length > "AnthropicPBC.Claude_".Length);

    internal static bool IsDesktop(DesktopProcess process, RegistrationContext context)
    {
        if (!string.Equals(process.Name, "claude", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(process.Name, "claude.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var path = FullCommandPath(process.ExecutablePath);
        if (path is null || !string.Equals(Path.GetFileName(path), "Claude.exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (Under(path, Path.Combine(context.LocalAppData, "AnthropicClaude"))) return true;
        var packages = FullCommandPath(Path.Combine(context.ProgramFiles, "WindowsApps"))!;
        if (!Under(path, packages)) return false;
        // Under already checked the boundary ignoring case, independently of the host OS.
        var segments = path[(packages.TrimEnd(Path.DirectorySeparatorChar).Length + 1)..].Split(Path.DirectorySeparatorChar);
        return segments.Length >= 2 && PackageName(segments[0]);
    }

    internal static bool Disabled(object? value) => value switch
    {
        bool flag => !flag,
        sbyte n => n == 0, byte n => n == 0, short n => n == 0, ushort n => n == 0,
        int n => n == 0, uint n => n == 0, long n => n == 0, ulong n => n == 0,
        string text => text.Trim().Equals("0", StringComparison.OrdinalIgnoreCase) || text.Trim().Equals("false", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    [SupportedOSPlatform("windows")]
    private static DesktopPolicy ReadPolicy()
    {
        var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
        object? Read(RegistryHive hive)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var policy = root.OpenSubKey(@"SOFTWARE\Policies\Claude", writable: false);
            return policy?.GetValue("isLocalDevMcpEnabled", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        return new(Read(RegistryHive.LocalMachine), Read(RegistryHive.CurrentUser));
    }

    private static IReadOnlyList<DesktopProcess> ReadProcesses()
    {
        var processes = Process.GetProcesses();
        var result = new List<DesktopProcess>();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var name = process.ProcessName;
                    if (name.Equals("claude", StringComparison.OrdinalIgnoreCase) || name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase))
                        result.Add(new(name, process.MainModule?.FileName));
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
                { /* An individual inaccessible/exited process is explicitly ignored. */ }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return result;
    }

    internal static async Task<IReadOnlyList<string>> ListToolNamesAsync(Uri endpoint, CancellationToken token)
    {
        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp },
            new HttpClient(new HttpClientHandler { UseDefaultCredentials = true }), ownsHttpClient: true);
        await using var upstream = await McpClient.CreateAsync(transport, cancellationToken: token);
        var tools = await upstream.ListToolsAsync(cancellationToken: token);
        return tools.Select(tool => tool.Name).ToArray();
    }
}

internal sealed class RegistrationFiles : IRegistrationFiles
{
    internal sealed class TemporaryCleanupException : IOException { }
    private readonly int attempts;
    private readonly int pauseMilliseconds;
    private readonly Action<int> sleep;

    internal RegistrationFiles() : this(10, 50, Thread.Sleep) { }

    internal RegistrationFiles(int attempts, int pauseMilliseconds, Action<int> sleep)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(pauseMilliseconds);
        ArgumentNullException.ThrowIfNull(sleep);
        this.attempts = attempts;
        this.pauseMilliseconds = pauseMilliseconds;
        this.sleep = sleep;
    }

    private void Retry(Action operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception error) when (attempt < attempts &&
                (error is UnauthorizedAccessException ||
                 error is IOException { HResult: unchecked((int)0x80070020) or unchecked((int)0x80070021) }))
            {
                sleep(pauseMilliseconds);
            }
        }
    }
    public FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    public IReadOnlyList<string> Directories(string path) => Directory.GetDirectories(path);
    public byte[] ReadAllBytes(string path)
    {
        byte[] content = [];
        Retry(() => content = File.ReadAllBytes(path));
        return content;
    }
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void CopyFile(string source, string destination) => Retry(() => File.Copy(source, destination, overwrite: false));
    public void WriteNewFile(string path, byte[] content) => Retry(() =>
    {
        var owned = false;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            owned = true;
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            if (owned)
            {
                try { File.Delete(path); }
                catch (Exception) { throw new TemporaryCleanupException(); }
            }
            throw;
        }
    });
    public void MoveFile(string source, string destination, bool overwrite) => Retry(() => File.Move(source, destination, overwrite));
    public void DeleteFile(string path) => Retry(() => File.Delete(path));
}
