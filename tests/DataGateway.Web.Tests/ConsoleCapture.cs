using System.Text.RegularExpressions;

namespace DataGateway.Web.Tests;

/// <summary>Redirects the process's standard output and error to memory for the life of one test, so a test can
/// read what the host wrote to the console.</summary>
/// <remarks>The console is process-wide, so a capture is only reliable while no other test writes to it: this test
/// assembly runs its tests one at a time (see <see cref="ConfigurationTests"/>), and the console log tests also share
/// the non-parallel <see cref="ConsoleCaptureCollection"/>. The writers are synchronized because the host can write
/// from several threads at once.</remarks>
internal sealed class ConsoleCapture : IDisposable
{
    /// <summary>The standard output in place before the capture, restored on dispose.</summary>
    private readonly TextWriter output = Console.Out;
    /// <summary>The standard error in place before the capture, restored on dispose.</summary>
    private readonly TextWriter error = Console.Error;
    /// <summary>The captured standard output.</summary>
    private readonly StringWriter stdout = new();
    /// <summary>The captured standard error.</summary>
    private readonly StringWriter stderr = new();
    /// <summary>Starts capturing both streams.</summary>
    internal ConsoleCapture()
    {
        Console.SetOut(TextWriter.Synchronized(stdout));
        Console.SetError(TextWriter.Synchronized(stderr));
    }
    /// <summary>The non-empty lines written to standard output, with ANSI color codes and carriage returns removed, so
    /// assertions compare plain text whatever console formatter colored it.</summary>
    internal string[] Lines => Regex.Replace(stdout.ToString(), @"\x1B\[[0-?]*[ -/]*[@-~]", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\r')).ToArray();
    /// <summary>Everything written to standard error, unchanged.</summary>
    internal string Errors => stderr.ToString();
    /// <summary>Restores the original console streams and releases the buffers.</summary>
    public void Dispose()
    {
        Console.SetOut(output);
        Console.SetError(error);
        stdout.Dispose();
        stderr.Dispose();
    }
}
