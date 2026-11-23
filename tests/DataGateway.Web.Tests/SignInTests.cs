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

/// <summary>Pins the example host's Windows sign-in: every endpoint requires a signed-in caller, a refused caller gets
/// the gateway's JSON NotAuthenticated error with the Negotiate challenge, and a refusal never reads the catalog or
/// leaks what the caller sent.</summary>
/// <remarks>The tests run the real production host with its real Negotiate handler, which cannot complete a handshake
/// here, so every unsigned call is refused. A probe catalog counts reads to prove refusal happens before the gateway
/// does any work.</remarks>
/// <param name="output">Receives each response and the recorded logs, to diagnose a failing run.</param>
public sealed class SignInTests(ITestOutputHelper output)
{
    /// <summary>The exact JSON body of a NotAuthenticated refusal without an incident id.</summary>
    private const string FailureBody = """
        {"code":"NotAuthenticated","path":"$","message":"Sign in with Windows to call DataGateway: send the request with Windows credentials (HTTP Negotiate)."}
        """;
    /// <summary>A catalog connection setting for a host that does not exist; the tests must never need to reach it.</summary>
    private const string Connection = "Server=fake-sign-in-host;Database=Catalog;Connect Timeout=1";

    /// <summary>The error envelope turns the sign-in failure into status 401 and exactly <see cref="FailureBody"/>.</summary>
    [Fact]
    public void T18_envelope_keeps_the_sign_in_body_byte_for_byte()
    {
        var reply = ErrorEnvelope.From(WindowsSignIn.NotAuthenticated, null);
        Assert.Equal(new ErrorReply(401, new("NotAuthenticated", "$",
            "Sign in with Windows to call DataGateway: send the request with Windows credentials (HTTP Negotiate).", null)), reply);
        Assert.Equal(FailureBody, JsonSerializer.Serialize(reply.Body, JsonSerializerOptions.Web));
    }

    /// <summary>Negotiate is the default scheme for authenticate and challenge, the fallback policy denies anonymous
    /// callers, and no endpoint opts out with allow-anonymous.</summary>
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

    /// <summary>Every mapped endpoint, and unmapped routes, wrong methods and bad bodies, answer an unsigned call with the
    /// plain refusal, without reading the catalog or logging a sign-in failure.</summary>
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

    /// <summary>A malformed Negotiate token is refused with an incident id and one log line that names the exception type
    /// but holds no exception or token, and the next unsigned call gets the plain refusal again.</summary>
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

    /// <summary>When the handshake's first leg answers 401 with its own challenge, the caller still gets the gateway's JSON
    /// body, the leg's header is kept as sent, and nothing is logged or read.</summary>
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

    /// <summary>A signed-in caller still gets the normal 404 and 405 for unmapped routes and wrong methods, not a sign-in
    /// error.</summary>
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

    /// <summary>Asserts a response is the sign-in refusal: 401, one challenge header, JSON content and the refusal body,
    /// with an incident id only when the refusal was logged.</summary>
    /// <param name="response">The response to check.</param>
    /// <param name="token">Cancels reading the body.</param>
    /// <param name="challenge">The exact <c>WWW-Authenticate</c> value expected.</param>
    /// <param name="logged">Whether the refusal was logged and so carries an incident id.</param>
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

    /// <summary>Writes every recorded log entry to the test output.</summary>
    private void Dump(RecordedLogs logs)
    {
        foreach (var entry in logs.Entries) output.WriteLine($"{entry.Level} {entry.Category}: {entry.Text} {entry.Exception}");
    }

    /// <summary>Every endpoint the host maps.</summary>
    private static Endpoint[] Endpoints(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();

    /// <summary>A concrete request path for a route, with each route parameter filled by <c>x</c>.</summary>
    private static string RoutePath(Endpoint endpoint) => "/" + string.Join("/",
        Assert.IsType<RouteEndpoint>(endpoint).RoutePattern.PathSegments.Select(segment => string.Concat(
            segment.Parts.Select(part => part switch
            {
                RoutePatternLiteralPart literal => literal.Content,
                RoutePatternSeparatorPart separator => separator.Content,
                RoutePatternParameterPart => "x",
                _ => throw new NotSupportedException("Unrecognized route pattern part.")
            }))));

    /// <summary>A client for the started host, with no credentials.</summary>
    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(Assert.Single(app.Urls)) };

