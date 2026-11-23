using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using ModelContextProtocol.Client;

namespace DataGateway.Proxy;

/// <summary>What registration knows about Claude Desktop on Windows: where it is installed, how to tell it is
/// running, its policy, and path comparison as Windows does it.</summary>
/// <remarks>Paths compare ignoring case, as Windows paths do, whatever operating system runs the code, and accept
/// either slash. A path that is not fully qualified is never treated as a match.</remarks>
internal static class ClaudeDesktopEnvironment
{
    /// <summary>The real machine: this program's path, the profile folders, the relocation variable, and the live
    /// policy, process list, gateway client and file system.</summary>
    /// <remarks><c>CLAUDE_USER_DATA_DIR</c> is read from this process first, then on Windows from the user's
    /// stored environment variables.</remarks>
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

    /// <summary>The full, normalized form of a path, with either slash accepted; null for an empty, relative or
    /// malformed path.</summary>
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

    /// <summary>Whether two paths name the same file, ignoring case; false when either is not a full path.</summary>
    internal static bool SameExecutable(string? left, string right)
    {
        var a = FullCommandPath(left);
        var b = FullCommandPath(right);
        return a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a path lies strictly inside a folder, ignoring case.</summary>
    /// <remarks>The folder is compared with a trailing separator, so a sibling whose name merely starts the same
    /// way (<c>Claude</c> and <c>Claude-3p</c>) is not inside it.</remarks>
    internal static bool Under(string path, string root)
    {
        var normalized = FullCommandPath(path);
        var parent = FullCommandPath(root)?.TrimEnd(Path.DirectorySeparatorChar);
        return normalized is not null && parent is not null &&
            normalized.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a folder name is a Claude Desktop package folder: <c>Claude_</c> or
    /// <c>AnthropicPBC.Claude_</c> followed by at least one more character.</summary>
    internal static bool PackageName(string name) =>
        (name.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase) && name.Length > "Claude_".Length) ||
        (name.StartsWith("AnthropicPBC.Claude_", StringComparison.OrdinalIgnoreCase) && name.Length > "AnthropicPBC.Claude_".Length);

    /// <summary>Whether a process is Claude Desktop: named claude, running <c>Claude.exe</c> from the standard
    /// install folder or from a Claude package under <c>WindowsApps</c>.</summary>
    /// <remarks>Name and path both count, so another program whose process is also named claude does not stop
    /// registration.</remarks>
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

    /// <summary>Whether a policy value turns local MCP servers off: false, a zero of any integer type, or the text
    /// <c>0</c> or <c>false</c>.</summary>
    /// <remarks>Any other value, absence included, leaves them on: only a recognizable "off" refuses.</remarks>
    internal static bool Disabled(object? value) => value switch
    {
        bool flag => !flag,
        sbyte n => n == 0, byte n => n == 0, short n => n == 0, ushort n => n == 0,
        int n => n == 0, uint n => n == 0, long n => n == 0, ulong n => n == 0,
        string text => text.Trim().Equals("0", StringComparison.OrdinalIgnoreCase) || text.Trim().Equals("false", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    /// <summary>Reads <c>isLocalDevMcpEnabled</c> under <c>SOFTWARE\Policies\Claude</c>, machine and user, from the
    /// registry view that matches the operating system.</summary>
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

    /// <summary>Lists the processes named claude with their executable paths.</summary>
    /// <remarks>A process that exits or cannot be inspected meanwhile is skipped, so one inaccessible process does
    /// not stop the check.</remarks>
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

    /// <summary>Lists the gateway's tool names, signed in with the current user's Windows account.</summary>
    /// <param name="endpoint">The gateway's <c>/mcp</c> URL.</param>
    /// <param name="token">Cancels the listing.</param>
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

/// <summary>The real file operations, retrying briefly when another process holds a file.</summary>
/// <remarks>Retries cover access denials and sharing or lock violations, failures that can clear once another
/// process releases the file; any other failure is reported at once.</remarks>
internal sealed class RegistrationFiles : IRegistrationFiles
{
    /// <summary>A failed write whose partial file could not be removed either, so the caller can report both.</summary>
    internal sealed class TemporaryCleanupException : IOException { }
    /// <summary>How many times an operation is tried in all.</summary>
    private readonly int attempts;
    /// <summary>The pause between tries, in milliseconds.</summary>
    private readonly int pauseMilliseconds;
    /// <summary>Pauses for the given milliseconds; replaceable so tests do not wait.</summary>
    private readonly Action<int> sleep;

    /// <summary>The real operations: ten tries, 50 milliseconds apart.</summary>
    internal RegistrationFiles() : this(10, 50, Thread.Sleep) { }

    /// <summary>The operations with a chosen retry schedule.</summary>
    /// <param name="attempts">How many times an operation is tried in all; at least 1.</param>
    /// <param name="pauseMilliseconds">The pause between tries; not negative.</param>
    /// <param name="sleep">Pauses for the given milliseconds.</param>
    internal RegistrationFiles(int attempts, int pauseMilliseconds, Action<int> sleep)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(pauseMilliseconds);
        ArgumentNullException.ThrowIfNull(sleep);
        this.attempts = attempts;
        this.pauseMilliseconds = pauseMilliseconds;
        this.sleep = sleep;
    }

    /// <summary>Runs an operation, trying again after a pause while it fails with an access denial, a sharing
    /// violation (0x80070020) or a lock violation (0x80070021) and tries remain.</summary>
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
    /// <inheritdoc/>
    public FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    /// <inheritdoc/>
    public IReadOnlyList<string> Directories(string path) => Directory.GetDirectories(path);
    /// <inheritdoc/>
    public byte[] ReadAllBytes(string path)
    {
        byte[] content = [];
        Retry(() => content = File.ReadAllBytes(path));
        return content;
    }
    /// <inheritdoc/>
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    /// <inheritdoc/>
    public void CopyFile(string source, string destination) => Retry(() => File.Copy(source, destination, overwrite: false));
    /// <inheritdoc/>
    /// <remarks>The file is created exclusively, so an existing file is never overwritten, and flushed to disk
    /// before it counts as written. When the write fails, the partial file is deleted; if that fails too,
    /// <see cref="TemporaryCleanupException"/> is thrown.</remarks>
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
    /// <inheritdoc/>
    public void MoveFile(string source, string destination, bool overwrite) => Retry(() => File.Move(source, destination, overwrite));
    /// <inheritdoc/>
    public void DeleteFile(string path) => Retry(() => File.Delete(path));
}
