using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;

namespace DataGateway.Web.SignIn;

/// <summary>This host's sign-in policy: every request must come from a caller signed in with Windows (HTTP
/// Negotiate: Kerberos, or NTLM as fallback), and a refused caller gets the gateway's own JSON error.</summary>
/// <remarks>
/// <para>Sign-in belongs to the host, not the library: DataGateway only serves requests the host lets through
/// (<see cref="DataGatewayBuilder.UseHostPolicy"/>). Another host could use tokens or nothing at all with the same
/// library.</para>
/// <para>The same code runs on Kestrel and on IIS. On IIS with Windows Authentication turned on, the Negotiate
/// handler leaves the handshake to IIS, so IIS answers anonymous callers before the app sees them
/// (DataGateway.Web/IIS.md).</para>
/// </remarks>
internal static class WindowsSignIn
{
    /// <summary>The sentence the API page adds to its description to say every call needs Windows sign-in.</summary>
    internal const string Description = "Every call needs Windows sign-in.";

    /// <summary>The API page's description of each operation's 401 answer.</summary>
    /// <remarks>Built from <see cref="DataGatewayError.NotAuthenticated"/> so the page names the code the wire
    /// actually carries, and cannot drift from it.</remarks>
    internal static string ResponseDescription => $"Unauthorized. The code is one of: {DataGatewayError.NotAuthenticated.Code}. The first leg of sign-in answers with WWW-Authenticate: Negotiate and no incidentId; a refused handshake adds an incidentId.";

    /// <summary>The error a caller receives when sign-in is missing or refused: code NotAuthenticated at the root
    /// path, with a message that names the fix.</summary>
    /// <remarks>Step "sign-in" marks the log line, so an operator can tell a refused handshake from a failure
    /// later in the request.</remarks>
    internal static CatalogFailure NotAuthenticated { get; } = new(DataGatewayError.NotAuthenticated.Code, "$",
        "Sign in with Windows to call DataGateway: send the request with Windows credentials (HTTP Negotiate).")
        { Step = "sign-in" };

    /// <summary>Registers Windows sign-in and makes it required for every endpoint.</summary>
    /// <remarks>A fallback policy, not an attribute per endpoint: the gateway maps its routes and <c>/mcp</c> inside
    /// the library, where no attribute can be added, and a route added later is closed by default instead of left
    /// open by omission.</remarks>
    /// <param name="services">The host's service collection.</param>
    internal static void Configure(IServiceCollection services)
    {
        services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
            .AddNegotiate(options => options.Events = new NegotiateEvents { OnAuthenticationFailed = Refuse });
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    }

    /// <summary>Adds the sign-in and access checks to the request pipeline.</summary>
    /// <remarks>Call after <c>UseRouting</c> and before the gateway is mapped, so the fallback policy sees each
    /// endpoint before it runs.</remarks>
    /// <param name="app">The host's application.</param>
    internal static void Require(WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
    }

    /// <summary>Turns a failed Negotiate handshake into the gateway's NotAuthenticated error, with an incident id.</summary>
    /// <remarks>Without this, a refused handshake throws, and the caller gets a bare 500. Recording the refusal
    /// lets the gateway answer 401 with its JSON body and log the exception's type name, never the exception
    /// itself, which can carry account details. <c>SkipHandler</c> then lets authorization send the challenge.</remarks>
    /// <param name="context">The failed handshake.</param>
    /// <returns>A completed task: the work is synchronous.</returns>
    private static Task Refuse(AuthenticationFailedContext context)
    {
        context.HttpContext.TryRecordDataGatewayRefusal(
            NotAuthenticated with { ExceptionType = context.Exception.GetType().Name });
        context.SkipHandler();
        return Task.CompletedTask;
    }
}
