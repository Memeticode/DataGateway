// DataGateway.Proxy: forwards stdio MCP to a gateway's /mcp endpoint as the Windows user.
// In server mode stdout carries only the protocol; logs go to stderr and a file.
// Registration CLI commands return before logging or the stdio server starts.
using DataGateway.Proxy;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

var registrationExit = await ProxyRegistration.TryHandleAsync(args);
if (registrationExit is int exitCode) return exitCode;

if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
{
    Console.Error.WriteLine("Usage: DataGateway.Proxy <URL of the server's /mcp endpoint>");
    return 2;
}

const string Template = "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("ModelContextProtocol", LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate: Template, standardErrorFromLevel: LogEventLevel.Verbose)
    .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "logs", "datagateway-proxy-.log"), outputTemplate: Template, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7, shared: true)
    .CreateLogger();
try
{
    using var loggers = new SerilogLoggerFactory(Log.Logger);
    var transport = new HttpClientTransport(
        new HttpClientTransportOptions { Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp },
        // No client-side timeout: HttpClient's default (100 s) would cut off a query the gateway still allows; the gateway
        // enforces its own CommandTimeoutSeconds, and the client cancels through the call's token.
        new HttpClient(new HttpClientHandler { UseDefaultCredentials = true }) { Timeout = Timeout.InfiniteTimeSpan }, loggers, ownsHttpClient: true);
    // Start-up keeps a limit: a gateway that takes the connection but never answers ends the proxy with exit 1.
    using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(100));
    await using var upstream = await McpClient.CreateAsync(transport, loggerFactory: loggers, cancellationToken: startup.Token);
    // Listing once fills the client's tool cache, which it checks on every call (an empty cache logs a warning per call).
    var tools = await upstream.ListToolsAsync(cancellationToken: startup.Token);
    Log.Information("Connected to {Endpoint}: {Server} {Version}, {Tools} tools", endpoint.ToString(), upstream.ServerInfo.Name, upstream.ServerInfo.Version, tools.Count);
    await using var stdio = McpServer.Create(new StdioServerTransport("DataGateway.Proxy", loggers), ProxyServer.Options(upstream, endpoint), loggers);
    await stdio.RunAsync();
    return 0;
}
catch (Exception error)
{
    Log.Fatal(error, "DataGateway.Proxy stopped: {Endpoint}", endpoint.ToString());
    return 1;
}
finally { await Log.CloseAndFlushAsync(); }
