using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>A host with two named gateways, <c>north</c> and <c>south</c>, for tests of host policy: a sign-in
/// refusal recorded by the host becomes the gateway's JSON error, its log line and its API-page description.</summary>
/// <remarks>
/// <para>TERM — host policy: the host's own access rule (sign-in) registered with <c>UseHostPolicy</c>. The host
/// refuses a call by recording a failure with <c>TryRecordDataGatewayRefusal</c> and ending the request with an
/// empty 401; the gateway's error boundary (<c>UseDataGatewayErrors</c>) then writes the error reply and the
/// failure log line. An unrecorded empty 401 gets the policy's fallback failure instead.</para>
/// <para>Each request carries two test headers: <c>X-Policy-Call</c>, an id the outermost middleware signals when
/// the server has finished the request, so a test reads logs only after they are written; and
/// <c>X-Policy-Mode</c>, which picks what the host does: <c>record</c> (the default) records a refusal,
/// <c>challenge</c> records nothing and sends a challenge header, <c>duplicate</c> records twice, <c>fault</c>
/// throws, and <c>bad413</c> or <c>bad400</c> throws a bad-request exception.</para>
/// <para>The two gateways read separate SQLite files whose rows differ (south ids are shifted by ten), so a reply
/// shows which gateway answered.</para>
/// </remarks>
internal sealed class HostPolicyHost : IAsyncDisposable
{
    /// <summary>The two gateway names.</summary>
    internal static readonly string[] Names = ["north", "south"];
    /// <summary>The JSON API operations each gateway maps.</summary>
    internal static readonly string[] Operations = ["list", "describe", "query"];
    /// <summary>The API-page routes below each gateway's page prefix: the document, the page, its redirect and its
    /// assets.</summary>
    internal static readonly string[] Pages = ["openapi/v1.json", "scalar", "scalar/", "scalar/v1", "scalar/scalar.js", "scalar/scalar.aspnetcore.js", "scalar/favicon.svg"];
    /// <summary>The north gateway's database.</summary>
    internal SqliteFixture North { get; } = new();
    /// <summary>The south gateway's database.</summary>
    internal SqliteFixture South { get; } = new();
    /// <summary>The catalog state of each gateway, in <see cref="Names"/> order.</summary>
    internal NamedGatewayState[] States { get; }
    /// <summary>The recording engine of each gateway, in <see cref="Names"/> order.</summary>
    internal NamedGatewayEngine[] Engines { get; } = [new(), new()];
    /// <summary>The underlying host, with its logs.</summary>
    internal NamedGatewayHost Host { get; }
    /// <summary>The completion signals and refusal records shared with the authentication handler.</summary>
    internal HostPolicySignals Signals { get; } = new();
    /// <summary>Counts the error bodies the gateway writes, and can make writing fail.</summary>
    internal HostPolicyWrites Writes { get; } = new();
    /// <summary>The catalog counts of each gateway when the host started.</summary>
    internal (int, int, int)[] Baselines { get; private set; } = [];
    /// <summary>The compile count of each engine when the host started.</summary>
    internal int[] CompileBaselines { get; private set; } = [];
    /// <summary>The execute count of each engine when the host started.</summary>
    internal int[] ExecuteBaselines { get; private set; } = [];
    /// <summary>The path in front of every gateway route: empty, or <c>/base/nested</c> when the host nests them under a
    /// path base and a group.</summary>
    internal string Root { get; }
    /// <summary>True when only the API pages are mapped, not the JSON API.</summary>
    internal bool PageOnly { get; }
    /// <summary>When set, replaces the mode-driven middleware inside the error boundary, so a test can run any host
    /// code before or after the gateway handler.</summary>
    internal Func<HttpContext, RequestDelegate, Task>? Middleware { get; set; }
    /// <summary>A client that does not follow redirects, so the page redirect can be asserted.</summary>
    internal HttpClient Client { get; private set; } = new();

