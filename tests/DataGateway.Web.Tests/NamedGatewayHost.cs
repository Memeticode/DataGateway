using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataGateway.Core.Errors;
using System.Text.Encodings.Web;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Sqlite;
using DataGateway.Failures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>A real Kestrel host on a loopback port for tests of named gateways: each test registers its own
/// gateways and pipeline, then calls the host over HTTP.</summary>
/// <remarks>
/// <para>The host starts from an empty configuration and asserts it stays empty, so no appsettings file, user
/// secret or environment variable on the test machine can change what a test sees.</para>
/// <para>It listens on port 0 and reads the bound address back, so tests can run side by side without port
/// clashes. Authentication and authorization run after the test's pipeline, so a test can map endpoints that
/// require a synthetic signed-in caller (<see cref="NamedGatewayAuthentication"/>).</para>
/// </remarks>
internal sealed class NamedGatewayHost : IAsyncDisposable
{
    /// <summary>The built application, for mapping endpoints and reading its services.</summary>
    internal WebApplication App { get; }
    /// <summary>A client bound to the listening address; replaced by <see cref="Start"/>.</summary>
    internal HttpClient Client { get; private set; } = new();
    /// <summary>Every log event the host wrote, the gateway's failure lines among them.</summary>
    internal NamedGatewayLogs Logs { get; } = new();
    /// <summary>Records which request-scoped dependencies were disposed, so tests can prove scope cleanup.</summary>
    internal NamedGatewayCleanup Cleanup { get; } = new();

    /// <summary>Builds the host without starting it.</summary>
    /// <param name="configure">Registers the test's gateways and services before the app is built.</param>
    /// <param name="pipeline">Adds the test's middleware before authentication and authorization.</param>
    internal NamedGatewayHost(Action<WebApplicationBuilder>? configure = null, Action<WebApplication>? pipeline = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(NamedGatewayHost).Assembly.GetName().Name, Args = [], EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        Assert.Empty(builder.Configuration.AsEnumerable());
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(Logs);
        builder.Services.AddSingleton(Cleanup);
        builder.Services.AddScoped<NamedGatewayDependency>();
        builder.Services.AddAuthentication("NamedSynthetic").AddScheme<AuthenticationSchemeOptions, NamedGatewayAuthentication>("NamedSynthetic", _ =>
        {
        });
        builder.Services.AddAuthorization();
        configure?.Invoke(builder);
        Assert.Empty(builder.Configuration.AsEnumerable());
        App = builder.Build();
        pipeline?.Invoke(App);
        App.UseAuthentication();
        App.UseAuthorization();
    }

    /// <summary>Registers one named gateway over a test catalog and an engine (a fresh <see cref="NamedGatewayEngine"/>
    /// when none is given).</summary>
    internal static void Register(IServiceCollection services, string name, NamedGatewayState state, IDatabaseEngine? engine = null) => services.AddDataGateway(name, gateway => gateway.UseCatalog(state.Create).AddEngine(engine ?? new NamedGatewayEngine()));
    /// <summary>Starts the host within 15 seconds and points <see cref="Client"/> at the one address it bound.</summary>
    internal async Task Start(CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await App.StartAsync(timeout.Token);
        Client.Dispose();
        var addresses = App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Client = new HttpClient
        {
            BaseAddress = new Uri(Assert.Single(addresses)),
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    /// <summary>Every route endpoint the host mapped, gateway and host-owned alike.</summary>
    internal RouteEndpoint[] Endpoints => ((IEndpointRouteBuilder)App).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();

    /// <summary>Posts one gateway operation and returns its status, content type and bytes.</summary>
    /// <param name="prefix">The gateway's mapped prefix, such as <c>/north</c>.</param>
    /// <param name="operation"><c>list</c>, <c>describe</c> or <c>query</c>.</param>
    /// <param name="body">The JSON body; the operation's default from <see cref="Body"/> when null.</param>
    /// <param name="signed">Sends the synthetic sign-in header.</param>
    internal async Task<NamedGatewayReply> Post(string prefix, string operation, string? body = null, bool signed = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, prefix + PathOf(operation))
        {
            Content = new StringContent(body ?? Body(operation), Encoding.UTF8, "application/json")
        };
        if (signed)
            request.Headers.Add("X-Synthetic-User", "synthetic-reader");
        using var response = await Client.SendAsync(request);
        return new((int)response.StatusCode, response.Content.Headers.ContentType?.ToString(), await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>The route below a gateway prefix for an operation name; anything other than list or describe is the
    /// query route.</summary>
    internal static string PathOf(string operation) => operation switch
    {
        "list" => "/sources/list",
        "describe" => "/sources/describe",
        _ => "/query"
    };
    /// <summary>A valid request body for an operation: the query selects ids 1 and 2 in order, so replies are
    /// predictable.</summary>
    internal static string Body(string operation, string name = "synthetic_items") => operation switch
    {
        "list" => "{}",
        "describe" => System.Text.Json.JsonSerializer.Serialize(new { source = name }),
        _ => System.Text.Json.JsonSerializer.Serialize(new { source = name, query = new { select = new[] { "id" }, order_by = new[] { new { col = "id", dir = "asc" } }, limit = 2 } })};
    /// <summary>Stops the host and releases the client.</summary>
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }
}

/// <summary>One HTTP reply as raw bytes, so tests can compare the exact wire output.</summary>
internal sealed record NamedGatewayReply(int Status, string? ContentType, byte[] Bytes)
{
    /// <summary>The reply body as UTF-8 text.</summary>
    internal string Text => Encoding.UTF8.GetString(Bytes);
}

/// <summary>A test sign-in scheme: a request with a nonblank <c>X-Synthetic-User</c> header is signed in as that
/// name; any other request is anonymous.</summary>
/// <remarks>Lets tests exercise signed and anonymous callers, and the caller name in failure logs, without a real
/// identity provider.</remarks>
internal sealed class NamedGatewayAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Request.Headers.TryGetValue("X-Synthetic-User", out var name) && !string.IsNullOrWhiteSpace(name) ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name.ToString())], Scheme.Name)), Scheme.Name)) : AuthenticateResult.NoResult());
}

