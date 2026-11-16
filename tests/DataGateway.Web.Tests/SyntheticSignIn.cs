using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataGateway.Web.Tests;

internal static class SyntheticSignIn
{
    internal static void Use(WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, SignedIn>("Synthetic", _ => { });
        // Production sets DefaultScheme during Configure; this test-only seam runs after every Configure.
        builder.Services.PostConfigure<AuthenticationOptions>(options => options.DefaultScheme = "Synthetic");
    }

    internal static void UseFaulting(WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, Faulting>("SyntheticFault", _ => { });
        builder.Services.PostConfigure<AuthenticationOptions>(o => o.DefaultScheme = "SyntheticFault");
    }

    internal static void UseStalling(WebApplicationBuilder builder, TaskCompletionSource started)
    {
        builder.Services.AddSingleton(new StallStarted(started));
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, Stalling>("SyntheticStall", _ => { });
        builder.Services.PostConfigure<AuthenticationOptions>(o => o.DefaultScheme = "SyntheticStall");
    }

    private sealed class Faulting(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => throw new SyntheticFault("synthetic sign-in fault");
    }

    private sealed record StallStarted(TaskCompletionSource Signal);

    private sealed class Stalling(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, StallStarted started) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            started.Signal.TrySetResult();
            await Task.Delay(Timeout.Infinite, Context.RequestAborted);
            return AuthenticateResult.NoResult();
        }
    }

    internal static void UseHandshakeLeg(WebApplicationBuilder builder) =>
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HandshakeLeg>("SyntheticLeg", _ => { });

    private sealed class SignedIn(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, Request.Headers.TryGetValue("X-Synthetic-User", out var caller) ? caller.ToString() : "synthetic-user")], Scheme.Name)), Scheme.Name)));
    }

    private sealed class HandshakeLeg(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder), IAuthenticationRequestHandler
    {
        public Task<bool> HandleRequestAsync()
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = "Negotiate synthetic-leg";
            return Task.FromResult(true);
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