    /// <summary>The display form of a gateway name, used in each gateway's distinct policy texts.</summary>
    internal static string Title(string name) => name == "north" ? "North" : "South";
    /// <summary>The gateway's policy fallback: the failure used for an unrecorded empty 401.</summary>
    internal static CatalogFailure Fallback(string name) => new(DataGatewayError.NotAuthenticated.Code, "$", Title(name) + " credentials are required.") { Step = "sign-in" };
    /// <summary>The failure the host records when it refuses a caller; distinct from <see cref="Fallback"/> so a test
    /// can tell which one reached the wire.</summary>
    internal static CatalogFailure Actual(string name) => new(DataGatewayError.NotAuthenticated.Code, "$", Title(name) + " credentials were rejected.") { Step = "sign-in", ExceptionType = Title(name) + "PolicyFault" };
    /// <summary>The policy sentence each gateway adds to its API document description.</summary>
    internal static string Description(string name) => Title(name) + " policy applies to every call.";
    /// <summary>The policy's description of each operation's 401 response.</summary>
    internal static string ResponseDescription(string name) => Title(name) + " policy refused this call.";

    /// <summary>Builds the two-gateway host without starting it.</summary>
    /// <param name="boundary">Installs the gateway error boundary.</param>
    /// <param name="policy">Registers host policy on the gateways.</param>
    /// <param name="twice">Installs the boundary twice, with a counting middleware between, to prove the two share one
    /// request boundary.</param>
    /// <param name="protect">Requires sign-in on the API and page groups; north's page on the page group, south's on
    /// its parent group.</param>
    /// <param name="northOnlyPolicy">Registers policy on north only.</param>
    /// <param name="nested">Maps every route under a path base and a group (<see cref="Root"/>).</param>
    /// <param name="pageOnly">Maps only the API pages.</param>
    /// <param name="fallback">Replaces <see cref="Fallback"/>.</param>
    /// <param name="description">Replaces <see cref="Description"/>.</param>
    /// <param name="responseDescription">Replaces <see cref="ResponseDescription"/>.</param>
    /// <param name="logCategory">When set, records only log events in this category.</param>
    internal HostPolicyHost(bool boundary = true, bool policy = true, bool twice = false, bool protect = true,
        bool northOnlyPolicy = false, bool nested = false, bool pageOnly = false,
        Func<string, CatalogFailure>? fallback = null, string? description = null, string? responseDescription = null, string? logCategory = null)
    {
        Root = nested ? "/base/nested" : "";
        PageOnly = pageOnly;
        North.Change("DELETE FROM gateway_source WHERE id<>101; UPDATE gateway_source SET name='synthetic_north';");
        South.Change("DELETE FROM gateway_source WHERE id<>101; UPDATE gateway_source SET name='synthetic_south'; UPDATE proof_items SET id=id+10;");
        States = [new(North), new(South)];
        Host = new NamedGatewayHost(builder =>
        {
            if (logCategory is not null)
                builder.Logging.AddFilter<NamedGatewayLogs>((category, _) => category == logCategory);
            builder.Services.AddSingleton(Signals);
            builder.Services.AddSingleton(new global::DataGateway.GatewayJson(Writes));
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "HostPolicySynthetic";
                options.DefaultChallengeScheme = "HostPolicySynthetic";
            }).AddScheme<AuthenticationSchemeOptions, HostPolicyAuthentication>("HostPolicySynthetic", _ => { });
            for (var i = 0; i < Names.Length; i++)
            {
                var index = i;
                var name = Names[i];
                builder.Services.AddDataGateway(name, gateway =>
                {
                    gateway.UseCatalog(States[index].Create).AddEngine(Engines[index]);
                    if (policy && (!northOnlyPolicy || index == 0))
                        gateway.UseHostPolicy((fallback ?? Fallback)(name), description ?? Description(name), responseDescription ?? ResponseDescription(name));
                });
            }
        }, app =>
        {
            if (nested)
                app.UsePathBase("/base");
            app.UseRouting();
            app.Use(async (context, next) =>
            {
                var id = context.Request.Headers["X-Policy-Call"].ToString();
                try
                {
                    await next(context);
                }
                catch (HostPolicyFault)
                {
                    context.Response.StatusCode = 591;
                    await context.Response.WriteAsync("outer host fault");
                }
                finally
                {
                    Signals.Done(id);
                }
            });
            if (boundary)
                Assert.Same(app, app.UseDataGatewayErrors());
            if (twice)
            {
                app.Use(async (context, next) =>
                {
                    Interlocked.Increment(ref Signals.Traversals);
                    await next(context);
                });
                Assert.Same(app, app.UseDataGatewayErrors());
            }
            app.Use(async (context, next) =>
            {
                if (Middleware is { } custom)
                {
                    await custom(context, next);
                    return;
                }
                var mode = context.Request.Headers["X-Policy-Mode"].ToString();
                if (mode == "fault")
                    throw new HostPolicyFault("synthetic private host detail");
                if (mode == "bad413" || mode == "bad400")
                    throw new BadHttpRequestException("synthetic private body detail", mode == "bad413" ? 413 : 400);
                await next(context);
            });
        });
        IEndpointRouteBuilder endpoints = Host.App;
        if (nested)
            endpoints = Host.App.MapGroup("/nested");
        foreach (var name in Names)
        {
            if (!pageOnly)
            {
                var api = endpoints.MapDataGateway(name, "/" + name);
                if (protect)
                    api.RequireAuthorization();
                api.MapGet("/host-neighbor", Bare);
            }
            var parent = endpoints.MapGroup("/" + name + "-docs");
            if (protect && name == "south")
                parent.RequireAuthorization();
            var page = parent.MapDataGatewayApiPage(name, "");
            if (protect && name == "north")
                page.RequireAuthorization();
            page.MapGet("/host-neighbor", Bare);
        }
        endpoints.MapGet("/host", Bare);
        endpoints.MapGet("/north-similar", Bare);
        endpoints.MapGet("/host-fault", () => { throw new HostPolicyFault("host-owned"); });
        endpoints.MapGet("/scope", (ILoggerFactory loggers) =>
        {
            loggers.CreateLogger("HostPolicy.Neighbor").LogInformation("Neighbor complete");
            return "neighbor";
        });
    }

    /// <summary>A host-owned endpoint that answers an empty 401 and tries to record a refusal, reporting in
    /// <c>X-Host-Record</c> whether the gateway accepted it.</summary>
    /// <remarks>Host-owned endpoints, even ones mapped inside a gateway's groups, lie outside the gateway's boundary,
    /// so the record must be refused and the 401 left as the host wrote it.</remarks>
    private static void Bare(HttpContext context)
    {
        context.Response.StatusCode = 401;
        context.Response.Headers.WWWAuthenticate = "Synthetic challenge-leg";
        context.Response.Headers["X-Host-Record"] = context.TryRecordDataGatewayRefusal(Actual("north")).ToString();
    }

    /// <summary>Starts the host and takes the baselines that <see cref="NoWork"/> compares against.</summary>
    internal async Task Start()
    {
        await Host.Start();
        Client.Dispose();
        Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = Host.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(20) };
        Baselines = States.Select(state => state.Counts).ToArray();
        CompileBaselines = Engines.Select(engine => engine.Compiled.Count).ToArray();
        ExecuteBaselines = Engines.Select(engine => engine.Executed.Count).ToArray();
    }

    /// <summary>The route of one gateway's API operation.</summary>
    internal string Api(string name, string operation) => Root + "/" + name + NamedGatewayHost.PathOf(operation);
    /// <summary>The route of one gateway's API-page path.</summary>
    internal string Page(string name, string suffix) => Root + "/" + name + "-docs/" + suffix;
    /// <summary>The route of one gateway's MCP endpoint.</summary>
    internal string Mcp(string name) => Root + "/" + name + "/mcp";
    /// <summary>A valid request body for an operation on the gateway's one source.</summary>
    internal static string Body(string name, string operation) => NamedGatewayHost.Body(operation, "synthetic_" + name);

    /// <summary>Sends one request in the given mode and waits until the server has finished it.</summary>
    /// <param name="path">The route.</param>
    /// <param name="mode">The <c>X-Policy-Mode</c> value.</param>
    /// <param name="signed">Signs the caller in.</param>
    /// <param name="body">A JSON body to POST; a GET when null.</param>
    /// <param name="caller">The signed-in caller's name.</param>
    internal async Task<HostPolicyResponse> Send(string path, string mode = "record", bool signed = false, string? body = null, string? caller = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var done = Signals.Expect(id);
        using var request = Request(path, mode, signed, body, id, caller);
        using var response = await Client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        await done.WaitAsync(TimeSpan.FromSeconds(10));
        var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(pair => pair.Key, pair => string.Join(", ", pair.Value), StringComparer.OrdinalIgnoreCase);
        return new((int)response.StatusCode, response.Content.Headers.ContentType?.ToString(), bytes, headers, id);
    }

    /// <summary>Builds a request with the test headers.</summary>
    internal static HttpRequestMessage Request(string path, string mode, bool signed, string? body, string id, string? caller = null)
    {
        var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Add("X-Policy-Call", id);
        request.Headers.Add("X-Policy-Mode", mode);
        if (signed)
            request.Headers.Add("X-Synthetic-User", caller ?? "synthetic-reader");
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>Sends one MCP tools/list call in the given mode and waits until the server has finished it.</summary>
    internal async Task<HostPolicyResponse> SendMcp(string name, string mode = "record", bool signed = false)
    {
        var id = Guid.NewGuid().ToString("N");
        var done = Signals.Expect(id);
        using var client = new HttpClient(new HostPolicyHeaders(mode, id, signed)) { BaseAddress = Client.BaseAddress, Timeout = TimeSpan.FromSeconds(20) };
        using var response = await NamedGatewayMcpWire.Raw(client, Mcp(name), NamedGatewayMcpWire.LegacyList, CancellationToken.None);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        await done.WaitAsync(TimeSpan.FromSeconds(10));
        return new((int)response.StatusCode, response.Content.Headers.ContentType?.ToString(), bytes,
            response.Headers.Concat(response.Content.Headers).ToDictionary(pair => pair.Key, pair => string.Join(", ", pair.Value), StringComparer.OrdinalIgnoreCase), id);
    }

    /// <summary>Asserts that no catalog read, compile or execute happened since <see cref="Start"/>: a refused call
    /// must not reach the data.</summary>
    internal void NoWork()
    {
        Assert.Equal(Baselines, States.Select(state => state.Counts));
        Assert.Equal(CompileBaselines, Engines.Select(engine => engine.Compiled.Count));
        Assert.Equal(ExecuteBaselines, Engines.Select(engine => engine.Executed.Count));
    }

    /// <summary>Asserts both gateways still serve signed callers, each from its own database.</summary>
    internal async Task Witnesses()
    {
        foreach (var name in Names)
        {
            var list = await Send(Api(name, "list"), signed: true, body: "{}");
            Assert.Equal(200, list.Status);
            Assert.Equal("synthetic_" + name, Assert.Single(HostPolicyAssert.Json(list.Bytes).GetProperty("sources").EnumerateArray()).GetProperty("name").GetString());
            var query = await Send(Api(name, "query"), signed: true, body: Body(name, "query"));
            Assert.Equal(200, query.Status);
            var root = HostPolicyAssert.Json(query.Bytes);
            Assert.Equal("synthetic_" + name, root.GetProperty("source").GetString());
            Assert.Equal(name == "north" ? 1 : 11, root.GetProperty("rows")[0].GetProperty("id").GetInt32());
            Assert.Equal(name == "north" ? 2 : 12, root.GetProperty("rows")[1].GetProperty("id").GetInt32());
        }
    }

    /// <summary>The gateway endpoint with the given name and operation.</summary>
    internal Endpoint Endpoint(string name, string operation) => Host.Endpoints.Single(endpoint =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == $"DataGateway:{name}:{operation}");

    /// <summary>Runs the error boundary in-process on a fabricated context bound to a gateway endpoint, with
    /// <paramref name="downstream"/> as the code it covers.</summary>
    /// <remarks>A real socket cannot be made to report a started response or a departed caller on cue; the probe's
    /// fake response and lifetime features can.</remarks>
    /// <param name="name">The gateway.</param>
    /// <param name="operation">The endpoint's operation.</param>
    /// <param name="downstream">The code inside the boundary.</param>
    /// <param name="twice">Installs the boundary twice.</param>
    /// <param name="started">Starts with the response already started.</param>
    /// <param name="token">The request-aborted token.</param>
    /// <param name="configure">Changes the context before it runs.</param>
    internal async Task<HostPolicyProbe> Probe(string name, string operation, RequestDelegate downstream,
        bool twice = false, bool started = false, CancellationToken token = default,
        Action<DefaultHttpContext>? configure = null)
    {
        await using var scope = Host.App.Services.CreateAsyncScope();
        var probe = new HostPolicyProbe(scope.ServiceProvider, Endpoint(name, operation), token, started);
        configure?.Invoke(probe.Context);
        var app = new ApplicationBuilder(scope.ServiceProvider);
        Assert.Same(app, app.UseDataGatewayErrors());
        if (twice)
            Assert.Same(app, app.UseDataGatewayErrors());
        app.Run(downstream);
        await app.Build()(probe.Context);
        return probe;
    }

    /// <summary>Stops the host and deletes both databases.</summary>
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.DisposeAsync();
        North.Dispose();
        South.Dispose();
    }
}

