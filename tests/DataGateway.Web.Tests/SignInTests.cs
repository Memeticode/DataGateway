using DataGateway.Data.SqlServer;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataGateway.Web.Configuration;
using DataGateway.Web.SignIn;
using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using DataGateway.Data.Catalog;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using TestFiles = DataGateway.Core.Tests.TestFiles;

namespace DataGateway.Web.Tests;

public sealed class SignInTests(ITestOutputHelper output)
{
    private const string FailureBody = """
        {"code":"NotAuthenticated","path":"$","message":"Sign in with Windows to call DataGateway: send the request with Windows credentials (HTTP Negotiate)."}
        """;
    private const string Connection = "Server=fake-sign-in-host;Database=Catalog;Connect Timeout=1";

    [Fact]
    public void T18_envelope_keeps_the_sign_in_body_byte_for_byte()
    {
        var reply = ErrorEnvelope.From(WindowsSignIn.NotAuthenticated, null);
        Assert.Equal(new ErrorReply(401, new("NotAuthenticated", "$",
            "Sign in with Windows to call DataGateway: send the request with Windows credentials (HTTP Negotiate).", null)), reply);
        Assert.Equal(FailureBody, JsonSerializer.Serialize(reply.Body, JsonSerializerOptions.Web));
    }

    [Fact]
    public async Task T6_production_defaults_and_fallback_cover_every_endpoint()
    {
        using var settings = new SettingsScope();
        await using var app = Host(new RecordedLogs());
        var schemes = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        foreach (var scheme in new[] { await schemes.GetDefaultAuthenticateSchemeAsync(), await schemes.GetDefaultChallengeSchemeAsync() })
        {
            Assert.NotNull(scheme);
            Assert.Equal(NegotiateDefaults.AuthenticationScheme, scheme.Name);
            Assert.Equal(typeof(NegotiateHandler), scheme.HandlerType);
        }
        var policy = await app.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();
        Assert.NotNull(policy);
        Assert.IsType<DenyAnonymousAuthorizationRequirement>(Assert.Single(policy.Requirements));
        Assert.Empty(policy.AuthenticationSchemes);
        var endpoints = Endpoints(app);
        Assert.NotEmpty(endpoints);
        foreach (var endpoint in endpoints) Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.Equal("sign-in", WindowsSignIn.NotAuthenticated.Step);
    }