    /// <summary>Builds the production host on a free local port, with logs recorded and the catalog replaced.</summary>
    /// <param name="logs">Receives every log entry.</param>
    /// <param name="probe">The catalog probe that counts reads, or null for a plain test catalog.</param>
    /// <param name="configure">An optional change to the host, such as a synthetic sign-in scheme.</param>
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

    /// <summary>Counts the catalog reads made after the host has started.</summary>
    /// <remarks>Startup reads the catalog itself; <see cref="StartInterval"/> sets the baseline, so <see cref="Calls"/>
    /// counts only reads caused by the test's requests.</remarks>
    private sealed class ReaderProbe
    {
        /// <summary>The test catalog the host uses.</summary>
        internal HostCatalogState Catalog { get; } = new();
        /// <summary>The read count when the interval started.</summary>
        private int baseline;
        /// <summary>The catalog reads since <see cref="StartInterval"/>.</summary>
        internal int Calls => Catalog.Lists + Catalog.Details - baseline;
        /// <summary>Starts counting from now and arms the catalog.</summary>
        internal void StartInterval() { baseline = Catalog.Lists + Catalog.Details; Catalog.Arm(); }
    }

    /// <summary>Sets the environment the host reads for one test, and restores it afterwards.</summary>
    /// <remarks>The user-profile folders point at an empty scratch folder, so no user secrets on the machine reach the
    /// host, and every limit setting is cleared, so the host runs on its defaults.</remarks>
    private sealed class SettingsScope : IDisposable
    {
        /// <summary>Each changed variable's value before the scope.</summary>
        private readonly Dictionary<string, string?> original = new();
        /// <summary>An empty folder under <c>obj</c> that stands in for the user profile.</summary>
        private readonly string scratch = TestFiles.InRepo("tests/DataGateway.Web.Tests/obj/sign-in-settings/" + Guid.NewGuid());
        /// <summary>Creates the scratch folder and sets the variables.</summary>
        public SettingsScope()
        {
            Directory.CreateDirectory(scratch);
            foreach (var key in new[] { "APPDATA", "HOME", "DOTNET_USER_SECRETS_FALLBACK_DIR" }) Set(key, scratch);
            Set(DataGatewayConfiguration.ConnectionSetting, Connection);
            foreach (var name in new[] { "ConnectTimeoutSeconds", "CommandTimeoutSeconds", "RowCap", "MaxJsonLength", "MaxFilterDepth", "MaxFilterConditions", "MaxInValues" }) Set("DataGateway__Limits__" + name, null);
        }
        /// <summary>Sets a variable, remembering its first original value.</summary>
        private void Set(string key, string? value)
        {
            original.TryAdd(key, Environment.GetEnvironmentVariable(key));
            Environment.SetEnvironmentVariable(key, value);
        }
        /// <summary>Restores every variable and removes the scratch folder.</summary>
        public void Dispose()
        {
            foreach (var pair in original) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>One recorded log call, with its structured fields.</summary>
    private sealed record LogEntry(string Category, LogLevel Level, string Text, Exception? Exception, Dictionary<string, object?> Fields);
    /// <summary>A logger provider that records every entry, so tests can read the fields of a sign-in failure.</summary>
    private sealed class RecordedLogs : ILoggerProvider
    {
        /// <summary>Every entry, in order; a queue because requests log from several threads.</summary>
        private readonly ConcurrentQueue<LogEntry> entries = new();
        /// <summary>Every recorded entry.</summary>
        internal IEnumerable<LogEntry> Entries => entries;
        /// <summary>The entries that record a NotAuthenticated failure.</summary>
        internal IEnumerable<LogEntry> SignInFailures => entries.Where(entry => entry.Fields.GetValueOrDefault("Code") as string == "NotAuthenticated");
        /// <summary>Creates a logger that records into this provider.</summary>
        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, entries);
        /// <summary>Nothing to release.</summary>
        public void Dispose() { }
        /// <summary>A logger that records every entry at every level.</summary>
        private sealed class Recorder(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            /// <summary>Scopes are not recorded.</summary>
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            /// <summary>Every level is recorded.</summary>
            public bool IsEnabled(LogLevel logLevel) => true;
            /// <summary>Records the entry with its formatted text and structured fields.</summary>
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => entries.Enqueue(new(category, logLevel, formatter(state, exception), exception,
                    state is IEnumerable<KeyValuePair<string, object?>> fields ? fields.ToDictionary(pair => pair.Key, pair => pair.Value) : new()));
        }
    }
}
