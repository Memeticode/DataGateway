using System.Data.Common;
using System.Runtime.ExceptionServices;
using Dapper.Data;
using DataGateway.Core.Queries;
using DataGateway.Data.Queries;
using SqlKata;
using SqlKata.Compilers;

namespace DataGateway.Data;

/// <summary>The bounded path of an engine derived from <see cref="DatabaseEngine{TConnection}"/>: compile, and read
/// with the base's own row-keeping consumer.</summary>
/// <remarks>Internal, and implemented explicitly by the base: an engine in another assembly can neither implement it
/// nor replace it, so <see cref="Queries.QueryRunner"/> can rely on the base's bounded read whatever a derived engine
/// adds.</remarks>
internal interface ICappedQueryEngine
{
    /// <summary>Compiles with the base's compilation.</summary>
    /// <param name="query">The validated query.</param>
    /// <returns>The compiled query.</returns>
    CompiledQuery Compile(ValidatedQuery query);
    /// <summary>Reads the query's rows, keeping at most <see cref="CompiledQuery.Keep"/>.</summary>
    /// <param name="connection">The source connection string.</param>
    /// <param name="query">The compiled query.</param>
    /// <param name="commandTimeoutSeconds">The driver's per-command timeout, in seconds.</param>
    /// <param name="opened">Called once the connection is open.</param>
    /// <param name="expired">Asked once the rows are kept: true when the query's deadline has passed.</param>
    /// <param name="cancellationToken">Cancels the open, the command and every read.</param>
    /// <returns>The kept rows.</returns>
    Task<QueryRows> ReadQueryAsync(string connection, CompiledQuery query,
        int commandTimeoutSeconds, Action opened, Func<bool> expired, CancellationToken cancellationToken);
}

/// <summary>Compiles and streams source queries within one factory-created connection per call.</summary>
/// <remarks>
/// <para>The shared engine: everything that is the same on every database lives here, and a derived engine supplies
/// only its dialect through the protected hooks (compiler, matched-row count, LIKE escaping, parameter binding and
/// cell conversion) plus connection checks and failure recognition.</para>
/// <para>Each read creates its own connection from the factory and closes it before returning, so an engine holds no
/// connection between calls.</para>
/// </remarks>
/// <typeparam name="TConnection">The driver's connection type.</typeparam>
public abstract class DatabaseEngine<TConnection> : ICappedQueryEngine where TConnection : DbConnection, new()
{
    /// <summary>Creates a fresh, closed connection from a source connection string.</summary>
    private readonly Func<string, TConnection> createConnection;

    /// <summary>Takes the factory each call uses for its connection.</summary>
    /// <param name="createConnection">Returns a fresh, closed connection for a connection string; the engine owns and
    /// disposes it.</param>
    protected DatabaseEngine(Func<string, TConnection> createConnection)
    {
        ArgumentNullException.ThrowIfNull(createConnection);
        this.createConnection = createConnection;
    }

    /// <summary>A new SqlKata compiler for this engine's dialect.</summary>
    /// <returns>The compiler; a new one per call.</returns>
    protected abstract Compiler CreateCompiler();
    /// <summary>The raw select expression that counts matched rows into
    /// <see cref="CompiledQuery.MatchedRowCountColumn"/>, as a nonnegative 64-bit integer.</summary>
    protected abstract string MatchedRowCountExpression { get; }
    /// <summary>Escapes a caller's LIKE pattern so <c>%</c> stays the only wildcard on this database.</summary>
    /// <remarks>The public vocabulary promises that <c>_</c>, <c>[</c> and <c>]</c> match themselves; each database
    /// gives them different meanings, so each engine escapes its own.</remarks>
    /// <param name="pattern">The caller's pattern.</param>
    /// <returns>The escaped pattern, and the escape character to declare; empty for none.</returns>
    protected abstract (string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern);
    /// <summary>Sets a parameter's value, and its type where this database needs one.</summary>
    /// <param name="parameter">The new parameter, already named.</param>
    /// <param name="value">The bound value; null for SQL null.</param>
    protected abstract void PrepareParameter(DbParameter parameter, object? value);
    /// <summary>Converts a non-null cell read from the source to the value the answer carries.</summary>
    /// <param name="value">The cell, never null or <see cref="DBNull"/>.</param>
    /// <param name="column">The output column the cell belongs to; null for a column the query did not declare,
    /// such as the matched-row count.</param>
    /// <returns>The converted value.</returns>
    protected abstract object ConvertCell(object value, OutputColumn? column);
    /// <inheritdoc cref="IDatabaseEngine.ValidateSourceConnection"/>
    public abstract void ValidateSourceConnection(string? connection);
    /// <inheritdoc cref="IDatabaseEngine.RecognizeFailure"/>
    public abstract ProviderFailure? RecognizeFailure(Exception error);

    /// <inheritdoc cref="ICappedQueryEngine.Compile"/>
    CompiledQuery ICappedQueryEngine.Compile(ValidatedQuery query) => Compile(query);

    /// <inheritdoc cref="ICappedQueryEngine.ReadQueryAsync"/>
    Task<QueryRows> ICappedQueryEngine.ReadQueryAsync(string connection, CompiledQuery query,
        int commandTimeoutSeconds, Action opened, Func<bool> expired, CancellationToken cancellationToken) =>
        ReadSourceAsync(connection, query, commandTimeoutSeconds, opened,
            rows => KeptRows.KeepAsync(rows, query, cancellationToken, expired), cancellationToken);

