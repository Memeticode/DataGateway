using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace DataGateway.Web.Tests;

/// <summary>One scripted source-database scenario: it records what the engine does to the fake connection, command
/// and reader, and holds the failures and gates the test wants injected at each stage.</summary>
/// <remarks>
/// <para>It runs the production connection scope (the engine's <c>DbContext</c> session, command, reader and
/// cleanup) without a network or database, so the order of open, execute, read and disposal can be asserted exactly.</para>
/// <para>The scenario travels in an <see cref="AsyncLocal{T}"/>, not a constructor argument: the engine creates
/// <see cref="ProbeConnection"/> itself through its factory, so the connection finds its scenario through
/// <see cref="Current"/>. Creating a probe makes it current; disposing it restores the previous one.</para>
/// </remarks>
internal sealed class SourceProbe : IDisposable
{
    /// <summary>The scenario of the current async flow, read by every probe connection when it is created.</summary>
    internal static readonly AsyncLocal<SourceProbe?> Current = new();
    /// <summary>The scenario that was current before this one, restored on <see cref="Dispose"/>.</summary>
    private readonly SourceProbe? previous;
    /// <summary>The ordered trail of driver calls (open-start, open-success, command, execute, schema, read and the
    /// disposals), which tests compare to the order they expect.</summary>
    internal readonly List<string> Events = [];
    /// <summary>Every connection string the engine assigned, so a test can show it reached the driver unchanged.</summary>
    internal readonly List<string> AssignedConnections = [];
    /// <summary>Every connection created in this scenario; an empty list proves no connection was constructed.</summary>
    internal readonly List<ProbeConnection> Connections = [];
    /// <summary>Every command the engine created.</summary>
    internal readonly List<ProbeCommand> Commands = [];
    /// <summary>The token passed to each read, in order.</summary>
    internal readonly List<CancellationToken> ReadTokens = [];
    /// <summary>The result's column names.</summary>
    /// <remarks>The default mixes case and order on purpose, so a row dictionary that sorts keys or compares them
    /// without case is caught.</remarks>
    internal string[] Names = ["z", "a", "Z"];
    /// <summary>The result's rows, one array of cells per row; a null cell is read as <see cref="DBNull"/>.</summary>
    internal object?[][] Values = [];
    /// <summary>An optional check run on the command just before the reader is returned.</summary>
    internal Action<ProbeCommand>? BeforeExecute;
    /// <summary>Whether asking for the next result set is allowed; when false, the reader throws, because the engine
    /// must read one result set only.</summary>
    internal bool AllowNextResult;
    /// <summary>Counts of read attempts, reads that returned a row, disposals of each resource and next-result calls.</summary>
    internal int Reads, SuccessfulReads, ReaderDisposals, CommandDisposals, ConnectionDisposals, NextResults;
    /// <summary>The 1-based read attempt that throws <see cref="ReadError"/>; -1 means none.</summary>
    internal int FailReadAt = -1;
    /// <summary>The 1-based read attempt that waits on <see cref="ReadGate"/>.</summary>
    internal int GateReadAt = 1;
    /// <summary>The failure to throw at each stage, or null for none.</summary>
    internal Exception? OpenError, ExecuteError, SchemaError, ReadError, ReaderCleanupError, CommandCleanupError, ConnectionCleanupError;
    /// <summary>Gates that hold a stage until the test releases it, and signals that a stage has been entered, so a test
    /// can observe or cancel the engine mid-call.</summary>
    internal TaskCompletionSource? OpenGate, OpenEntered, ExecuteGate, ExecuteEntered, ReadGate, ReadEntered, CleanupGate, CleanupEntered;
    /// <summary>The tokens the engine passed to open and execute.</summary>
    internal CancellationToken OpenToken, ExecuteToken;
    /// <summary>Creates a scenario and makes it current for this async flow.</summary>
    internal SourceProbe() { previous = Current.Value; Current.Value = this; }
    /// <summary>Restores the scenario that was current before this one.</summary>
    public void Dispose() => Current.Value = previous;
    /// <summary>Throws <paramref name="error"/> from a named frame.</summary>
    /// <remarks>Tests look for this method's name in the caught stack trace, which proves the engine rethrew the original
    /// exception with its stack, rather than a copy or a wrapper.</remarks>
    /// <param name="error">The exception to throw.</param>
    internal static void ThrowAtProbe(Exception error) => throw error;
    /// <summary>Creates a gate or signal.</summary>
    /// <remarks>Continuations run asynchronously so that releasing a gate never runs the engine's next step inline on the
    /// test's own thread.</remarks>
    internal static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>A fake connection that records its use in the current <see cref="SourceProbe"/> and fails where the
/// scenario says.</summary>
/// <remarks>Only the async open is supported, and disposing twice throws, so a sync call or a double cleanup in the
/// engine fails the test instead of passing unseen.</remarks>
internal sealed class ProbeConnection : DbConnection
{
    /// <summary>The scenario this connection reports to; creating a connection outside a scenario throws.</summary>
    internal readonly SourceProbe Probe = SourceProbe.Current.Value ?? throw new InvalidOperationException("Missing isolated probe scenario.");
    /// <summary>The connection string last assigned.</summary>
    private string connection = "";
    /// <summary>The connection's state, open only after a successful async open.</summary>
    private ConnectionState state;
    /// <summary>Whether an open was attempted, and whether the connection has been disposed.</summary>
    internal bool WasOpened, IsDisposed;
    /// <summary>Creates a connection and registers it with the current scenario.</summary>
    public ProbeConnection() => Probe.Connections.Add(this);
    /// <summary>The connection string; each assignment is recorded in <see cref="SourceProbe.AssignedConnections"/>.</summary>
    [AllowNull]
    public override string ConnectionString
    {
        get => connection;
        set { connection = value ?? ""; Probe.AssignedConnections.Add(connection); }
    }
    /// <summary>A fixed synthetic database name.</summary>
    public override string Database => "Synthetic";
    /// <summary>A fixed synthetic server name.</summary>
    public override string DataSource => "Synthetic";
    /// <summary>A fixed server version.</summary>
    public override string ServerVersion => "1";
    /// <summary>The current state.</summary>
    public override ConnectionState State => state;
    /// <summary>Not supported: the engine never changes database.</summary>
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
    /// <summary>Refuses a synchronous open, which would block a request thread.</summary>
    public override void Open() => throw new NotSupportedException("Only async open is allowed.");
    /// <summary>Opens the connection, honouring the scenario's gate, cancellation and open failure.</summary>
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        WasOpened = true;
        Probe.OpenToken = cancellationToken;
        Probe.Events.Add("open-start");
        Probe.OpenEntered?.TrySetResult();
        if (Probe.OpenGate is not null) await Probe.OpenGate.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Probe.OpenError is not null) SourceProbe.ThrowAtProbe(Probe.OpenError);
        state = ConnectionState.Open;
        Probe.Events.Add("open-success");
    }
    /// <summary>Closes the connection.</summary>
    public override void Close() => state = ConnectionState.Closed;
    /// <summary>Not supported: source queries run without a transaction.</summary>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    /// <summary>Creates a <see cref="ProbeCommand"/> and records it.</summary>
    protected override DbCommand CreateDbCommand()
    {
        Probe.Events.Add("command");
        var command = new ProbeCommand(this);
        Probe.Commands.Add(command);
        return command;
    }
    /// <summary>Disposes the connection once, recording the disposal and throwing the scenario's cleanup failure if the
    /// connection was opened.</summary>
    protected override void Dispose(bool disposing)
    {
        if (!disposing) { base.Dispose(false); return; }
        if (IsDisposed) throw new InvalidOperationException("Connection disposed twice.");
        IsDisposed = true;
        state = ConnectionState.Closed;
        if (WasOpened)
        {
            Probe.Events.Add("connection-dispose"); Probe.ConnectionDisposals++;
            if (Probe.ConnectionCleanupError is not null) SourceProbe.ThrowAtProbe(Probe.ConnectionCleanupError);
        }
        base.Dispose(disposing);
    }
    /// <summary>Disposes asynchronously, waiting on the scenario's cleanup gate first so a test can see the call still
    /// pending during connection cleanup.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (WasOpened && Probe.CleanupGate is not null)
        {
            Probe.CleanupEntered?.TrySetResult();
            await Probe.CleanupGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Dispose();
    }
}