/// <summary>State shared between a test and the host's middleware and authentication handler.</summary>
internal sealed class HostPolicySignals
{
    /// <summary>The pending completion of each call id.</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> completions = new();
    /// <summary>For each call id, whether the first record was accepted and whether a duplicate record was.</summary>
    internal ConcurrentDictionary<string, bool[]> Accepted { get; } = new();
    /// <summary>How many requests passed between two boundary installations; a field so it can be incremented
    /// atomically.</summary>
    internal int Traversals;
    /// <summary>The failure the authentication handler records for a gateway name.</summary>
    internal Func<string, CatalogFailure> Recorded { get; set; } = HostPolicyHost.Actual;
    /// <summary>Registers a call id and returns a task that completes when the server finishes that call.</summary>
    internal Task<string> Expect(string id)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(completions.TryAdd(id, completion));
        return completion.Task;
    }
    /// <summary>Completes a call id, if a test is waiting for it.</summary>
    internal void Done(string id)
    {
        if (completions.TryRemove(id, out var completion))
            completion.TrySetResult(id);
    }
}

/// <summary>The synthetic sign-in scheme: signs in a caller with <c>X-Synthetic-User</c>, and otherwise records a
/// refusal unless the mode is <c>challenge</c>.</summary>
internal sealed class HostPolicyAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, HostPolicySignals signals)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Signs the caller in or records the refusal (twice in <c>duplicate</c> mode) and stores whether each
    /// record was accepted.</summary>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.TryGetValue("X-Synthetic-User", out var user))
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
        var name = Request.Path.ToString().Contains("south", StringComparison.Ordinal) ? "south" : "north";
        var mode = Request.Headers["X-Policy-Mode"].ToString();
        if (mode != "challenge")
        {
            var failure = signals.Recorded(name);
            var first = Context.TryRecordDataGatewayRefusal(failure);
            var second = mode == "duplicate" && Context.TryRecordDataGatewayRefusal(failure with { Message = "A distinct duplicate must never win." });
            signals.Accepted[Request.Headers["X-Policy-Call"].ToString()] = [first, second];
        }
        return Task.FromResult(AuthenticateResult.NoResult());
    }
    /// <summary>Ends the request with an empty 401, adding a challenge header in <c>challenge</c> mode.</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        if (Request.Headers["X-Policy-Mode"] == "challenge")
            Response.Headers.WWWAuthenticate = "Synthetic challenge-leg";
        return Task.CompletedTask;
    }
}

