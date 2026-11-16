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

internal sealed class HostPolicyHost : IAsyncDisposable
{
    internal static readonly string[] Names = ["north", "south"];
    internal static readonly string[] Operations = ["list", "describe", "query"];
    internal static readonly string[] Pages = ["openapi/v1.json", "scalar", "scalar/", "scalar/v1", "scalar/scalar.js", "scalar/scalar.aspnetcore.js", "scalar/favicon.svg"];
    internal SqliteFixture North { get; } = new();
    internal SqliteFixture South { get; } = new();
    internal NamedGatewayState[] States { get; }
    internal NamedGatewayEngine[] Engines { get; } = [new(), new()];
    internal NamedGatewayHost Host { get; }
    internal HostPolicySignals Signals { get; } = new();
    internal HostPolicyWrites Writes { get; } = new();
    internal (int, int, int)[] Baselines { get; private set; } = [];
    internal int[] CompileBaselines { get; private set; } = [];
    internal int[] ExecuteBaselines { get; private set; } = [];
    internal string Root { get; }
    internal bool PageOnly { get; }
    internal Func<HttpContext, RequestDelegate, Task>? Middleware { get; set; }
    internal HttpClient Client { get; private set; } = new();

    internal static string Title(string name) => name == "north" ? "North" : "South";
    internal static CatalogFailure Fallback(string name) => new(DataGatewayError.NotAuthenticated.Code, "$", Title(name) + " credentials are required.") { Step = "sign-in" };
    internal static CatalogFailure Actual(string name) => new(DataGatewayError.NotAuthenticated.Code, "$", Title(name) + " credentials were rejected.") { Step = "sign-in", ExceptionType = Title(name) + "PolicyFault" };
    internal static string Description(string name) => Title(name) + " policy applies to every call.";
    internal static string ResponseDescription(string name) => Title(name) + " policy refused this call.";

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

    private static void Bare(HttpContext context)
    {
        context.Response.StatusCode = 401;
        context.Response.Headers.WWWAuthenticate = "Synthetic challenge-leg";
        context.Response.Headers["X-Host-Record"] = context.TryRecordDataGatewayRefusal(Actual("north")).ToString();
    }

    internal async Task Start()
    {
        await Host.Start();
        Client.Dispose();
        Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = Host.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(20) };
        Baselines = States.Select(state => state.Counts).ToArray();
        CompileBaselines = Engines.Select(engine => engine.Compiled.Count).ToArray();
        ExecuteBaselines = Engines.Select(engine => engine.Executed.Count).ToArray();
    }

    internal string Api(string name, string operation) => Root + "/" + name + NamedGatewayHost.PathOf(operation);
    internal string Page(string name, string suffix) => Root + "/" + name + "-docs/" + suffix;
    internal string Mcp(string name) => Root + "/" + name + "/mcp";
    internal static string Body(string name, string operation) => NamedGatewayHost.Body(operation, "synthetic_" + name);

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

    internal void NoWork()
    {
        Assert.Equal(Baselines, States.Select(state => state.Counts));
        Assert.Equal(CompileBaselines, Engines.Select(engine => engine.Compiled.Count));
        Assert.Equal(ExecuteBaselines, Engines.Select(engine => engine.Executed.Count));
    }

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

    internal Endpoint Endpoint(string name, string operation) => Host.Endpoints.Single(endpoint =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == $"DataGateway:{name}:{operation}");

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

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.DisposeAsync();
        North.Dispose();
        South.Dispose();
    }
}

internal sealed class HostPolicySignals
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> completions = new();
    internal ConcurrentDictionary<string, bool[]> Accepted { get; } = new();
    internal int Traversals;
    internal Func<string, CatalogFailure> Recorded { get; set; } = HostPolicyHost.Actual;
    internal Task<string> Expect(string id)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(completions.TryAdd(id, completion));
        return completion.Task;
    }
    internal void Done(string id)
    {
        if (completions.TryRemove(id, out var completion))
            completion.TrySetResult(id);
    }
}

internal sealed class HostPolicyAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, HostPolicySignals signals)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
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
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        if (Request.Headers["X-Policy-Mode"] == "challenge")
            Response.Headers.WWWAuthenticate = "Synthetic challenge-leg";
        return Task.CompletedTask;
    }
}

internal sealed class HostPolicyHeaders(string mode, string id, bool signed) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        request.Headers.Add("X-Policy-Call", id);
        request.Headers.Add("X-Policy-Mode", mode);
        if (signed)
            request.Headers.Add("X-Synthetic-User", "synthetic-reader");
        return base.SendAsync(request, token);
    }
}

internal sealed class HostPolicyMcpHeaders : DelegatingHandler
{
    internal string CallId { get; } = Guid.NewGuid().ToString("N");
    private int calls;

    internal HostPolicyMcpHeaders() : base(new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

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

internal sealed record HostPolicyResponse(int Status, string? ContentType, byte[] Bytes, IReadOnlyDictionary<string, string> Headers, string Call);
internal sealed class HostPolicyFault(string message) : Exception(message);

internal sealed class HostPolicyWrites : JsonConverter<ErrorBody>
{
    private int attempts;
    internal int Attempts => Volatile.Read(ref attempts);
    internal bool Throw { get; set; }
    public override ErrorBody? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
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

internal sealed class HostPolicyProbe
{
    internal DefaultHttpContext Context { get; }
    internal MemoryStream Output { get; } = new();
    internal HostPolicyResponseFeature Response { get; }
    internal HostPolicyLifetime Lifetime { get; }
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
    internal HostPolicyResponse Reply => new(Context.Response.StatusCode, Context.Response.ContentType, Output.ToArray(),
        Context.Response.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()), "probe");
}

internal sealed class HostPolicyLifetime : IHttpRequestLifetimeFeature
{
    public CancellationToken RequestAborted { get; set; }
    internal int Aborts { get; private set; }
    public void Abort() => Aborts++;
}

internal sealed class HostPolicyResponseFeature : IHttpResponseFeature
{
    public int StatusCode { get; set; } = 200;
    public string? ReasonPhrase { get; set; }
    public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
    public Stream Body { get; set; } = Stream.Null;
    public bool HasStarted { get; set; }
    public void OnStarting(Func<object, Task> callback, object state) { }
    public void OnCompleted(Func<object, Task> callback, object state) { }
}

internal static class HostPolicyAssert
{
    internal const string Template = "Failure {IncidentId}: {Operation} for {Caller}: {Code}: {Message} ({Step}, {ExceptionType}, {SqlNumber})";
    internal static JsonElement Json(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        return json.RootElement.Clone();
    }
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
    internal static void Bare(HostPolicyResponse response, int status = 401, string body = "")
    {
        Assert.Equal(status, response.Status);
        Assert.Equal(Encoding.UTF8.GetBytes(body), response.Bytes);
    }
}