    [Fact]
    public async Task T7_anonymous_calls_to_every_endpoint_and_invalid_routes_never_read_the_catalog()
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        var probe = new ReaderProbe();
        await using var app = Host(logs, probe);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await app.StartAsync(timeout.Token);
        probe.StartInterval();
        using var client = Client(app);
        var calls = Endpoints(app).SelectMany(endpoint =>
            (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods ? methods : new[] { "POST" })
            .Select(method => (Method: method, Path: RoutePath(endpoint), Body: "{}"))).ToList();
        Assert.Contains(calls, call => call.Method == "POST" && call.Path == "/sources/list");
        Assert.Contains(calls, call => call.Method == "POST" && call.Path == "/sources/describe");
        calls.AddRange([("POST", "/not-a-route", "{}"), ("GET", "/sources/describe", "{}"),
            ("POST", "/sources/describe", "{\"source\":\"unknown\"}"), ("POST", "/sources/describe", "not json"), ("POST", "/sources/list", "not json")]);
        try
        {
            foreach (var call in calls)
            {
                using var request = new HttpRequestMessage(new HttpMethod(call.Method), call.Path)
                    { Content = new StringContent(call.Body, Encoding.UTF8, "application/json") };
                using var response = await client.SendAsync(request, timeout.Token);
                await AssertRefused(response, timeout.Token);
            }
            Assert.Equal(0, probe.Calls);
            
            Assert.Empty(logs.SignInFailures);
        }
        finally { Dump(logs); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T8_bad_tokens_are_refused_once_without_leaking_and_next_anonymous_call_is_plain(bool encoded)
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        var probe = new ReaderProbe();
        await using var app = Host(logs, probe);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await app.StartAsync(timeout.Token);
        probe.StartInterval();
        using var client = Client(app);
        var token = encoded ? Convert.ToBase64String(Encoding.UTF8.GetBytes("not-a-token")) : "%%%";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/sources/list") { Content = JsonContent.Create(new { }) };
            request.Headers.TryAddWithoutValidation("Authorization", "Negotiate " + token);
            using var response = await client.SendAsync(request, timeout.Token);
            await AssertRefused(response, timeout.Token, logged: true);
            var entry = Assert.Single(logs.SignInFailures);
            using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            Assert.Equal(entry.Fields["IncidentId"], answer.RootElement.GetProperty("incidentId").GetString());
            Assert.Equal("sign-in", entry.Fields["Step"]);
            Assert.Equal("$", entry.Fields["Path"]);
            Assert.Equal(WindowsSignIn.NotAuthenticated.Message, entry.Fields["Message"]);
            Assert.False(string.IsNullOrEmpty(Assert.IsType<string>(entry.Fields["ExceptionType"])));
            Assert.Null(entry.Fields["SqlNumber"]);
            Assert.Null(entry.Exception);
            using var next = await client.PostAsJsonAsync("/sources/list", new { }, timeout.Token);
            await AssertRefused(next, timeout.Token);
            Assert.Single(logs.SignInFailures);
            Assert.Equal(0, probe.Calls);
            
            foreach (var secret in new[] { token, "not-a-token" })
            {
                Assert.DoesNotContain(secret, await response.Content.ReadAsStringAsync(timeout.Token));
                foreach (var log in logs.Entries)
                {
                    Assert.DoesNotContain(secret, log.Text);
                    Assert.DoesNotContain(secret, log.Exception?.ToString() ?? "");
                }
            }
        }
        finally { Dump(logs); }
    }

    [Fact]
    public async Task T9_handshake_leg_preserves_header_and_gets_the_body_outside_authentication()
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        var probe = new ReaderProbe();
        await using var app = Host(logs, probe, SyntheticSignIn.UseHandshakeLeg);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(timeout.Token);
        probe.StartInterval();
        using var client = Client(app);
        using var response = await client.PostAsJsonAsync("/sources/list", new { }, timeout.Token);
        try
        {
            await AssertRefused(response, timeout.Token, "Negotiate synthetic-leg");
            Assert.Equal(0, probe.Calls);
            
            Assert.Empty(logs.SignInFailures);
        }
        finally { Dump(logs); }
    }

    [Fact]
    public async Task T9_signed_in_unmapped_and_wrong_method_calls_keep_404_and_405()
    {
        using var settings = new SettingsScope();
        await using var app = Host(new RecordedLogs(), configure: SyntheticSignIn.Use);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(timeout.Token);
        using var client = Client(app);
        using var unmapped = await client.PostAsJsonAsync("/not-a-route", new { }, timeout.Token);
        using var method = await client.GetAsync("/sources/describe", timeout.Token);
        Assert.Equal(HttpStatusCode.NotFound, unmapped.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, method.StatusCode);
        foreach (var response in new[] { unmapped, method })
            Assert.DoesNotContain("NotAuthenticated", await response.Content.ReadAsStringAsync(timeout.Token));
    }

    private async Task AssertRefused(HttpResponseMessage response, CancellationToken token, string challenge = "Negotiate", bool logged = false)
    {
        var body = await response.Content.ReadAsStringAsync(token);
        output.WriteLine($"HTTP {(int)response.StatusCode}; WWW-Authenticate: {string.Join(", ", response.Headers.WwwAuthenticate)}; Content-Type: {response.Content.Headers.ContentType}; body: {body}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(challenge, Assert.Single(response.Headers.GetValues("WWW-Authenticate")));
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        if (!logged) Assert.Equal(FailureBody, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(logged ? new[] { "code", "path", "message", "incidentId" } : new[] { "code", "path", "message" }, json.RootElement.EnumerateObject().Select(property => property.Name));
        if (logged)
        {
            Assert.Matches("^[0-9a-f]{32}$", json.RootElement.GetProperty("incidentId").GetString()!);
            using var original = JsonDocument.Parse(FailureBody);
            foreach (var property in original.RootElement.EnumerateObject())
                Assert.Equal(property.Value.GetString(), json.RootElement.GetProperty(property.Name).GetString());
        }
    }

    private void Dump(RecordedLogs logs)
    {
        foreach (var entry in logs.Entries) output.WriteLine($"{entry.Level} {entry.Category}: {entry.Text} {entry.Exception}");
    }

    private static Endpoint[] Endpoints(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();

    private static string RoutePath(Endpoint endpoint) => "/" + string.Join("/",
        Assert.IsType<RouteEndpoint>(endpoint).RoutePattern.PathSegments.Select(segment => string.Concat(
            segment.Parts.Select(part => part switch
            {
                RoutePatternLiteralPart literal => literal.Content,
                RoutePatternSeparatorPart separator => separator.Content,
                RoutePatternParameterPart => "x",
                _ => throw new NotSupportedException("Unrecognized route pattern part.")
            }))));

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(Assert.Single(app.Urls)) };

    private static WebApplication Host(RecordedLogs logs, ReaderProbe? probe = null, Action<WebApplicationBuilder>? configure = null) =>
        Program.CreateApplication(new WebApplicationOptions
        {
            EnvironmentName = "Production", ContentRootPath = TestFiles.InRepo("DataGateway.Web"),
            ApplicationName = typeof(Program).Assembly.GetName().Name, Args = []
        }, builder =>
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
            (probe?.Catalog ?? new HostCatalogState()).Use(builder.Services);
            configure?.Invoke(builder);
        });

    private sealed class ReaderProbe
    {
        internal HostCatalogState Catalog { get; } = new();
        private int baseline;
        internal int Calls => Catalog.Lists + Catalog.Details - baseline;
        internal void StartInterval() { baseline = Catalog.Lists + Catalog.Details; Catalog.Arm(); }
    }

    private sealed class SettingsScope : IDisposable
    {
        private readonly Dictionary<string, string?> original = new();
        private readonly string scratch = TestFiles.InRepo("tests/DataGateway.Web.Tests/obj/sign-in-settings/" + Guid.NewGuid());
        public SettingsScope()
        {
            Directory.CreateDirectory(scratch);
            foreach (var key in new[] { "APPDATA", "HOME", "DOTNET_USER_SECRETS_FALLBACK_DIR" }) Set(key, scratch);
            Set(DataGatewayConfiguration.ConnectionSetting, Connection);
            foreach (var name in new[] { "ConnectTimeoutSeconds", "CommandTimeoutSeconds", "RowCap", "MaxJsonLength", "MaxFilterDepth", "MaxFilterConditions", "MaxInValues" }) Set("DataGateway__Limits__" + name, null);
        }
        private void Set(string key, string? value)
        {
            original.TryAdd(key, Environment.GetEnvironmentVariable(key));
            Environment.SetEnvironmentVariable(key, value);
        }
        public void Dispose()
        {
            foreach (var pair in original) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            Directory.Delete(scratch, recursive: true);
        }
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Text, Exception? Exception, Dictionary<string, object?> Fields);
    private sealed class RecordedLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> entries = new();
        internal IEnumerable<LogEntry> Entries => entries;
        internal IEnumerable<LogEntry> SignInFailures => entries.Where(entry => entry.Fields.GetValueOrDefault("Code") as string == "NotAuthenticated");
        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, entries);
        public void Dispose() { }
        private sealed class Recorder(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => entries.Enqueue(new(category, logLevel, formatter(state, exception), exception,
                    state is IEnumerable<KeyValuePair<string, object?>> fields ? fields.ToDictionary(pair => pair.Key, pair => pair.Value) : new()));
        }
    }
}
