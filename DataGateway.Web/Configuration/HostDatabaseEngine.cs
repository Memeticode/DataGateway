using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data;

namespace DataGateway.Web.Configuration;

// The gateway is configured before Build; the DI-owned singleton is selected and bound after Build.
internal sealed class HostDatabaseEngine(string providerName) : IDatabaseEngine, IStoreTypeEngine
{
    private IDatabaseEngine? selected;
    public string ProviderName { get; } = providerName;
    private IDatabaseEngine Engine => selected ?? throw new InvalidOperationException("The host engine has not been bound.");

    internal void Bind(IDatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (Interlocked.CompareExchange(ref selected, engine, null) is not null)
            throw new InvalidOperationException("The host engine has already been bound.");
    }

    public CompiledQuery Compile(ValidatedQuery query) => Engine.Compile(query);
    public void ValidateSourceConnection(string? connection) => Engine.ValidateSourceConnection(connection);
    public ProviderFailure? RecognizeFailure(Exception error) => Engine.RecognizeFailure(error);
    public Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query,
        int commandTimeoutSeconds, Action opened,
        Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume,
        CancellationToken cancellationToken) =>
        Engine.ReadSourceAsync(connection, query, commandTimeoutSeconds, opened, consume, cancellationToken);

    // Forwards the store-type check to the selected engine when it has one (the SQL Server engine), so wrapping an
    // engine never hides it; any other engine's sources bind as before.
    public Task<IReadOnlyDictionary<string, StoreType>> ResolveStoreTypesAsync(SourceContract source,
        Action<StoreTypeFinding> report, CancellationToken cancellationToken) =>
        Engine is IStoreTypeEngine typed
            ? typed.ResolveStoreTypesAsync(source, report, cancellationToken)
            : Task.FromResult<IReadOnlyDictionary<string, StoreType>>(new Dictionary<string, StoreType>());
}
