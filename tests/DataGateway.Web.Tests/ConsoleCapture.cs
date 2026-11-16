using System.Text.RegularExpressions;

namespace DataGateway.Web.Tests;

internal sealed class ConsoleCapture : IDisposable
{
    private readonly TextWriter output = Console.Out;
    private readonly TextWriter error = Console.Error;
    private readonly StringWriter stdout = new();
    private readonly StringWriter stderr = new();
    internal ConsoleCapture()
    {
        Console.SetOut(TextWriter.Synchronized(stdout));
        Console.SetError(TextWriter.Synchronized(stderr));
    }
    internal string[] Lines => Regex.Replace(stdout.ToString(), @"\x1B\[[0-?]*[ -/]*[@-~]", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\r')).ToArray();
    internal string Errors => stderr.ToString();
    public void Dispose()
    {
        Console.SetOut(output);
        Console.SetError(error);
        stdout.Dispose();
        stderr.Dispose();
    }
}