/// <summary>A fake command that records its text, timeout, parameters and behaviour, and returns a
/// <see cref="ProbeReader"/> over the scenario's rows.</summary>
/// <remarks>Only the async reader is supported and disposing twice throws, for the same reason as
/// <see cref="ProbeConnection"/>.</remarks>
/// <param name="connection">The connection that created the command.</param>
internal sealed class ProbeCommand(ProbeConnection connection) : DbCommand
{
    /// <summary>The command's parameters.</summary>
    private readonly ProbeParameters parameters = new();
    /// <summary>The behaviour the engine asked for when it executed the reader.</summary>
    internal CommandBehavior Behavior { get; private set; }
    /// <summary>Whether the command has been disposed.</summary>
    internal bool IsDisposed;
    /// <summary>The SQL text the engine set.</summary>
    [AllowNull] public override string CommandText { get; set; } = "";
    /// <summary>The command timeout in seconds the engine set.</summary>
    public override int CommandTimeout { get; set; }
    /// <summary>The command type.</summary>
    public override CommandType CommandType { get; set; }
    /// <summary>Unused designer flag.</summary>
    public override bool DesignTimeVisible { get; set; }
    /// <summary>Unused update setting.</summary>
    public override UpdateRowSource UpdatedRowSource { get; set; }
    /// <summary>The owning connection.</summary>
    protected override DbConnection? DbConnection { get; set; } = connection;
    /// <summary>Always null: no transaction is used.</summary>
    protected override DbTransaction? DbTransaction { get; set; }
    /// <summary>The parameter collection.</summary>
    protected override DbParameterCollection DbParameterCollection => parameters;
    /// <summary>Does nothing; cancellation reaches the probe through the tokens instead.</summary>
    public override void Cancel() { }
    /// <summary>Not supported: source queries only read.</summary>
    public override int ExecuteNonQuery() => throw new NotSupportedException();
    /// <summary>Not supported: the engine reads through a reader only.</summary>
    public override object? ExecuteScalar() => throw new NotSupportedException();
    /// <summary>Not supported: the engine never prepares commands.</summary>
    public override void Prepare() => throw new NotSupportedException();
    /// <summary>Creates a <see cref="ProbeParameter"/>.</summary>
    protected override DbParameter CreateDbParameter() => new ProbeParameter();
    /// <summary>Refuses a synchronous execute, which would block a request thread.</summary>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    /// <summary>Executes the command, honouring the scenario's gate, cancellation and execute failure, and returns a
    /// reader over the scenario's rows.</summary>
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var probe = connection.Probe;
        Behavior = behavior;
        probe.Events.Add("execute");
        probe.ExecuteToken = cancellationToken;
        probe.ExecuteEntered?.TrySetResult();
        if (probe.ExecuteGate is not null) await probe.ExecuteGate.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (probe.ExecuteError is not null) SourceProbe.ThrowAtProbe(probe.ExecuteError);
        probe.BeforeExecute?.Invoke(this);
        return new ProbeReader(probe);
    }
    /// <summary>Disposes the command once, recording the disposal and throwing the scenario's command cleanup failure.</summary>
    protected override void Dispose(bool disposing)
    {
        if (!disposing) { base.Dispose(false); return; }
        if (IsDisposed) throw new InvalidOperationException("Command disposed twice.");
        IsDisposed = true;
        connection.Probe.Events.Add("command-dispose");
        connection.Probe.CommandDisposals++;
        if (connection.Probe.CommandCleanupError is not null) SourceProbe.ThrowAtProbe(connection.Probe.CommandCleanupError);
        base.Dispose(disposing);
    }
}

