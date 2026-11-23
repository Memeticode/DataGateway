using System.Text;
using System.Text.Json;
using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using DataGateway.Data.SqlServer;
using DataGateway.Routes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>Pins the host error boundary, <c>UseDataGatewayErrors</c>: the middleware a host installs outermost so
/// that a failure or sign-in refusal anywhere in its pipeline still gets the gateway's JSON error and one log record.</summary>
/// <remarks>
/// <para>TERM refusal: a sign-in failure the host records on the request with <c>TryRecordDataGatewayRefusal</c>; the
/// boundary turns it into the NotAuthenticated answer once the response is a bare 401. The first refusal recorded
/// wins.</para>
/// <para>TERM effective registration: the gateway whose settings and log scope apply to a request, which is the
/// gateway of the matched endpoint when there is one, else the gateway named when the boundary was installed.</para>
/// </remarks>
public sealed class HostBoundaryTests
{
    /// <summary>The name of the gateway the boundary is installed for.</summary>
    private const string Root = "DataGateway.Web";
    /// <summary>A NotAuthenticated refusal at the sign-in step.</summary>
    /// <param name="message">The message; tests use the gateway name to tell which gateway's refusal was used.</param>
    private static CatalogFailure Refusal(string message = "Sign in.") =>
        new(DataGatewayError.NotAuthenticated.Code, "$", message) { Step = "sign-in", ExceptionType = "SyntheticFault" };

