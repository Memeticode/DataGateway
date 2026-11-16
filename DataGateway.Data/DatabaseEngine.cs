using System.Data.Common;
using System.Runtime.ExceptionServices;
using Dapper.Data;
using DataGateway.Core.Queries;
using DataGateway.Data.Queries;
using SqlKata;
using SqlKata.Compilers;

namespace DataGateway.Data;

internal interface ICappedQueryEngine
{
    CompiledQuery Compile(ValidatedQuery query);
    Task<QueryRows> ReadQueryAsync(string connection, CompiledQuery query,
        int commandTimeoutSeconds, Action opened, Func<bool> expired, CancellationToken cancellationToken);
}

/// <summary>Compiles and streams source queries within one factory-created connection per call.</summary>
public abstract class DatabaseEngine<TConnection> : ICappedQueryEngine where TConnection : DbConnection, new()
{
    private readonly Func<string, TConnection> createConnection;

    protected DatabaseEngine(Func<string, TConnection> createConnection)
    {
        ArgumentNullException.ThrowIfNull(createConnection);
        this.createConnection = createConnection;
    }

    protected abstract Compiler CreateCompiler();
    protected abstract string MatchedRowCountExpression { get; }
    protected abstract (string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern);
    protected abstract void PrepareParameter(DbParameter parameter, object? value);
    protected abstract object ConvertCell(object value, OutputColumn? column);
    public abstract void ValidateSourceConnection(string? connection);
    public abstract ProviderFailure? RecognizeFailure(Exception error);

    CompiledQuery ICappedQueryEngine.Compile(ValidatedQuery query) => Compile(query);

    Task<QueryRows> ICappedQueryEngine.ReadQueryAsync(string connection, CompiledQuery query,
        int commandTimeoutSeconds, Action opened, Func<bool> expired, CancellationToken cancellationToken) =>
        ReadSourceAsync(connection, query, commandTimeoutSeconds, opened,
            rows => KeptRows.KeepAsync(rows, query, cancellationToken, expired), cancellationToken);

    public CompiledQuery Compile(ValidatedQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var runnable = query.CloneQuery().Limit(query.RowLimit.Fetch);
        var matched = !runnable.HasComponent("group") &&
            runnable.GetComponents<AbstractColumn>("select").Any(column => column is AggregatedColumn);
        if (matched) runnable.SelectRaw(MatchedRowCountExpression);
        PrepareLike(runnable);
        var result = CreateCompiler().Compile(runnable);
        return new CompiledQuery(result.Sql, result.NamedBindings,
            query.RowLimit.Keep, matched, query.Columns);
    }

    private void PrepareLike(global::SqlKata.Query query)
    {
        foreach (var condition in query.GetComponents<AbstractCondition>("where"))
        {
            if (condition is BasicStringCondition
                { Operator: "like", EscapeCharacter: null, Value: string pattern } like)
            {
                var prepared = PrepareLikePattern(pattern);
                like.Value = prepared.Pattern;
                like.EscapeCharacter = prepared.EscapeCharacter;
            }
            else if (condition is NestedCondition<global::SqlKata.Query> group) PrepareLike(group.Query);
        }
    }

    public async Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query,
        int commandTimeoutSeconds, Action opened,
        Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(opened);
        ArgumentNullException.ThrowIfNull(consume);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Sql);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commandTimeoutSeconds);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSourceConnection(connection);
        var context = DbContext<TConnection>.FromFactory(() => createConnection(connection));
        return await context.BatchAsync(async session =>
        {
            DbCommand? command = null;
            DbDataReader? reader = null;
            SourceRows? rows = null;
            ExceptionDispatchInfo? failure = null;
            QueryRows? result = null;
            try
            {
                opened();
                command = session.Connection.CreateCommand();
                command.CommandText = query.Sql;
                command.CommandTimeout = commandTimeoutSeconds;
                BindParameters(command, query);
                reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                rows = new SourceRows(reader, query.Columns, cancellationToken, ConvertCell);
                result = await consume(rows).ConfigureAwait(false);
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            rows?.Close();
            failure = await Cleanup(failure, reader, command).ConfigureAwait(false);
            failure?.Throw();
            return result!;
        }, cancellationToken).ConfigureAwait(false);
    }

    private void BindParameters(DbCommand command, CompiledQuery query)
    {
        foreach (var (name, value) in query.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            if (value is int) parameter.Value = value;
            else PrepareParameter(parameter, value);
            command.Parameters.Add(parameter);
        }
    }

    // Attempt every acquired resource; the first failure (including a primary failure) wins.
    private static async Task<ExceptionDispatchInfo?> Cleanup(ExceptionDispatchInfo? failure,
        params IAsyncDisposable?[] resources)
    {
        foreach (var resource in resources)
        {
            if (resource is null) continue;
            try { await resource.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        }
        return failure;
    }
}

internal sealed class SourceRows : IAsyncEnumerable<IDictionary<string, object?>>,
    IAsyncEnumerator<IDictionary<string, object?>>
{
    private DbDataReader? reader;
    private readonly string[] names;
    private readonly OutputColumn?[] metadata;
    private readonly Func<object, OutputColumn?, object> convert;
    private CancellationToken token;
    private CancellationTokenSource? linked;
    private IDictionary<string, object?>? current;
    private bool leased, closed, ended;

    internal SourceRows(DbDataReader reader, IReadOnlyList<OutputColumn> columns, CancellationToken token,
        Func<object, OutputColumn?, object> convert)
    {
        this.convert = convert;
        this.reader = reader;
        this.token = token;
        names = new string[reader.FieldCount];
        metadata = new OutputColumn?[names.Length];
        if (columns.Select(column => column.Name).Distinct(StringComparer.Ordinal).Count() != columns.Count)
            throw new GatewayInvariantException("The source row has duplicate column names.");
        var declared = columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < names.Length; ordinal++)
        {
            var name = reader.GetName(ordinal);
            if (!seen.Add(name)) throw new GatewayInvariantException("The source row has duplicate column names.");
            names[ordinal] = name;
            metadata[ordinal] = declared.GetValueOrDefault(name);
        }
    }

    public IAsyncEnumerator<IDictionary<string, object?>> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(closed, this);
        if (leased) throw new InvalidOperationException("Source rows can only be enumerated once.");
        leased = true;
        if (cancellationToken.CanBeCanceled && cancellationToken != token)
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            token = linked.Token;
        }
        return this;
    }

    public IDictionary<string, object?> Current
    {
        get
        {
            ObjectDisposedException.ThrowIf(closed, this);
            return current ?? throw new InvalidOperationException("Source rows are not positioned on a row.");
        }
    }

    public async ValueTask<bool> MoveNextAsync()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        if (ended) return false;
        current = null;
        token.ThrowIfCancellationRequested();
        if (!await reader!.ReadAsync(token).ConfigureAwait(false)) { ended = true; return false; }
        token.ThrowIfCancellationRequested();
        var values = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < names.Length; ordinal++)
        {
            object? value = reader.GetValue(ordinal);
            if (value is DBNull) value = null;
            else if (value is not null) value = convert(value, metadata[ordinal]);
            values.Add(names[ordinal], value);
        }
        current = values;
        return true;
    }

    public ValueTask DisposeAsync()
    {
        ended = true;
        current = null;
        linked?.Dispose();
        linked = null;
        return ValueTask.CompletedTask;
    }

    internal void Close()
    {
        closed = true;
        ended = true;
        current = null;
        reader = null;
        linked?.Dispose();
        linked = null;
    }
}