/// <summary>A plain parameter that keeps whatever the engine sets, so tests can read back the value and type.</summary>
internal sealed class ProbeParameter : DbParameter
{
    /// <summary>The database type the engine chose.</summary>
    public override DbType DbType { get; set; }
    /// <summary>The direction.</summary>
    public override ParameterDirection Direction { get; set; }
    /// <summary>Whether the value may be null.</summary>
    public override bool IsNullable { get; set; }
    /// <summary>The parameter's name, such as <c>@p0</c>.</summary>
    [AllowNull] public override string ParameterName { get; set; } = "";
    /// <summary>Unused source column.</summary>
    [AllowNull] public override string SourceColumn { get; set; } = "";
    /// <summary>The bound value.</summary>
    public override object? Value { get; set; }
    /// <summary>Unused null mapping flag.</summary>
    public override bool SourceColumnNullMapping { get; set; }
    /// <summary>The size.</summary>
    public override int Size { get; set; }
    /// <summary>Resets the type to <see cref="DbType.Object"/>.</summary>
    public override void ResetDbType() => DbType = DbType.Object;
}

/// <summary>A list-backed parameter collection that keeps the order the engine added parameters in.</summary>
internal sealed class ProbeParameters : DbParameterCollection
{
    /// <summary>The parameters, in insertion order.</summary>
    private readonly List<DbParameter> values = [];
    /// <summary>The number of parameters.</summary>
    public override int Count => values.Count;
    /// <summary>The collection itself.</summary>
    public override object SyncRoot => this;
    /// <summary>Adds a parameter and returns its index.</summary>
    public override int Add(object value) { values.Add((DbParameter)value); return values.Count - 1; }
    /// <summary>Adds each parameter in order.</summary>
    public override void AddRange(Array items) { foreach (var item in items) Add(item!); }
    /// <summary>Removes every parameter.</summary>
    public override void Clear() => values.Clear();
    /// <summary>Whether the parameter is in the collection.</summary>
    public override bool Contains(object value) => values.Contains((DbParameter)value);
    /// <summary>Whether a parameter with this name is in the collection.</summary>
    public override bool Contains(string value) => IndexOf(value) >= 0;
    /// <summary>Copies the parameters into an array.</summary>
    public override void CopyTo(Array array, int index) => ((ICollection)values).CopyTo(array, index);
    /// <summary>Enumerates the parameters in order.</summary>
    public override IEnumerator GetEnumerator() => values.GetEnumerator();
    /// <summary>The index of the parameter, or -1.</summary>
    public override int IndexOf(object value) => values.IndexOf((DbParameter)value);
    /// <summary>The index of the parameter with this name, or -1.</summary>
    public override int IndexOf(string parameterName) => values.FindIndex(p => p.ParameterName == parameterName);
    /// <summary>Inserts a parameter at an index.</summary>
    public override void Insert(int index, object value) => values.Insert(index, (DbParameter)value);
    /// <summary>Removes the parameter.</summary>
    public override void Remove(object value) => values.Remove((DbParameter)value);
    /// <summary>Removes the parameter at an index.</summary>
    public override void RemoveAt(int index) => values.RemoveAt(index);
    /// <summary>Removes the parameter with this name.</summary>
    public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));
    /// <summary>The parameter at an index.</summary>
    protected override DbParameter GetParameter(int index) => values[index];
    /// <summary>The parameter with this name.</summary>
    protected override DbParameter GetParameter(string parameterName) => values[IndexOf(parameterName)];
    /// <summary>Replaces the parameter at an index.</summary>
    protected override void SetParameter(int index, DbParameter value) => values[index] = value;
    /// <summary>Replaces the parameter with this name.</summary>
    protected override void SetParameter(string parameterName, DbParameter value) => SetParameter(IndexOf(parameterName), value);
}