/// <summary>A logger provider that keeps every event in memory with its structured fields and scope values.</summary>
/// <remarks>Tests assert on the fields (incident id, code, caller) and on the <c>Gateway</c> scope, not on
/// formatted text alone, because the failure log is a structured contract.</remarks>
internal sealed class NamedGatewayLogs : ILoggerProvider, ISupportExternalScope
{
    /// <summary>The scope stack; the host replaces it with its own through <see cref="SetScopeProvider"/>.</summary>
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
    /// <summary>Every event in the order written.</summary>
    internal ConcurrentQueue<NamedGatewayEvent> Events { get; } = new();
    /// <summary>The events in the gateway's failure log category.</summary>
    internal NamedGatewayEvent[] Failures => Events.Where(item => item.Category == FailureLog.Category).ToArray();

    /// <summary>Takes the host's scope provider, so scopes opened by the gateway reach recorded events.</summary>
    public void SetScopeProvider(IExternalScopeProvider provider) => scopes = provider;
    /// <summary>Creates a logger that records into this provider.</summary>
    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);
    /// <summary>Nothing to release: events stay readable after the host stops.</summary>
    public void Dispose()
    {
    }

    /// <summary>The logger for one category; every level is enabled so nothing is filtered out of a test.</summary>
    private sealed class Recorder(NamedGatewayLogs owner, string category) : ILogger
    {
        /// <summary>Pushes a scope onto the shared scope stack.</summary>
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner.scopes.Push(state);
        /// <summary>Always true: tests see every level.</summary>
        public bool IsEnabled(LogLevel level) => true;
        /// <summary>Records the event with its state fields, the merged fields of every active scope, the formatted text
        /// and the exception text.</summary>
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value) : new Dictionary<string, object?>();
            var scopeFields = new Dictionary<string, object?>();
            owner.scopes.ForEachScope((scope, values) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> properties)
                    foreach (var property in properties)
                        values[property.Key] = property.Value;
            }, scopeFields);
            owner.Events.Enqueue(new(category, level, fields, scopeFields, formatter(state, exception), exception?.ToString()));
        }
    }
}