/// <summary>Adds the test headers to every request of an MCP call.</summary>
internal sealed class HostPolicyHeaders(string mode, string id, bool signed) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
{
    /// <summary>Adds the headers and sends.</summary>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        request.Headers.Add("X-Policy-Call", id);
        request.Headers.Add("X-Policy-Mode", mode);
        if (signed)
            request.Headers.Add("X-Synthetic-User", "synthetic-reader");
        return base.SendAsync(request, token);
    }
}

/// <summary>Signs in every request of an MCP session and gives its one tools/call request the known
/// <see cref="CallId"/>.</summary>
/// <remarks>A session sends several requests; only the tools/call one should complete the id the test waits
/// for, and the handler asserts there is exactly one.</remarks>
internal sealed class HostPolicyMcpHeaders : DelegatingHandler
{
    /// <summary>The call id of the session's tools/call request.</summary>
    internal string CallId { get; } = Guid.NewGuid().ToString("N");
    /// <summary>How many tools/call requests were sent.</summary>
    private int calls;

    /// <summary>Creates the handler over a client that does not follow redirects.</summary>
    internal HostPolicyMcpHeaders() : base(new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    /// <summary>Adds the headers, with <see cref="CallId"/> on the tools/call request and a fresh id on the others.</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        if (request.Method == HttpMethod.Post && request.Content is not null)
        {
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            if (body.RootElement.TryGetProperty("method", out var method) && method.GetString() == "tools/call")
            {
                Assert.Equal(1, Interlocked.Increment(ref calls));
                id = CallId;
            }
        }
        request.Headers.Add("X-Policy-Call", id);
        request.Headers.Add("X-Synthetic-User", "synthetic-reader");
        return await base.SendAsync(request, token);
    }
}

