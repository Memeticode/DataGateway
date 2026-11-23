using Microsoft.Extensions.Hosting;

namespace DataGateway;

/// <summary>Validates every registered gateway's catalog while the host starts, so a broken catalog stops startup
/// instead of failing the first caller.</summary>
/// <remarks>The check runs in <see cref="StartingAsync"/>, the lifecycle step that runs before any hosted service
/// starts, the web server included, so the server never listens with a catalog that would refuse every call. It
/// covers gateways that were registered but never mapped. Source connectivity is not checked: sources are
/// connected on the query path.</remarks>
/// <param name="registrations">Every registered gateway.</param>
/// <param name="scopes">Creates the separate service scope each gateway's catalog factory runs in.</param>
internal sealed class GatewayStartup(IEnumerable<GatewayRegistration> registrations,
    IServiceScopeFactory scopes) : IHostedLifecycleService
{
    /// <summary>Reads and checks each gateway's MCP metadata, source list and every active source's detail.</summary>
    /// <param name="cancellationToken">Ends the check when host startup is cancelled.</param>
    /// <returns>A task that fails with the first invalid gateway's message.</returns>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in registrations)
        {
            await using var scope = scopes.CreateAsyncScope();
            var catalog = registration.CreateCatalog(scope.ServiceProvider);
            await catalog.ValidateAsync(registration.Name, registration.McpTools, cancellationToken);
        }
    }

    /// <summary>No work at this lifecycle step.</summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <summary>No work at this lifecycle step.</summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>A completed task.</returns>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <summary>No work at this lifecycle step.</summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>A completed task.</returns>
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <summary>No work at this lifecycle step.</summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <summary>No work at this lifecycle step.</summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>A completed task.</returns>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