    /// <summary>Compiles a validated query into this engine's SQL.</summary>
    /// <remarks>
    /// <para>Works on a clone, so the validated query is never changed. The limit is always in the SQL, at keep plus
    /// one: the database stops at that many rows, rather than producing every row for the reader to stop at, and the
    /// extra row shows that more exist.</para>
    /// <para>An aggregate without <c>group_by</c> always yields one row, even over no matching rows, so the query
    /// also selects the matched-row count; the answer then tells "nothing matched" from a real result.</para>
    /// </remarks>
    /// <param name="query">The validated query.</param>
    /// <returns>The SQL, its named parameters and the row facts.</returns>
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

    /// <summary>Escapes every LIKE pattern in the query, nested groups included, with
    /// <see cref="PrepareLikePattern"/>.</summary>
    /// <remarks>Only <c>like</c> conditions without an escape character are prepared; a condition that already
    /// declares one is left as it is.</remarks>
    /// <param name="query">The query, or a nested group, to prepare in place.</param>
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

    /// <summary>Runs a compiled query on the source and hands its rows to one consumer, inside one connection
    /// scope.</summary>
    /// <remarks>
    /// <para>The rows the consumer sees are valid only while it runs: once it returns, the saved sequence expires and
    /// the reader, command and connection are disposed, in that order, before this task completes.</para>
    /// <para>Disposal is explicit rather than <c>await using</c>: every resource is attempted, and the first failure
    /// wins, so a failing dispose never hides the exception that stopped the query.</para>
    /// </remarks>
    /// <param name="connection">The source connection string, used exactly as given.</param>
    /// <param name="query">The compiled query.</param>
    /// <param name="commandTimeoutSeconds">The driver's per-command timeout, in seconds; at least 1.</param>
    /// <param name="opened">Called once the connection is open, before the command is created.</param>
    /// <param name="consume">The one consumer of the rows; it returns the kept rows.</param>
    /// <param name="cancellationToken">Cancels the open, the command and every read.</param>
    /// <returns>What <paramref name="consume"/> returned.</returns>
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

    /// <summary>Adds the compiled query's named parameters to the command.</summary>
    /// <remarks>CLR integers are bound as they are; every other value goes through <see cref="PrepareParameter"/>.</remarks>
    /// <param name="command">The command to bind.</param>
    /// <param name="query">The compiled query.</param>
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
    /// <summary>Disposes each acquired resource in order and keeps the first failure.</summary>
    /// <param name="failure">The failure so far; it takes precedence over any cleanup failure.</param>
    /// <param name="resources">The reader and command, either possibly null.</param>
    /// <returns>The first failure, or null when everything succeeded.</returns>
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

/// <summary>The rows of one open reader, as a sequence that can be enumerated once and only inside its connection
/// scope.</summary>
/// <remarks>
/// <para>The sequence is its own enumerator: there is one reader, so there can be one pass. Each row is a fresh,
/// ordered dictionary keyed by column name, so a kept row survives the reader.</para>
/// <para><see cref="Close"/> expires the sequence when the scope ends, so a consumer that saved it gets
/// <see cref="ObjectDisposedException"/> instead of reading a closed reader.</para>
/// </remarks>
internal sealed class SourceRows : IAsyncEnumerable<IDictionary<string, object?>>,
    IAsyncEnumerator<IDictionary<string, object?>>
{
    /// <summary>The open reader; null once closed.</summary>
    private DbDataReader? reader;
    /// <summary>The column names, by ordinal.</summary>
    private readonly string[] names;
    /// <summary>The declared output column for each ordinal; null for an undeclared column.</summary>
    private readonly OutputColumn?[] metadata;
    /// <summary>The engine's cell conversion.</summary>
    private readonly Func<object, OutputColumn?, object> convert;
    /// <summary>The token every read observes; linked with the enumerator's token when one is given.</summary>
    private CancellationToken token;
    /// <summary>The source that links the scope's token and the enumerator's; null when not needed.</summary>
    private CancellationTokenSource? linked;
    /// <summary>The current row; null before the first read and after the end.</summary>
    private IDictionary<string, object?>? current;
    /// <summary>Whether enumeration has started, whether the scope has ended, and whether the reader is
    /// done.</summary>
    private bool leased, closed, ended;

    /// <summary>Wraps an open reader and matches its columns to the declared output columns.</summary>
    /// <remarks>Duplicate names, declared or read, throw <see cref="GatewayInvariantException"/>: rows are keyed by
    /// name, so a duplicate name cannot be represented.</remarks>
    /// <param name="reader">The open reader.</param>
    /// <param name="columns">The compiled query's output columns.</param>
    /// <param name="token">The scope's cancellation token.</param>
    /// <param name="convert">The engine's cell conversion.</param>
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

    /// <summary>Starts the one pass over the rows.</summary>
    /// <param name="cancellationToken">An extra token to observe with the scope's own.</param>
    /// <returns>This instance.</returns>
    /// <exception cref="InvalidOperationException">The rows were already enumerated.</exception>
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

    /// <summary>The current row.</summary>
    public IDictionary<string, object?> Current
    {
        get
        {
            ObjectDisposedException.ThrowIf(closed, this);
            return current ?? throw new InvalidOperationException("Source rows are not positioned on a row.");
        }
    }

    /// <summary>Reads the next row, with database nulls as null and other cells converted by the engine.</summary>
    /// <returns><see langword="true"/> when a row was read.</returns>
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

    /// <summary>Ends the pass; the reader itself belongs to the engine's scope and is disposed there.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        ended = true;
        current = null;
        linked?.Dispose();
        linked = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>Expires the sequence when the connection scope ends.</summary>
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
