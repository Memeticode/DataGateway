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

internal sealed class NamedGatewayHost : IAsyncDisposable
{
    internal WebApplication App { get; }
    internal HttpClient Client { get; private set; } = new();
    internal NamedGatewayLogs Logs { get; } = new();
    internal NamedGatewayCleanup Cleanup { get; } = new();

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

    internal static void Register(IServiceCollection services, string name, NamedGatewayState state, IDatabaseEngine? engine = null) => services.AddDataGateway(name, gateway => gateway.UseCatalog(state.Create).AddEngine(engine ?? new NamedGatewayEngine()));
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

    internal RouteEndpoint[] Endpoints => ((IEndpointRouteBuilder)App).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();

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

    internal static string PathOf(string operation) => operation switch
    {
        "list" => "/sources/list",
        "describe" => "/sources/describe",
        _ => "/query"
    };
    internal static string Body(string operation, string name = "synthetic_items") => operation switch
    {
        "list" => "{}",
        "describe" => System.Text.Json.JsonSerializer.Serialize(new { source = name }),
        _ => System.Text.Json.JsonSerializer.Serialize(new { source = name, query = new { select = new[] { "id" }, order_by = new[] { new { col = "id", dir = "asc" } }, limit = 2 } })};
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }
}

internal sealed record NamedGatewayReply(int Status, string? ContentType, byte[] Bytes)
{
    internal string Text => Encoding.UTF8.GetString(Bytes);
}

internal sealed class NamedGatewayAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Request.Headers.TryGetValue("X-Synthetic-User", out var name) && !string.IsNullOrWhiteSpace(name) ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name.ToString())], Scheme.Name)), Scheme.Name)) : AuthenticateResult.NoResult());
}

internal sealed class NamedGatewayLogs : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
    internal ConcurrentQueue<NamedGatewayEvent> Events { get; } = new();
    internal NamedGatewayEvent[] Failures => Events.Where(item => item.Category == FailureLog.Category).ToArray();

    public void SetScopeProvider(IExternalScopeProvider provider) => scopes = provider;
    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);
    public void Dispose()
    {
    }

    private sealed class Recorder(NamedGatewayLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner.scopes.Push(state);
        public bool IsEnabled(LogLevel level) => true;
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

internal sealed record NamedGatewayEvent(string Category, LogLevel Level, IReadOnlyDictionary<string, object?> Fields, IReadOnlyDictionary<string, object?> Scopes, string Text, string? Exception);
internal sealed class NamedGatewayEngine(string provider = "sqlite") : IDatabaseEngine
{
    private readonly IDatabaseEngine inner = new SqliteEngine();
    internal ConcurrentQueue<CompiledQuery> Compiled { get; } = new();
    internal ConcurrentQueue<(string Connection, CompiledQuery Query, int Timeout)> Executed { get; } = new();
    internal Func<Exception?>? CompileFault { get; set; }
    internal Func<CancellationToken, Task>? BeforeExecute { get; set; }
    internal Func<QueryRows, QueryRows>? ChangeRows { get; set; }

    string IDatabaseEngine.ProviderName => provider;

    public CompiledQuery Compile(ValidatedQuery query)
    {
        if (CompileFault?.Invoke()is { } error)
            throw error;
        var compiled = inner.Compile(query);
        Compiled.Enqueue(compiled);
        return compiled;
    }

    public void ValidateSourceConnection(string? connection) => inner.ValidateSourceConnection(connection);
    public ProviderFailure? RecognizeFailure(Exception error) => inner.RecognizeFailure(error);
    public async Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int timeout, Action opened, Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken token)
    {
        Executed.Enqueue((connection, query, timeout));
        if (BeforeExecute is { } before)
            await before(token);
        var result = await inner.ReadSourceAsync(connection, query, timeout, opened, consume, token);
        return ChangeRows is { } change ? change(result) : result;
    }
}

internal sealed class NamedGatewaySentinel : IHostedService
{
    internal bool Started { get; private set; }

    public Task StartAsync(CancellationToken token)
    {
        Started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}

internal sealed class NamedGatewayCancelStream(CancellationTokenSource abort) : MemoryStream
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        abort.Cancel();
        throw new OperationCanceledException(token);
    }

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken token)
    {
        abort.Cancel();
        throw new OperationCanceledException(token);
    }
}

internal sealed class NamedGatewayErrorWrites : JsonConverter<ErrorBody>
{
    private int attempts;
    internal int Attempts => Volatile.Read(ref attempts);

    public override ErrorBody? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
    public override void Write(Utf8JsonWriter writer, ErrorBody value, JsonSerializerOptions options)
    {
        Interlocked.Increment(ref attempts);
        var clean = new JsonSerializerOptions(options);
        clean.Converters.Remove(this);
        JsonSerializer.Serialize(writer, value, clean);
    }
}
