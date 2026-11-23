using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>QueryRunner passes the engine its own deadline token, linked to the caller's, never the caller's itself.</summary>
internal static class DeadlineToken
{
    /// <summary>Asserts that <paramref name="passed"/> is a cancelable token distinct from <paramref name="caller"/>.</summary>
    /// <param name="caller">The token the test handed to the runner.</param>
    /// <param name="passed">The token the engine received.</param>
    internal static void Passed(CancellationToken caller, CancellationToken passed)
    {
        Assert.NotEqual(caller, passed);
        Assert.True(passed.CanBeCanceled);
    }
}
