using DataGateway.Core.Queries;

namespace DataGateway.Data;

public interface IDatabaseEngine
{
    /// <summary>The default registration name, matched ordinally ignoring case.</summary>
    string ProviderName { get; }
    CompiledQuery Compile(ValidatedQuery query);
    void ValidateSourceConnection(string? connection);
    ProviderFailure? RecognizeFailure(Exception error);
    /// <summary>Awaits one consumer of detached rows, then releases all source resources.</summary>
    /// <remarks>The consumer enumerates once and finishes every read before returning.
    /// Saved sequences and enumerators expire when the callback returns; materialized rows survive.
    /// The callback starts no work that outlives its scope. Opened runs once after successful open,
    /// before command creation. Cleanup completes before this task completes.</remarks>
    Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query,
        int commandTimeoutSeconds, Action opened,
        Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume,
        CancellationToken cancellationToken);
}
