using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;

namespace DataGateway.Web.SignIn;

internal static class WindowsSignIn
{
    internal const string Description = "Every call needs Windows sign-in.";
    internal static string ResponseDescription => $"Unauthorized. The code is one of: {DataGatewayError.NotAuthenticated.Code}. The first leg of sign-in answers with WWW-Authenticate: Negotiate and no incidentId; a refused handshake adds an incidentId.";
    internal static CatalogFailure NotAuthenticated { get; } = new(DataGatewayError.NotAuthenticated.Code, "$",
        "Sign in with Windows to call DataGateway: send the request with Windows credentials (HTTP Negotiate).")
        { Step = "sign-in" };

    internal static void Configure(IServiceCollection services)
    {
        services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
            .AddNegotiate(options => options.Events = new NegotiateEvents { OnAuthenticationFailed = Refuse });
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    }

    internal static void Require(WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
    }

    private static Task Refuse(AuthenticationFailedContext context)
    {
        context.HttpContext.TryRecordDataGatewayRefusal(
            NotAuthenticated with { ExceptionType = context.Exception.GetType().Name });
        context.SkipHandler();
        return Task.CompletedTask;
    }
}
