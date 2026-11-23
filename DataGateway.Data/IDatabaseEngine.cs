using DataGateway.Core.Queries;

namespace DataGateway.Data;

/// <summary>A database engine: compiles validated queries into its SQL dialect and runs them on a source
/// database.</summary>
/// <remarks>
/// <para>Most engines derive from <see cref="DatabaseEngine{TConnection}"/>, which implements compilation, bounded
/// reading and resource cleanup and leaves only the dialect differences. Implementing this interface directly is
/// possible; <see cref="Queries.QueryRunner"/> then still bounds the rows with its own consumer.</para>
/// <para>An engine reports facts only. <see cref="Queries.QueryRunner"/> turns its exceptions and
/// <see cref="ProviderFailure"/> facts into the gateway's public codes, so no engine adds a code of its own.</para>
/// </remarks>
public interface IDatabaseEngine
{
    /// <summary>The default registration name, matched ordinally ignoring case.</summary>
    string ProviderName { get; }
    /// <summary>Compiles a validated query into this engine's SQL, asking for one row more than the answer
    /// keeps.</summary>
    /// <param name="query">The validated query; left unchanged.</param>
    /// <returns>The SQL, its named parameters and the row facts.</returns>
    CompiledQuery Compile(ValidatedQuery query);
    /// <summary>Checks a source connection string locally, without opening it.</summary>
    /// <remarks>Throw <see cref="ArgumentException"/> or <see cref="FormatException"/> for text that cannot be
    /// used; the gateway reports it as a catalog fault. Never echo the string: it can carry a password.</remarks>
    /// <param name="connection">The source connection string from the catalog.</param>
    void ValidateSourceConnection(string? connection);
    /// <summary>Reads the provider facts from an exception this engine's driver raised.</summary>
    /// <param name="error">The exception.</param>
    /// <returns>The timeout and error-number facts, or null when the exception is not the driver's.</returns>
    ProviderFailure? RecognizeFailure(Exception error);
    /// <summary>Awaits one consumer of detached rows, then releases all source resources.</summary>
    /// <remarks>The consumer enumerates once and finishes every read before returning.
    /// Saved sequences and enumerators expire when the callback returns; materialized rows survive.
    /// The callback starts no work that outlives its scope. Opened runs once after successful open,
    /// before command creation. Cleanup completes before this task completes.</remarks>
    /// <param name="connection">The source connection string, used exactly as given.</param>
    /// <param name="query">The compiled query.</param>
    /// <param name="commandTimeoutSeconds">The driver's per-command timeout, in seconds.</param>
    /// <param name="opened">Called once the connection is open, before the command is created.</param>
    /// <param name="consume">The one consumer of the rows; it returns the kept rows.</param>
    /// <param name="cancellationToken">Cancels the open, the command and every read.</param>
    /// <returns>What <paramref name="consume"/> returned.</returns>
    Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query,
        int commandTimeoutSeconds, Action opened,
        Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume,
        CancellationToken cancellationToken);
}