/// <summary>A fake reader over the scenario's <see cref="SourceProbe.Names"/> and <see cref="SourceProbe.Values"/>
/// that records schema reads, reads and disposal, and fails where the scenario says.</summary>
/// <remarks>Only async reads are supported, asking for a second result set throws unless allowed, and disposing twice
/// throws, so the engine's reading discipline is checked by every test that uses it.</remarks>
/// <param name="probe">The scenario to read from and report to.</param>
internal sealed class ProbeReader(SourceProbe probe) : DbDataReader
{
    /// <summary>The current row's index; -1 before the first read.</summary>
    private int index = -1;
    /// <summary>Whether the reader has been closed or disposed.</summary>
    private bool closed;
    /// <summary>The number of columns.</summary>
    public override int FieldCount => probe.Names.Length;
    /// <summary>Whether the scenario has any rows.</summary>
    public override bool HasRows => probe.Values.Length > 0;
    /// <summary>Whether the reader is closed.</summary>
    public override bool IsClosed => closed;
    /// <summary>Always 0: results are not nested.</summary>
    public override int Depth => 0;
    /// <summary>Always -1: a query changes no records.</summary>
    public override int RecordsAffected => -1;
    /// <summary>The current row's cell at an ordinal.</summary>
    public override object this[int ordinal] => GetValue(ordinal);
    /// <summary>The current row's cell for a column name.</summary>
    public override object this[string name] => GetValue(GetOrdinal(name));
    /// <summary>A column's name, recorded as a schema read and failing with the scenario's schema failure.</summary>
    public override string GetName(int ordinal)
    {
        probe.Events.Add("schema");
        if (probe.SchemaError is not null) SourceProbe.ThrowAtProbe(probe.SchemaError);
        return probe.Names[ordinal];
    }
    /// <summary>Moves to the next row, honouring the scenario's read gate, cancellation and read failure.</summary>
    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if (closed) throw new InvalidOperationException("Read after disposal.");
        probe.Events.Add("read"); probe.ReadTokens.Add(cancellationToken);
        probe.Reads++;
        probe.ReadEntered?.TrySetResult();
        if (probe.ReadGate is not null && probe.Reads == probe.GateReadAt) await probe.ReadGate.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (probe.ReadError is not null && probe.Reads == probe.FailReadAt) SourceProbe.ThrowAtProbe(probe.ReadError);
        if (++index >= probe.Values.Length) return false;
        probe.SuccessfulReads++;
        return true;
    }
    /// <summary>Refuses a synchronous read, which would block a request thread.</summary>
    public override bool Read() => throw new NotSupportedException("Only async reads are allowed.");
    /// <summary>Throws unless <see cref="SourceProbe.AllowNextResult"/> is set: the engine must not drain further
    /// result sets.</summary>
    public override bool NextResult()
    {
        probe.NextResults++;
        if (probe.AllowNextResult) return false;
        throw new InvalidOperationException("Must not drain results.");
    }
    /// <summary>The async form of <see cref="NextResult"/>.</summary>
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(NextResult());
    /// <summary>The current row's cell, with null read as <see cref="DBNull"/> as a real driver returns it.</summary>
    public override object GetValue(int ordinal) => probe.Values[index][ordinal] ?? DBNull.Value;
    /// <summary>Copies the current row's cells into <paramref name="values"/> and returns how many were copied.</summary>
    public override int GetValues(object[] values) { for (var i = 0; i < Math.Min(values.Length, FieldCount); i++) values[i] = GetValue(i); return Math.Min(values.Length, FieldCount); }
    /// <summary>The ordinal of a column name, matched exactly; -1 when absent.</summary>
    public override int GetOrdinal(string name) => Array.IndexOf(probe.Names, name);
    /// <summary>Always <see cref="object"/>: the cells keep their own runtime types.</summary>
    public override Type GetFieldType(int ordinal) => typeof(object);
    /// <summary>Always <c>object</c>.</summary>
    public override string GetDataTypeName(int ordinal) => "object";
    /// <summary>Whether the current row's cell is null.</summary>
    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;
    /// <summary>The current row's cell as a <see cref="bool"/>.</summary>
    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    /// <summary>The current row's cell as a <see cref="byte"/>.</summary>
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
    /// <summary>The current row's cell as a <see cref="char"/>.</summary>
    public override char GetChar(int ordinal) => (char)GetValue(ordinal);
    /// <summary>The current row's cell as a <see cref="short"/>.</summary>
    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
    /// <summary>The current row's cell as an <see cref="int"/>.</summary>
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    /// <summary>The current row's cell as a <see cref="long"/>.</summary>
    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
    /// <summary>The current row's cell as a <see cref="float"/>.</summary>
    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
    /// <summary>The current row's cell as a <see cref="double"/>.</summary>
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
    /// <summary>The current row's cell as a <see cref="decimal"/>.</summary>
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
    /// <summary>The current row's cell as a <see cref="DateTime"/>.</summary>
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
    /// <summary>The current row's cell as a <see cref="Guid"/>.</summary>
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    /// <summary>The current row's cell as a <see cref="string"/>.</summary>
    public override string GetString(int ordinal) => (string)GetValue(ordinal);
    /// <summary>Not supported: no test streams binary cells.</summary>
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    /// <summary>Not supported: no test streams text cells.</summary>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    /// <summary>Not supported: the engine reads rows through <see cref="ReadAsync"/>.</summary>
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    /// <summary>Closes the reader without counting a disposal.</summary>
    public override void Close() => closed = true;
    /// <summary>Disposes the reader once, recording the disposal and throwing the scenario's reader cleanup failure.</summary>
    public override ValueTask DisposeAsync()
    {
        if (closed) throw new InvalidOperationException("Reader disposed twice.");
        closed = true; probe.ReaderDisposals++; probe.Events.Add("reader-dispose");
        if (probe.ReaderCleanupError is not null) SourceProbe.ThrowAtProbe(probe.ReaderCleanupError);
        return ValueTask.CompletedTask;
    }
}
