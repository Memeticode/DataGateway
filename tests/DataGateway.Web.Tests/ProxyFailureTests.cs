extern alias RegistrationProxy;
using System.Net;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using RegistrationProxy::DataGateway.Proxy;
using DataGateway.Core.Tests;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>What the proxy tells a client when a tool call fails on the way to the gateway.</summary>
public sealed class ProxyFailureTests
{
    [Theory]
    [InlineData(null, "ServiceUnreachable", "could not be reached")]
    [InlineData(HttpStatusCode.Unauthorized, "ServiceRefused", "refused this computer's Windows sign-in (HTTP 401)")]
    [InlineData(HttpStatusCode.Forbidden, "ServiceRefused", "refused this computer's Windows sign-in (HTTP 403)")]
    [InlineData(HttpStatusCode.NotFound, "ServiceRefused", "answered with HTTP 404")]
    [InlineData(HttpStatusCode.InternalServerError, "ServiceRefused", "answered with HTTP 500")]
    [InlineData(HttpStatusCode.BadGateway, "ServiceUnreachable", "could not be reached")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ServiceUnreachable", "could not be reached")]
    [InlineData(HttpStatusCode.GatewayTimeout, "ServiceUnreachable", "could not be reached")]
    public void T1_an_answer_with_an_http_status_is_not_reported_as_unreachable(HttpStatusCode? status, string code, string says)
    {
        var result = ProxyServer.Failed("0123456789abcdef0123456789abcdef", status);
        Assert.True(result.IsError);
        using var body = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.Contains(says, body.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("restart", body.RootElement.GetProperty("message").GetString());
        Assert.Equal("0123456789abcdef0123456789abcdef", body.RootElement.GetProperty("incidentId").GetString());
    }

    [Fact]
    public void T2_the_proxy_sets_no_client_side_timeout_of_its_own()
    {
        var program = File.ReadAllText(TestFiles.InRepo("DataGateway.Proxy/Program.cs"));
        Assert.Contains("{ Timeout = Timeout.InfiniteTimeSpan }", program);
    }
}
