using DataGateway.Core.Queries;
using DataGateway.Data;

namespace DataGateway.Web.Configuration;

/// <summary>The engine the gateway is given at registration: it forwards every call to the engine selected from the
/// service provider once the host is built.</summary>
/// <remarks>The gateway is configured before Build; the DI-owned singleton is selected and bound after Build. That
/// lets a host or test replace the source engine with its own <see cref="IDatabaseEngine"/> registration, while the
/// gateway still finds an engine under the provider name the catalog details carry.</remarks>
/// <param name="providerName">The provider name the selected catalog reader puts on each detail.</param>
internal sealed class HostDatabaseEngine(string providerName) : IDatabaseEngine
{
    /// <summary>The bound engine; null until <see cref="Bind"/> runs.</summary>
    private IDatabaseEngine? selected;
    /// <summary>The provider name this alias is registered under.</summary>
    public string ProviderName { get; } = providerName;
    /// <summary>The bound engine; a call before binding is a composition bug and throws.</summary>
    private IDatabaseEngine Engine => selected ?? throw new InvalidOperationException("The host engine has not been bound.");

    /// <summary>Binds the forwarding target, exactly once.</summary>
    /// <remarks>A second bind throws instead of replacing the target, so the engine serving queries cannot change
    /// after startup.</remarks>
    /// <param name="engine">The engine resolved from the service provider.</param>
    internal void Bind(IDatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (Interlocked.CompareExchange(ref selected, engine, null) is not null)
            throw new InvalidOperationException("The host engine has already been bound.");
    }

    /// <inheritdoc/>
    public CompiledQuery Compile(ValidatedQuery query) => Engine.Compile(query);
    /// <inheritdoc/>
    public void ValidateSourceConnection(string? connection) => Engine.ValidateSourceConnection(connection);
    /// <inheritdoc/>
    public ProviderFailure? RecognizeFailure(Exception error) => Engine.RecognizeFailure(error);
    /// <inheritdoc/>
    public Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query,
        int commandTimeoutSeconds, Action opened,
        Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume,
        CancellationToken cancellationToken) =>
        Engine.ReadSourceAsync(connection, query, commandTimeoutSeconds, opened, consume, cancellationToken);
}