    /// <summary>Services with two registered gateways, the root and <c>foreign</c>, whose limits and default refusal
    /// differ, logging into <paramref name="logs"/>.</summary>
    private static ServiceProvider Services(NamedGatewayLogs logs)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs));
        foreach (var name in new[] { Root, "foreign" })
            services.AddDataGateway(name, gateway => gateway
                .UseCatalog<Microsoft.Data.SqlClient.SqlConnection>(_ => new HostCatalogFixture(
                    _ => Task.FromResult<IReadOnlyList<SourceListRow>>([]), (_, _) => Task.FromResult<CatalogDetail?>(null)))
                .AddEngine(new SqlServerEngine())
                .UseLimits(new GatewayLimits { MaxJsonLength = name == Root ? 20 : 40 })
                .UseHostPolicy(Refusal(name), "Sign in.", "Unauthorized."));
        return services.BuildServiceProvider();
    }

    /// <summary>A bare request context with a readable response body.</summary>
    private static DefaultHttpContext Context(IServiceProvider services) => new()
    {
        RequestServices = services,
        Response = { Body = new MemoryStream() }
    };

    /// <summary>Installing the boundary checks its arguments and that the gateway is registered, by exact name, at once
    /// rather than on the first request, and returns the same builder.</summary>
    [Fact]
    public async Task T4_guards_resolve_immediately_and_return_the_same_builder()
    {
        Assert.Throws<ArgumentNullException>(() => DataGatewayExtensions.UseDataGatewayErrors(null!, null!));
        await using var provider = new ServiceCollection().BuildServiceProvider();
        IApplicationBuilder app = new ApplicationBuilder(provider);
        foreach (var name in new string?[] { null, "", " \t" })
            Assert.Equal("Gateway name must contain a non-whitespace character.",
                Assert.Throws<InvalidOperationException>(() => app.UseDataGatewayErrors(name!)).Message);
        Assert.Equal("Gateway 'DataGateway.Web'. Register this gateway with AddDataGateway before installing its host error boundary.",
            Assert.Throws<InvalidOperationException>(() => app.UseDataGatewayErrors(Root)).Message);
        await using var registered = Services(new());
        app = new ApplicationBuilder(registered);
        Assert.Same(app, app.UseDataGatewayErrors(Root));
        Assert.Equal("Gateway 'datagateway.web'. Register this gateway with AddDataGateway before installing its host error boundary.",
            Assert.Throws<InvalidOperationException>(() => app.UseDataGatewayErrors("datagateway.web")).Message);
    }

    /// <summary>A recorded refusal becomes one answer and one log record, even when the boundary is installed more than
    /// once; the first refusal wins, the host's challenge header is kept, and the record carries the effective gateway.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task T4_outer_boundary_completes_once_and_first_record_wins(bool twice, bool selected)
    {
        var logs = new NamedGatewayLogs();
        await using var provider = Services(logs);
        var app = new ApplicationBuilder(provider);
        app.UseDataGatewayErrors(Root);
        app.Use(async (http, next) =>
        {
            if (selected) Select(http, provider, "foreign");
            await next(http);
        });
        if (twice) { app.UseDataGatewayErrors(Root); app.UseDataGatewayErrors(); }
        app.Run(http =>
        {
            Assert.True(http.TryRecordDataGatewayRefusal(Refusal("First.")));
            Assert.False(http.TryRecordDataGatewayRefusal(Refusal("Second.")));
            http.Response.StatusCode = 401;
            http.Response.Headers.WWWAuthenticate = "Negotiate opaque-continuation";
            return Task.CompletedTask;
        });
        var context = Context(provider);
        await app.Build()(context);
        Assert.False(context.TryRecordDataGatewayRefusal(Refusal()));
        Assert.Equal("Negotiate opaque-continuation", context.Response.Headers.WWWAuthenticate.ToString());
        Assert.Equal(401, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        using var answer = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
        Assert.Equal(new[] { "code", "path", "message", "incidentId" }, answer.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("First.", answer.RootElement.GetProperty("message").GetString());
        var line = Assert.Single(logs.Failures);
        Assert.Equal(answer.RootElement.GetProperty("incidentId").GetString(), line.Fields["IncidentId"]);
        Assert.Equal(selected ? "foreign" : Root, line.Scopes["Gateway"]);
        Assert.Equal("NotAuthenticated", line.Fields["Code"]);
        Assert.Equal(LogLevel.Error, line.Level);
    }

    /// <summary>An exception from the pipeline becomes one UnexpectedError answer and one record under the effective
    /// gateway, after which no refusal can be recorded.</summary>
    [Theory]
    [InlineData("before-routing", false)]
    [InlineData("routing", false)]
    [InlineData("authentication", true)]
    [InlineData("host-endpoint", false)]
    [InlineData("unmatched", false)]
    [InlineData("method-rejection", true)]
    public async Task T4_faults_use_effective_registration_and_one_incident(string stage, bool selected)
    {
        var logs = new NamedGatewayLogs();
        await using var provider = Services(logs);
        var app = new ApplicationBuilder(provider);
        app.UseDataGatewayErrors(Root);
        app.Run(http =>
        {
            if (selected) Select(http, provider, "foreign");
            throw new IOException("Synthetic " + stage);
        });
        var context = Context(provider);
        await app.Build()(context);
        Assert.Equal(500, context.Response.StatusCode);
        using var body = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
        Assert.Equal("UnexpectedError", body.RootElement.GetProperty("code").GetString());
        var line = Assert.Single(logs.Failures);
        Assert.Equal(body.RootElement.GetProperty("incidentId").GetString(), line.Fields["IncidentId"]);
        Assert.Equal(selected ? "foreign" : Root, line.Scopes["Gateway"]);
        Assert.Equal("IOException", line.Fields["ExceptionType"]);
        Assert.Null(line.Fields["Sql"]);
        Assert.False(context.TryRecordDataGatewayRefusal(Refusal()));
    }

    /// <summary>A 401 challenge with no recorded refusal gets the gateway's default refusal body but no incident id and no
    /// log record, since a challenge is the normal first step of sign-in.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T7_plain_and_continuation_challenges_have_no_incident(bool continuation)
    {
        var logs = new NamedGatewayLogs();
        await using var provider = Services(logs);
        var app = new ApplicationBuilder(provider);
        app.UseDataGatewayErrors(Root);
        app.Run(http =>
        {
            http.Response.StatusCode = 401;
            http.Response.Headers.WWWAuthenticate = continuation ? "Negotiate continuation" : "Negotiate";
            return Task.CompletedTask;
        });
        var context = Context(provider);
        await app.Build()(context);
        Assert.Equal("{\"code\":\"NotAuthenticated\",\"path\":\"$\",\"message\":\"DataGateway.Web\"}",
            Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
        Assert.Equal(continuation ? "Negotiate continuation" : "Negotiate", context.Response.Headers.WWWAuthenticate.ToString());
        Assert.Empty(logs.Failures);
    }

    /// <summary>A recorded refusal is dropped, with no body and no record, when the response is not a 401 or the host
    /// has already set its own content length or content type.</summary>
    [Theory]
    [InlineData(200, false, false)]
    [InlineData(403, false, false)]
    [InlineData(401, true, false)]
    [InlineData(401, false, true)]
    public async Task T4_provisional_refusal_is_discarded_when_response_is_ineligible(int status, bool length, bool contentType)
    {
        var logs = new NamedGatewayLogs();
        await using var provider = Services(logs);
        var app = new ApplicationBuilder(provider);
        app.UseDataGatewayErrors(Root);
        app.Run(http =>
        {
            Assert.True(http.TryRecordDataGatewayRefusal(Refusal()));
            http.Response.StatusCode = status;
            if (length) http.Response.ContentLength = 0;
            if (contentType) http.Response.ContentType = "text/plain";
            return Task.CompletedTask;
        });
        var context = Context(provider);
        await app.Build()(context);
        Assert.Empty(logs.Failures);
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
    }

    /// <summary>The boundary installed without a gateway name leaves requests outside the gateway's endpoints to the host.</summary>
    [Fact]
    public async Task T4_existing_overload_still_ignores_unselected_host_requests()
    {
        await using var provider = Services(new());
        var app = new ApplicationBuilder(provider);
        app.UseDataGatewayErrors();
        app.Run(_ => throw new IOException("Host-owned."));
        await Assert.ThrowsAsync<IOException>(() => app.Build()(Context(provider)));
    }

    /// <summary>In a real ASP.NET Core host, failures before routing, in authentication, in a refused handshake and in a
    /// host handler all get the gateway's answer and one record, while a wrong-method 405 is left untouched.</summary>
    [Theory]
    [InlineData("before-routing")]
    [InlineData("authentication")]
    [InlineData("refused-handshake")]
    [InlineData("wrong-method")]
    [InlineData("handler")]
    public async Task T4_real_ASPNET_pipeline_keeps_whole_host_boundary(string stage)
    {
        var logs = new NamedGatewayLogs();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", ApplicationName = "DataGateway.Web", Args = [] });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        DataGateway.Web.SignIn.WindowsSignIn.Configure(builder.Services);
        if (stage == "authentication") SyntheticSignIn.UseFaulting(builder);
        else if (stage == "refused-handshake")
        {
            builder.Services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, RefusedSignIn>("Refused", _ => { });
            builder.Services.PostConfigure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(options => options.DefaultScheme = "Refused");
        }
        else SyntheticSignIn.Use(builder);
        builder.Services.AddDataGateway(Root, gateway => gateway.AddEngine(new SqlServerEngine())
            .UseCatalog<Microsoft.Data.SqlClient.SqlConnection>(_ => new HostCatalogFixture(_ => Task.FromResult<IReadOnlyList<SourceListRow>>([]), (_, _) => Task.FromResult<CatalogDetail?>(null)))
            .UseHostPolicy(Refusal(), "Sign in.", "Unauthorized."));
        await using var app = builder.Build();
        app.UseDataGatewayErrors(Root);
        if (stage == "before-routing") app.Use((HttpContext _, RequestDelegate _) => throw new IOException("Before routing."));
        app.UseRouting();
        DataGateway.Web.SignIn.WindowsSignIn.Require(app);
        app.MapDataGateway(Root, "");
        app.MapGet("/handler", (HttpContext _) => Task.FromException(new IOException("Inside handler.")));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(deadline.Token);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        using var response = await client.GetAsync(stage == "wrong-method" ? "/query" : stage == "handler" ? "/handler" : "/not-a-route", deadline.Token);
        var text = await response.Content.ReadAsStringAsync(deadline.Token);
        Assert.Equal(stage == "wrong-method" ? 405 : stage == "refused-handshake" ? 401 : 500, (int)response.StatusCode);
        if (stage == "wrong-method") { Assert.Empty(logs.Failures); Assert.Empty(text); return; }
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        using var body = JsonDocument.Parse(text);
        Assert.Equal(new[] { "code", "path", "message", "incidentId" }, body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(stage == "refused-handshake" ? "NotAuthenticated" : "UnexpectedError", body.RootElement.GetProperty("code").GetString());
        var line = Assert.Single(logs.Failures);
        Assert.Equal(Root, line.Scopes["Gateway"]);
        Assert.Equal(body.RootElement.GetProperty("incidentId").GetString(), line.Fields["IncidentId"]);
        Assert.Null(line.Fields["Operation"]);
        Assert.Null(line.Fields["Request"]);
        if (stage == "refused-handshake") Assert.Equal("Negotiate synthetic-refusal", response.Headers.WwwAuthenticate.ToString());
    }

    /// <summary>An authentication handler that records two refusals and fails, then challenges with a continuation
    /// header, to model a handshake the server refuses.</summary>
    private sealed class RefusedSignIn(Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(options, logger, encoder)
    {
        /// <summary>Records a first and a second refusal, asserting only the first is kept, and fails authentication.</summary>
        protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
        {
            Assert.True(Context.TryRecordDataGatewayRefusal(Refusal("First handshake refusal.")));
            Assert.False(Context.TryRecordDataGatewayRefusal(Refusal("Second handshake refusal.")));
            return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Fail("Synthetic refused handshake."));
        }
        /// <summary>Answers 401 with a Negotiate continuation header.</summary>
        protected override Task HandleChallengeAsync(Microsoft.AspNetCore.Authentication.AuthenticationProperties properties)
        {
            Response.StatusCode = 401;
            Response.Headers.WWWAuthenticate = "Negotiate synthetic-refusal";
            return Task.CompletedTask;
        }
    }

    /// <summary>Marks the request as matched to an endpoint of the gateway named <paramref name="name"/>.</summary>
    private static void Select(HttpContext http, IServiceProvider services, string name) =>
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(services.GetServices<GatewayRegistration>().Single(r => r.Name == name)), name));
}
