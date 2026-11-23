using System.Runtime.CompilerServices;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Queries;

namespace DataGateway.Web.Tests;

/// <summary>Builds database engines whose rows come from a test delegate instead of a database, while keeping a real
/// engine's compilation, connection checks and failure recognition.</summary>
/// <remarks>Only the read is faked: the query still compiles through the real engine, so a test sees the same SQL
/// and the same error mapping production uses, and controls only what the source returns and when.</remarks>
internal static class FakeRows
{
    /// <summary>Wraps <paramref name="engine"/> so its reads return the rows <paramref name="read"/> yields.</summary>
    /// <param name="engine">The real engine that compiles queries and recognizes failures.</param>
    /// <param name="read">Yields the rows for a connection and compiled query; it receives the engine's
    /// "connection opened" callback and the token the engine was given.</param>
    /// <returns>An engine with the fake read.</returns>
    internal static IDatabaseEngine Engine(IDatabaseEngine engine,
        Func<string, CompiledQuery, Action, CancellationToken,
            IAsyncEnumerable<IDictionary<string, object?>>> read) => new RowsEngine(engine, read);

    /// <summary>A <see cref="QueryRunner"/> over <see cref="Engine"/>'s fake engine.</summary>
    /// <param name="engine">The real engine that compiles queries and recognizes failures.</param>
    /// <param name="read">Yields the rows for a connection and compiled query.</param>
    /// <returns>A runner that reads from the delegate.</returns>
    internal static QueryRunner Runner(IDatabaseEngine engine,
        Func<string, CompiledQuery, Action, CancellationToken,
            IAsyncEnumerable<IDictionary<string, object?>>> read) =>
        new(new RowsEngine(engine, read));

    /// <summary>Adapts a delegate that returns a whole list of rows (the shape Dapper's <c>dynamic</c> rows come in) to
    /// the streaming read <see cref="Engine"/> takes.</summary>
    /// <param name="read">Returns every row at once; each row must be an <c>IDictionary&lt;string, object?&gt;</c>.</param>
    /// <returns>A read that yields those rows one by one.</returns>
    internal static Func<string, CompiledQuery, Action, CancellationToken,
        IAsyncEnumerable<IDictionary<string, object?>>> From(
        Func<string, CompiledQuery, Action, CancellationToken, Task<IReadOnlyList<dynamic>>> read) =>
        (connection, query, connected, token) => Iterate(read, connection, query, connected, token);

    /// <summary>Awaits the whole list from <paramref name="read"/>, then yields its rows.</summary>
    private static async IAsyncEnumerable<IDictionary<string, object?>> Iterate(
        Func<string, CompiledQuery, Action, CancellationToken, Task<IReadOnlyList<dynamic>>> read,
        string connection, CompiledQuery query, Action connected,
        [EnumeratorCancellation] CancellationToken token)
    {
        var rows = await read(connection, query, connected, token);
        foreach (object row in rows) yield return (IDictionary<string, object?>)row;
    }

    /// <summary>Copies <paramref name="values"/> into a dictionary that keeps insertion order and compares keys ordinally.</summary>
    /// <remarks>Stands in for a row read from a database, which keeps the order the query selected its columns in, so a
    /// test can run the same values both as a plain dictionary and as a database-shaped row.</remarks>
    internal static IDictionary<string, object?> Ordered(IDictionary<string, object?> values)
    {
        var row = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in values) row.Add(pair.Key, pair.Value);
        return row;
    }

    /// <summary>The engine <see cref="Engine"/> returns: delegates everything to the real engine except the read.</summary>
    private sealed class RowsEngine(IDatabaseEngine engine,
        Func<string, CompiledQuery, Action, CancellationToken,
            IAsyncEnumerable<IDictionary<string, object?>>> read) : IDatabaseEngine
    {
        /// <summary>The real engine's provider name.</summary>
        string IDatabaseEngine.ProviderName => engine.ProviderName;
        /// <summary>Compiles with the real engine.</summary>
        public CompiledQuery Compile(ValidatedQuery query) => engine.Compile(query);
        /// <summary>Checks the connection with the real engine.</summary>
        public void ValidateSourceConnection(string? connection) => engine.ValidateSourceConnection(connection);
        /// <summary>Maps failures with the real engine.</summary>
        public ProviderFailure? RecognizeFailure(Exception error) => engine.RecognizeFailure(error);
        /// <summary>Hands the fake rows to the consumer; no connection is opened, and <paramref name="opened"/> is passed to
        /// the delegate, which decides when, or whether, the connection counts as open.</summary>
        /// <remarks><paramref name="commandTimeoutSeconds"/> is ignored: a delegate that wants to model a slow source waits
        /// on the token instead.</remarks>
        public Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query,
            int commandTimeoutSeconds, Action opened,
            Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume,
            CancellationToken cancellationToken) =>
            consume(read(connection, query, opened, cancellationToken));
    }
}