/// <summary>One reply with its raw bytes, headers and the call id it was sent with.</summary>
internal sealed record HostPolicyResponse(int Status, string? ContentType, byte[] Bytes, IReadOnlyDictionary<string, string> Headers, string Call);
/// <summary>The exception the host's synthetic faults throw, so log lines can name its type.</summary>
internal sealed class HostPolicyFault(string message) : Exception(message);

/// <summary>A converter for the error body that counts writes and can make a write fail.</summary>
internal sealed class HostPolicyWrites : JsonConverter<ErrorBody>
{
    /// <summary>The write count, updated atomically.</summary>
    private int attempts;
    /// <summary>How many error bodies the gateway tried to write.</summary>
    internal int Attempts => Volatile.Read(ref attempts);
    /// <summary>Makes every write throw, as when the reply cannot be sent.</summary>
    internal bool Throw { get; set; }
    /// <summary>Not used: the gateway only writes error bodies.</summary>
    public override ErrorBody? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
    /// <summary>Counts the write, throws when <see cref="Throw"/> is set, and otherwise serializes without this
    /// converter.</summary>
    public override void Write(Utf8JsonWriter writer, ErrorBody value, JsonSerializerOptions options)
    {
        Interlocked.Increment(ref attempts);
        if (Throw)
            throw new HostPolicyFault("synthetic serialization detail");
        var clean = new JsonSerializerOptions(options);
        clean.Converters.Remove(this);
        JsonSerializer.Serialize(writer, value, clean);
    }
}

