using DataGateway.Core.Queries;
using DataGateway.Data;

namespace DataGateway.Web.Configuration;

// The gateway is configured before Build; the DI-owned singleton is selected and bound after Build.
internal sealed class HostDatabaseEngine(string providerName) : IDatabaseEngine
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
}
