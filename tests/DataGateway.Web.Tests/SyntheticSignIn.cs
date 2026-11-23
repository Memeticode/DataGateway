using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataGateway.Web.Tests;

/// <summary>Test-only sign-in schemes that stand in for Windows sign-in: one that signs every caller in, one that
/// throws, one that stalls, and one that answers like the first leg of a Negotiate handshake.</summary>
/// <remarks>A test host cannot run a real Kerberos or NTLM handshake, but the gateway's behaviour around sign-in
/// (who is the caller, what a sign-in fault or a hang looks like in the log) still needs proof. Each scheme is added
/// beside the host's real one; the <c>Use</c> methods make theirs the default, so the host's own policy runs
/// unchanged against it.</remarks>
internal static class SyntheticSignIn
{
    /// <summary>Signs every request in as <c>synthetic-user</c>, or as the name in the <c>X-Synthetic-User</c>
    /// header.</summary>
    /// <remarks>The header lets concurrent tests give each request its own caller.</remarks>
    internal static void Use(WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, SignedIn>("Synthetic", _ => { });
        // Production sets DefaultScheme during Configure; this test-only seam runs after every Configure.
        builder.Services.PostConfigure<AuthenticationOptions>(options => options.DefaultScheme = "Synthetic");
    }

    /// <summary>Makes every sign-in attempt throw a <see cref="SyntheticFault"/>, to prove a fault inside sign-in
    /// becomes the gateway's UnexpectedError with an anonymous caller.</summary>
    internal static void UseFaulting(WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, Faulting>("SyntheticFault", _ => { });
        builder.Services.PostConfigure<AuthenticationOptions>(o => o.DefaultScheme = "SyntheticFault");
    }

    /// <summary>Makes every sign-in attempt signal <paramref name="started"/> and then wait until the caller goes
    /// away.</summary>
    /// <remarks>Lets a test cancel a request while it is still inside sign-in, before routing knows the
    /// operation.</remarks>
    internal static void UseStalling(WebApplicationBuilder builder, TaskCompletionSource started)
    {
        builder.Services.AddSingleton(new StallStarted(started));
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, Stalling>("SyntheticStall", _ => { });
        builder.Services.PostConfigure<AuthenticationOptions>(o => o.DefaultScheme = "SyntheticStall");
    }

    /// <summary>The scheme behind <see cref="UseFaulting"/>.</summary>
    private sealed class Faulting(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        /// <summary>Throws instead of deciding.</summary>
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => throw new SyntheticFault("synthetic sign-in fault");
    }

    /// <summary>Carries the test's signal into <see cref="Stalling"/> through services.</summary>
    private sealed record StallStarted(TaskCompletionSource Signal);

    /// <summary>The scheme behind <see cref="UseStalling"/>.</summary>
    private sealed class Stalling(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, StallStarted started) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        /// <summary>Signals the test, then waits for the request to be aborted.</summary>
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            started.Signal.TrySetResult();
            await Task.Delay(Timeout.Infinite, Context.RequestAborted);
            return AuthenticateResult.NoResult();
        }
    }

    /// <summary>Adds a scheme that answers every request itself with 401 and <c>WWW-Authenticate: Negotiate
    /// synthetic-leg</c>, as a Negotiate handshake does on its first leg.</summary>
    /// <remarks>It is not made the default: a request handler answers inside the authentication middleware whatever
    /// the default is, which is how the first leg reaches the caller before any endpoint or catalog runs.</remarks>
    internal static void UseHandshakeLeg(WebApplicationBuilder builder) =>
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HandshakeLeg>("SyntheticLeg", _ => { });

    /// <summary>The scheme behind <see cref="Use"/>.</summary>
    private sealed class SignedIn(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        /// <summary>Signs the caller in under this scheme.</summary>
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, Request.Headers.TryGetValue("X-Synthetic-User", out var caller) ? caller.ToString() : "synthetic-user")], Scheme.Name)), Scheme.Name)));
    }

    /// <summary>The scheme behind <see cref="UseHandshakeLeg"/>.</summary>
    private sealed class HandshakeLeg(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder), IAuthenticationRequestHandler
    {
        /// <summary>Ends every request with the first-leg challenge.</summary>
        public Task<bool> HandleRequestAsync()
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = "Negotiate synthetic-leg";
            return Task.FromResult(true);
        }

        /// <summary>Never signs anyone in; the request handler answers first.</summary>
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