/// <summary>A fabricated request for <see cref="HostPolicyHost.Probe"/>: the context, its captured output and
/// fake response and lifetime features.</summary>
internal sealed class HostPolicyProbe
{
    /// <summary>The request context.</summary>
    internal DefaultHttpContext Context { get; }
    /// <summary>Everything written to the response body.</summary>
    internal MemoryStream Output { get; } = new();
    /// <summary>The response feature, whose started flag a test sets.</summary>
    internal HostPolicyResponseFeature Response { get; }
    /// <summary>The lifetime feature, which counts aborts.</summary>
    internal HostPolicyLifetime Lifetime { get; }
    /// <summary>Creates the context for an endpoint in a request scope.</summary>
    internal HostPolicyProbe(IServiceProvider services, Endpoint endpoint, CancellationToken token, bool started)
    {
        Context = new DefaultHttpContext { RequestServices = services };
        Response = new HostPolicyResponseFeature { HasStarted = started };
        Lifetime = new HostPolicyLifetime { RequestAborted = token };
        Context.Features.Set<IHttpResponseFeature>(Response);
        Context.Features.Set<IHttpRequestLifetimeFeature>(Lifetime);
        Context.Response.Body = Output;
        Context.SetEndpoint(endpoint);
    }
    /// <summary>The probe's response as a <see cref="HostPolicyResponse"/>.</summary>
    internal HostPolicyResponse Reply => new(Context.Response.StatusCode, Context.Response.ContentType, Output.ToArray(),
        Context.Response.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()), "probe");
}

