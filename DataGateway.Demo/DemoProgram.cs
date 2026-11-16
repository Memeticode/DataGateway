namespace DataGateway.Demo;

public sealed record DemoArguments(Uri DataGateway, string? Source);

public static class DemoProgram
{
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
