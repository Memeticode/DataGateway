namespace DataGateway.Demo;

/// <summary>The demo's parsed command line.</summary>
/// <param name="DataGateway">The gateway's root URL, where the routes are mapped.</param>
/// <param name="Source">The source to exercise, or null for the first one listed.</param>
public sealed record DemoArguments(Uri DataGateway, string? Source);

/// <summary>The demo's entry point: a console client that calls a running gateway, checks every answer against
/// the documented wire format, and prints each step.</summary>
/// <remarks>It knows nothing about the catalog in advance: it learns source names from list and column abilities
/// from describe, so it runs against any registered source. It reads no configuration and needs no reference to
/// the gateway's code; the URL is its only input.</remarks>
public static class DemoProgram
{
    /// <summary>Parses the arguments: one absolute http or https root URL, optionally followed by
    /// <c>--source &lt;name&gt;</c>.</summary>
    /// <remarks>The URL must be a bare root, with no credentials, path, query or fragment: each route is sent as
    /// an absolute path from that root, so a path in the URL would be silently dropped.</remarks>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The arguments, or null with the usage text to print.</returns>
    public static (DemoArguments? Arguments, string? Error) Parse(IReadOnlyList<string> args)
    {
        const string usage = "FIX THE CALL: supply one absolute http(s) root URL and optionally --source <name>. Usage: dotnet run --project DataGateway.Demo -- http://localhost:5000 [--source <name>]";
        if (args.Count is not (1 or 3) || !Uri.TryCreate(args[0], UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (args.Count == 3 && (args[1] != "--source" || string.IsNullOrWhiteSpace(args[2]) || args[2].StartsWith("--", StringComparison.Ordinal))))
            return (null, usage);
        return (new(uri, args.Count == 3 ? args[2] : null), null);
    }

    /// <summary>Runs the demo and returns its exit code.</summary>
    /// <remarks>Two clients share the base address: one signs in with the current Windows account, one sends no
    /// credentials, for the step that checks the anonymous challenge. Ctrl+C cancels the run cleanly instead of
    /// killing the process.</remarks>
    /// <param name="args">The command-line arguments; see <see cref="Parse"/>.</param>
    /// <returns>0 when every step passed; 1 when a check failed, the run stopped, was cancelled or the gateway was
    /// unreachable; 2 for bad arguments.</returns>
    public static async Task<int> Main(string[] args)
    {
        var parsed = Parse(args);
        if (parsed.Arguments is null) { Console.Error.WriteLine(parsed.Error); return 2; }
        var arguments = parsed.Arguments;
        using var signedIn = new HttpClient(new HttpClientHandler { UseDefaultCredentials = true }) { BaseAddress = arguments.DataGateway };
        using var anonymous = new HttpClient(new HttpClientHandler()) { BaseAddress = arguments.DataGateway };
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var report = await DemoRun.RunAsync(signedIn, anonymous, arguments.Source, Console.Out, cancellation.Token);
            return report.Passed ? 0 : 1;
        }
        catch (HttpRequestException error)
        {
            Console.Error.WriteLine($"{arguments.DataGateway}: {error.HttpRequestError}: start DataGateway (cd DataGateway.Web; dotnet run)");
            return 1;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Run cancelled or client timeout expired."); return 1; }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