/// <summary>A lifetime feature with a settable aborted token that counts calls to <see cref="Abort"/>.</summary>
/// <remarks>The boundary aborts a request it can no longer answer cleanly; the count shows it did.</remarks>
internal sealed class HostPolicyLifetime : IHttpRequestLifetimeFeature
{
    /// <summary>The token that reports the caller has left.</summary>
    public CancellationToken RequestAborted { get; set; }
    /// <summary>How many times the request was aborted.</summary>
    internal int Aborts { get; private set; }
    /// <summary>Counts an abort.</summary>
    public void Abort() => Aborts++;
}

/// <summary>A response feature whose started flag a test controls.</summary>
internal sealed class HostPolicyResponseFeature : IHttpResponseFeature
{
    /// <summary>The status code.</summary>
    public int StatusCode { get; set; } = 200;
    /// <summary>The reason phrase.</summary>
    public string? ReasonPhrase { get; set; }
    /// <summary>The response headers.</summary>
    public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
    /// <summary>The body stream; the context's response body replaces it.</summary>
    public Stream Body { get; set; } = Stream.Null;
    /// <summary>Whether the response has started; settable so a test can start it on cue.</summary>
    public bool HasStarted { get; set; }
    /// <summary>Ignored: nothing is sent.</summary>
    public void OnStarting(Func<object, Task> callback, object state) { }
    /// <summary>Ignored: nothing is sent.</summary>
    public void OnCompleted(Func<object, Task> callback, object state) { }
}

