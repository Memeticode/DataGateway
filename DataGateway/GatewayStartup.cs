using Microsoft.Extensions.Hosting;

namespace DataGateway;

internal sealed class GatewayStartup(IEnumerable<GatewayRegistration> registrations,
    IServiceScopeFactory scopes) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in registrations)
        {
            await using var scope = scopes.CreateAsyncScope();
            var catalog = registration.CreateCatalog(scope.ServiceProvider);
            await catalog.ValidateAsync(registration.Name, cancellationToken);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
