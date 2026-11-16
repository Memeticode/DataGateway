using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>QueryRunner passes the engine its own deadline token, linked to the caller's, never the caller's itself.</summary>
internal static class DeadlineToken
{
    internal static void Passed(CancellationToken caller, CancellationToken passed)
    {
        Assert.NotEqual(caller, passed);
        Assert.True(passed.CanBeCanceled);
    }
}
