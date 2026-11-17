using System.Runtime.CompilerServices;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Queries;

namespace DataGateway.Web.Tests;

internal static class FakeRows
{
    internal static IDatabaseEngine Engine(IDatabaseEngine engine,
        Func<string, CompiledQuery, Action, CancellationToken,
            IAsyncEnumerable<IDictionary<string, object?>>> read) => new RowsEngine(engine, read);

    internal static QueryRunner Runner(IDatabaseEngine engine,
        Func<string, CompiledQuery, Action, CancellationToken,
            IAsyncEnumerable<IDictionary<string, object?>>> read) =>
        new(new RowsEngine(engine, read));

    internal static Func<string, CompiledQuery, Action, CancellationToken,
        IAsyncEnumerable<IDictionary<string, object?>>> From(
        Func<string, CompiledQuery, Action, CancellationToken, Task<IReadOnlyList<dynamic>>> read) =>
        (connection, query, connected, token) => Iterate(read, connection, query, connected, token);

    private static async IAsyncEnumerable<IDictionary<string, object?>> Iterate(
        Func<string, CompiledQuery, Action, CancellationToken, Task<IReadOnlyList<dynamic>>> read,
        string connection, CompiledQuery query, Action connected,
        [EnumeratorCancellation] CancellationToken token)
    {
        var rows = await read(connection, query, connected, token);
        foreach (object row in rows) yield return (IDictionary<string, object?>)row;
    }

    internal static IDictionary<string, object?> Ordered(IDictionary<string, object?> values)
    {
        var row = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in values) row.Add(pair.Key, pair.Value);
        return row;
    }

    private sealed class RowsEngine(IDatabaseEngine engine,
        Func<string, CompiledQuery, Action, CancellationToken,
            IAsyncEnumerable<IDictionary<string, object?>>> read) : IDatabaseEngine
    {
        string IDatabaseEngine.ProviderName => engine.ProviderName;
        public CompiledQuery Compile(ValidatedQuery query) => engine.Compile(query);
        public void ValidateSourceConnection(string? connection) => engine.ValidateSourceConnection(connection);
        public ProviderFailure? RecognizeFailure(Exception error) => engine.RecognizeFailure(error);
        public Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query,
            int commandTimeoutSeconds, Action opened,
            Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume,
            CancellationToken cancellationToken) =>
            consume(read(connection, query, opened, cancellationToken));
    }
}