/// <summary>One recorded log event.</summary>
/// <param name="Category">The logger category.</param>
/// <param name="Level">The log level.</param>
/// <param name="Fields">The structured state fields, including <c>{OriginalFormat}</c>.</param>
/// <param name="Scopes">The fields of every scope active when the event was written.</param>
/// <param name="Text">The formatted message.</param>
/// <param name="Exception">The attached exception's text, or null.</param>
internal sealed record NamedGatewayEvent(string Category, LogLevel Level, IReadOnlyDictionary<string, object?> Fields, IReadOnlyDictionary<string, object?> Scopes, string Text, string? Exception);
/// <summary>A SQLite engine wrapper that records every compiled and executed query and lets a test inject faults,
/// delays and changed rows.</summary>
/// <remarks>Real SQLite does the work, so the gateway runs end to end; the wrapper only observes and interferes.
/// The provider name is configurable so tests can register an engine under a name the catalog does or does not
/// ask for.</remarks>
internal sealed class NamedGatewayEngine(string provider = "sqlite") : IDatabaseEngine
{
    /// <summary>The real engine that compiles and runs the queries.</summary>
    private readonly IDatabaseEngine inner = new SqliteEngine();
    /// <summary>Every query compiled, in order.</summary>
    internal ConcurrentQueue<CompiledQuery> Compiled { get; } = new();
    /// <summary>Every query sent to the database, with the connection and timeout it ran with.</summary>
    internal ConcurrentQueue<(string Connection, CompiledQuery Query, int Timeout)> Executed { get; } = new();
    /// <summary>When set and it returns an exception, compiling throws it.</summary>
    internal Func<Exception?>? CompileFault { get; set; }
    /// <summary>Runs before each database read, so a test can hold a query in flight or cancel it.</summary>
    internal Func<CancellationToken, Task>? BeforeExecute { get; set; }
    /// <summary>Rewrites the rows a read returns.</summary>
    internal Func<QueryRows, QueryRows>? ChangeRows { get; set; }

    /// <summary>The provider name given at construction.</summary>
    string IDatabaseEngine.ProviderName => provider;

    /// <summary>Compiles with SQLite after the optional <see cref="CompileFault"/>, and records the result.</summary>
    public CompiledQuery Compile(ValidatedQuery query)
    {
        if (CompileFault?.Invoke()is { } error)
            throw error;
        var compiled = inner.Compile(query);
        Compiled.Enqueue(compiled);
        return compiled;
    }

    /// <summary>Delegates to SQLite.</summary>
    public void ValidateSourceConnection(string? connection) => inner.ValidateSourceConnection(connection);
    /// <summary>Delegates to SQLite.</summary>
    public ProviderFailure? RecognizeFailure(Exception error) => inner.RecognizeFailure(error);
    /// <summary>Records the read, runs <see cref="BeforeExecute"/>, reads with SQLite and applies
    /// <see cref="ChangeRows"/>.</summary>
    public async Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int timeout, Action opened, Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken token)
    {
        Executed.Enqueue((connection, query, timeout));
        if (BeforeExecute is { } before)
            await before(token);
        var result = await inner.ReadSourceAsync(connection, query, timeout, opened, consume, token);
        return ChangeRows is { } change ? change(result) : result;
    }
}

/// <summary>A hosted service that records that it started, so a test can tell whether gateway validation finished
/// before the rest of the host started.</summary>
internal sealed class NamedGatewaySentinel : IHostedService
{
    /// <summary>True once the host started this service.</summary>
    internal bool Started { get; private set; }

    /// <summary>Marks the service started.</summary>
    public Task StartAsync(CancellationToken token)
    {
        Started = true;
        return Task.CompletedTask;
    }

    /// <summary>Nothing to stop.</summary>
    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}

/// <summary>A request body that aborts the request the moment it is read: it cancels <paramref name="abort"/> and
/// throws, as when a caller leaves while the body is still arriving.</summary>
internal sealed class NamedGatewayCancelStream(CancellationTokenSource abort) : MemoryStream
{
    /// <summary>Cancels the request and throws.</summary>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        abort.Cancel();
        throw new OperationCanceledException(token);
    }

    /// <summary>Cancels the request and throws.</summary>
    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken token)
    {
        abort.Cancel();
        throw new OperationCanceledException(token);
    }
}

/// <summary>A converter for the error body that counts how many times one is written, then writes it normally.</summary>
/// <remarks>Lets a test prove a failed request is answered exactly once.</remarks>
internal sealed class NamedGatewayErrorWrites : JsonConverter<ErrorBody>
{
    /// <summary>The write count, updated atomically.</summary>
    private int attempts;
    /// <summary>How many error bodies were written.</summary>
    internal int Attempts => Volatile.Read(ref attempts);

    /// <summary>Not used: the gateway only writes error bodies.</summary>
    public override ErrorBody? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
    /// <summary>Counts the write, then serializes with the same options minus this converter, so the bytes match an
    /// uncounted write.</summary>
    public override void Write(Utf8JsonWriter writer, ErrorBody value, JsonSerializerOptions options)
    {
        Interlocked.Increment(ref attempts);
        var clean = new JsonSerializerOptions(options);
        clean.Converters.Remove(this);
        JsonSerializer.Serialize(writer, value, clean);
    }
}