/// <summary>Assertions for host-policy replies and failure log lines.</summary>
internal static class HostPolicyAssert
{
    /// <summary>The failure log's message template, written out so a change to the log contract fails a test.</summary>
    internal const string Template = "Failure {IncidentId}: {Operation} for {Caller}: {Code}: {Message} ({Step}, {ExceptionType}, {SqlNumber})";
    /// <summary>Parses a reply body into a detached element.</summary>
    internal static JsonElement Json(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        return json.RootElement.Clone();
    }
    /// <summary>Asserts the reply is exactly the gateway's error envelope for <paramref name="failure"/>, byte for byte,
    /// and returns its incident id.</summary>
    /// <param name="response">The reply.</param>
    /// <param name="failure">The failure the envelope is built from.</param>
    /// <param name="recorded">True when the reply carries an incident id; an unrecorded fallback has none.</param>
    /// <param name="status">The expected status, when it differs from the envelope's own.</param>
    /// <returns>The incident id, or null when not recorded.</returns>
    internal static string? Envelope(HostPolicyResponse response, CatalogFailure failure, bool recorded = true, int? status = null)
    {
        var json = Json(response.Bytes);
        var incident = recorded ? json.GetProperty("incidentId").GetString() : null;
        if (recorded)
            Assert.Matches("^[0-9a-f]{32}$", incident!);
        Assert.Equal(recorded ? new[] { "code", "path", "message", "incidentId" } : ["code", "path", "message"], json.EnumerateObject().Select(property => property.Name));
        var expected = ErrorEnvelope.From(failure, incident);
        Assert.Equal(status ?? expected.Status, response.Status);
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected.Body, JsonSerializerOptions.Web), response.Bytes);
        return incident;
    }
    /// <summary>Asserts exactly one failure line was written after <paramref name="before"/> and that each of its
    /// fields matches the failure, the incident, the gateway scope and the caller.</summary>
    /// <param name="logs">The host's logs.</param>
    /// <param name="before">The failure count before the call.</param>
    /// <param name="incident">The incident id from the reply.</param>
    /// <param name="name">The gateway name expected in the scope.</param>
    /// <param name="operation">The operation expected in the line.</param>
    /// <param name="failure">The failure expected in the line.</param>
    /// <param name="caller">The caller name expected.</param>
    /// <param name="level">The level expected.</param>
    /// <param name="ordinary">When true, also asserts the request, SQL, source and exception fields are empty, as they
    /// are for a refusal.</param>
    /// <returns>The line.</returns>
    internal static NamedGatewayEvent Line(NamedGatewayLogs logs, int before, string incident, string name, string operation,
        CatalogFailure failure, string caller = "anonymous", LogLevel level = LogLevel.Error, bool ordinary = true)
    {
        var line = Assert.Single(logs.Failures.Skip(before));
        Assert.Equal(DataGatewayExtensions.FailureLogCategory, line.Category);
        Assert.Equal(level, line.Level);
        Assert.Equal(incident, line.Fields["IncidentId"]);
        Assert.Equal(name, line.Scopes["Gateway"]);
        Assert.Equal(operation, line.Fields["Operation"]);
        Assert.Equal(caller, line.Fields["Caller"]);
        Assert.Equal(failure.Code, line.Fields["Code"]);
        Assert.Equal(ErrorEnvelope.From(failure, incident).Body.Code, line.Fields["ReturnedCode"]);
        Assert.Equal(failure.Path, line.Fields["Path"]);
        Assert.Equal(failure.Message, line.Fields["Message"]);
        Assert.Equal(failure.Step, line.Fields["Step"]);
        Assert.Equal(failure.ExceptionType, line.Fields["ExceptionType"]);
        Assert.Equal(ErrorLogTests.Fields.Concat(["{OriginalFormat}"]), line.Fields.Keys);
        Assert.Equal(Template, line.Fields["{OriginalFormat}"]);
        Assert.Equal($"Failure {incident}: {operation} for {caller}: {failure.Code}: {failure.Message} ({failure.Step ?? "null"}, {failure.ExceptionType ?? "null"}, {failure.SqlNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"})", line.Text);
        Assert.Null(line.Exception);
        if (ordinary)
            foreach (var key in new[] { "Request", "RequestBytes", "RequestCut", "Sql", "SqlParameters", "SqlNumber", "SourceId", "SourceName", "DriverMessage", "ExceptionText" })
                Assert.Null(line.Fields[key]);
        return line;
    }
    /// <summary>Asserts the reply is the host's own, untouched by the gateway: the given status and exact body.</summary>
    internal static void Bare(HostPolicyResponse response, int status = 401, string body = "")
    {
        Assert.Equal(status, response.Status);
        Assert.Equal(Encoding.UTF8.GetBytes(body), response.Bytes);
    }
}
