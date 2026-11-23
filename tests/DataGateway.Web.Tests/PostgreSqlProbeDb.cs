using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace DataGateway.Web.Tests;

/// <summary>One scenario for the fake ADO.NET provider below: it scripts what the fake connection, command and
/// reader do, and records every call they receive.</summary>
/// <remarks>
/// <para>Runs the production Dapper.Data/Dapper scope without a network or database, so lifetime tests can check
/// exactly which resources were opened, read and disposed, and with which tokens.</para>
/// <para>The scenario is found through an <see cref="AsyncLocal{T}"/>, because the engine creates the connection
/// itself; parallel tests each see their own scenario. Disposing restores the one that was current before.</para>
/// </remarks>
internal sealed class PostgreSqlProbe : IDisposable
{
    /// <summary>The scenario a new fake connection joins.</summary>
    internal static readonly AsyncLocal<PostgreSqlProbe?> Current = new();
    /// <summary>The scenario that was current when this one started.</summary>
    private readonly PostgreSqlProbe? previous;
    /// <summary>Every provider call in order, such as <c>open-start</c>, <c>execute</c>, <c>read</c> and
    /// <c>reader-dispose</c>.</summary>
    internal readonly List<string> Events = [];
    /// <summary>Every connection string assigned to a fake connection.</summary>
    internal readonly List<string> AssignedConnections = [];
    /// <summary>Every fake connection created in this scenario.</summary>
    internal readonly List<PostgreSqlProbeConnection> Connections = [];
    /// <summary>Every fake command created in this scenario.</summary>
    internal readonly List<PostgreSqlProbeCommand> Commands = [];
    /// <summary>The token passed to each read.</summary>
    internal readonly List<CancellationToken> ReadTokens = [];
    /// <summary>The result's column names; the default includes names that differ only in case.</summary>
    internal string[] Names = ["z", "a", "Z"];
    /// <summary>The result's rows, one array of cells per row; a null cell reads as a database null.</summary>
    internal object?[][] Values = [];
    /// <summary>When false, a request for the next result set fails the test.</summary>
    internal bool AllowNextResult = false;
    /// <summary>Counts of reads, reads that returned a row, disposals of each resource, and next-result
    /// requests.</summary>
    internal int Reads, SuccessfulReads, ReaderDisposals, CommandDisposals, ConnectionDisposals, NextResults;
    /// <summary>The read, counted from 1, that waits on <see cref="ReadGate"/>.</summary>
    internal int GateReadAt = 1;
    /// <summary>The error the fake open throws after it starts.</summary>
    internal Exception? OpenError;
    /// <summary>Gates that hold open, execute or a read until the test releases them, and signals that each was
    /// entered.</summary>
    internal TaskCompletionSource? OpenGate, OpenEntered, ExecuteGate, ExecuteEntered, ReadGate, ReadEntered;
    /// <summary>The tokens passed to open and execute.</summary>
    internal CancellationToken OpenToken, ExecuteToken;
    /// <summary>Starts the scenario and makes it current for this async flow.</summary>
    internal PostgreSqlProbe() { previous = Current.Value; Current.Value = this; }
    /// <summary>Ends the scenario and restores the previous one.</summary>
    public void Dispose() => Current.Value = previous;
    /// <summary>Throws <paramref name="error"/>, the scenario's scripted failure.</summary>
    /// <remarks>The helper gives the injected failure a named frame, so a test can find that name in the caught stack
    /// trace and prove the engine rethrew the original exception with its stack, not a copy or a wrapper. No test
    /// reads this copy's frame any more; the shared engine tests do, through
    /// <see cref="SourceProbe.ThrowAtProbe"/>.</remarks>
    internal static void ThrowAtProbe(Exception error) => throw error;
    /// <summary>A gate whose continuations run asynchronously, so releasing it never runs test code inline.</summary>
    internal static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>A fake connection that records its use in the current <see cref="PostgreSqlProbe"/> and allows only
/// asynchronous open.</summary>
internal sealed class PostgreSqlProbeConnection : DbConnection
{
    /// <summary>The scenario this connection joined; creating one outside a scenario fails.</summary>
    internal readonly PostgreSqlProbe Probe = PostgreSqlProbe.Current.Value ?? throw new InvalidOperationException("Missing isolated probe scenario.");
    /// <summary>The assigned connection string.</summary>
    private string connection = "";
    /// <summary>Open after a successful open, closed otherwise.</summary>
    private ConnectionState state;
    /// <summary>Whether open was attempted, and whether the connection was disposed.</summary>
    internal bool WasOpened, IsDisposed;
    /// <summary>Creates the connection and adds it to the scenario.</summary>
    public PostgreSqlProbeConnection() => Probe.Connections.Add(this);
    /// <summary>The connection string; each assignment is recorded.</summary>
    [AllowNull]
    public override string ConnectionString
    {
        get => connection;
        set { connection = value ?? ""; Probe.AssignedConnections.Add(connection); }
    }
    /// <summary>A fixed database name.</summary>
    public override string Database => "Synthetic";
    /// <summary>A fixed server name.</summary>
    public override string DataSource => "Synthetic";
    /// <summary>A fixed server version.</summary>
    public override string ServerVersion => "1";
    /// <summary>The connection's state.</summary>
    public override ConnectionState State => state;
    /// <summary>Not supported: the gateway never changes database.</summary>
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
    /// <summary>Fails: the engine must open asynchronously, so cancellation reaches the open.</summary>
    public override void Open() => throw new NotSupportedException("Only async open is allowed.");
    /// <summary>Records the open and its token, waits on the open gate if set, then fails with the scenario's error
    /// or opens.</summary>
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        WasOpened = true;
        Probe.OpenToken = cancellationToken;
        Probe.Events.Add("open-start");
        Probe.OpenEntered?.TrySetResult();
        if (Probe.OpenGate is not null) await Probe.OpenGate.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Probe.OpenError is not null) PostgreSqlProbe.ThrowAtProbe(Probe.OpenError);
        state = ConnectionState.Open;
        Probe.Events.Add("open-success");
    }
    /// <summary>Closes the connection.</summary>
    public override void Close() => state = ConnectionState.Closed;
    /// <summary>Not supported: a read-only query needs no transaction.</summary>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    /// <summary>Creates a recorded fake command.</summary>
    protected override DbCommand CreateDbCommand()
    {
        Probe.Events.Add("command");
        var command = new PostgreSqlProbeCommand(this);
        Probe.Commands.Add(command);
        return command;
    }
    /// <summary>Records the disposal of an opened connection; a second disposal fails the test.</summary>
    protected override void Dispose(bool disposing)
    {
        if (!disposing) { base.Dispose(false); return; }
        if (IsDisposed) throw new InvalidOperationException("Connection disposed twice.");
        IsDisposed = true;
        state = ConnectionState.Closed;
        if (WasOpened)
        {
            Probe.Events.Add("connection-dispose"); Probe.ConnectionDisposals++;
        }
        base.Dispose(disposing);
    }

}

/// <summary>A fake command that records its execution and allows only an asynchronous reader.</summary>
internal sealed class PostgreSqlProbeCommand(PostgreSqlProbeConnection connection) : DbCommand
{
    /// <summary>The bound parameters.</summary>
    private readonly ProbeParameters parameters = new();
    /// <summary>The behavior the reader was requested with.</summary>
    internal CommandBehavior Behavior { get; private set; }
    /// <summary>Whether the command was disposed.</summary>
    internal bool IsDisposed;
    /// <summary>The SQL text the engine set.</summary>
    [AllowNull] public override string CommandText { get; set; } = "";
    /// <summary>The command timeout the engine set, in seconds.</summary>
    public override int CommandTimeout { get; set; }
    /// <summary>The command type the engine set.</summary>
    public override CommandType CommandType { get; set; }
    /// <summary>Unused designer flag.</summary>
    public override bool DesignTimeVisible { get; set; }
    /// <summary>Unused update setting.</summary>
    public override UpdateRowSource UpdatedRowSource { get; set; }
    /// <summary>The fake connection that created this command.</summary>
    protected override DbConnection? DbConnection { get; set; } = connection;
    /// <summary>Unused: no transaction.</summary>
    protected override DbTransaction? DbTransaction { get; set; }
    /// <summary>The bound parameters.</summary>
    protected override DbParameterCollection DbParameterCollection => parameters;
    /// <summary>Does nothing; cancellation reaches the fake through tokens.</summary>
    public override void Cancel() { }
    /// <summary>Not supported: the gateway only reads.</summary>
    public override int ExecuteNonQuery() => throw new NotSupportedException();
    /// <summary>Not supported: the gateway reads rows through a reader.</summary>
    public override object? ExecuteScalar() => throw new NotSupportedException();
    /// <summary>Not supported: the engine does not prepare commands.</summary>
    public override void Prepare() => throw new NotSupportedException();
    /// <summary>Creates a fake parameter.</summary>
    protected override DbParameter CreateDbParameter() => new ProbeParameter();
    /// <summary>Fails: the engine must execute asynchronously, so cancellation reaches the command.</summary>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    /// <summary>Records the execution and its token, waits on the execute gate if set, then returns a fake reader
    /// over the scenario's rows.</summary>
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var probe = connection.Probe;
        Behavior = behavior;
        probe.Events.Add("execute");
        probe.ExecuteToken = cancellationToken;
        probe.ExecuteEntered?.TrySetResult();
        if (probe.ExecuteGate is not null) await probe.ExecuteGate.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new PostgreSqlProbeReader(probe);
    }
    /// <summary>Records the disposal; a second disposal fails the test.</summary>
    protected override void Dispose(bool disposing)
    {
        if (!disposing) { base.Dispose(false); return; }
        if (IsDisposed) throw new InvalidOperationException("Command disposed twice.");
        IsDisposed = true;
        connection.Probe.Events.Add("command-dispose");
        connection.Probe.CommandDisposals++;
        base.Dispose(disposing);
    }
    /// <summary>Disposes synchronously, returning a failed disposal as a faulted task.</summary>
    public override ValueTask DisposeAsync()
    {
        try
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
        catch (Exception error) { return ValueTask.FromException(error); }
    }

}

/// <summary>A fake reader over the scenario's rows that allows only asynchronous reads and records each one.</summary>
internal sealed class PostgreSqlProbeReader(PostgreSqlProbe probe) : DbDataReader
{
    /// <summary>The current row; -1 before the first read.</summary>
    private int index = -1;
    /// <summary>Whether the reader was closed or disposed.</summary>
    private bool closed;
    /// <summary>The number of columns.</summary>
    public override int FieldCount => probe.Names.Length;
    /// <summary>Whether the scenario has any rows.</summary>
    public override bool HasRows => probe.Values.Length > 0;
    /// <summary>Whether the reader was closed.</summary>
    public override bool IsClosed => closed;
    /// <summary>Always 0: no nesting.</summary>
    public override int Depth => 0;
    /// <summary>Always -1: a query changes nothing.</summary>
    public override int RecordsAffected => -1;
    /// <summary>The current row's cell at <paramref name="ordinal"/>.</summary>
    public override object this[int ordinal] => GetValue(ordinal);
    /// <summary>The current row's cell named <paramref name="name"/>.</summary>
    public override object this[string name] => GetValue(GetOrdinal(name));
    /// <summary>The column's name; each call is recorded as a schema read.</summary>
    public override string GetName(int ordinal)
    {
        probe.Events.Add("schema");
        return probe.Names[ordinal];
    }
    /// <summary>Records the read and its token, waits on the read gate at the chosen read, then moves to the next
    /// row; a read after close fails the test.</summary>
    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if (closed) throw new InvalidOperationException("Read after disposal.");
        probe.Events.Add("read"); probe.ReadTokens.Add(cancellationToken);
        probe.Reads++;
        probe.ReadEntered?.TrySetResult();
        if (probe.ReadGate is not null && probe.Reads == probe.GateReadAt) await probe.ReadGate.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (++index >= probe.Values.Length) return false;
        probe.SuccessfulReads++;
        return true;
    }
    /// <summary>Fails: the engine must read asynchronously, so cancellation reaches each read.</summary>
    public override bool Read() => throw new NotSupportedException("Only async reads are allowed.");
    /// <summary>Fails the test unless <see cref="PostgreSqlProbe.AllowNextResult"/> is set, so a test sees any
    /// attempt to move past the first result set.</summary>
    public override bool NextResult()
    {
        probe.NextResults++;
        if (probe.AllowNextResult) return false;
        throw new InvalidOperationException("Must not drain results.");
    }
    /// <summary>The asynchronous form of <see cref="NextResult"/>.</summary>
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(NextResult());
    /// <summary>The current row's cell, with a null cell read as a database null.</summary>
    public override object GetValue(int ordinal) => probe.Values[index][ordinal] ?? DBNull.Value;
    /// <summary>Copies the current row's cells into <paramref name="values"/>.</summary>
    public override int GetValues(object[] values) { for (var i = 0; i < Math.Min(values.Length, FieldCount); i++) values[i] = GetValue(i); return Math.Min(values.Length, FieldCount); }
    /// <summary>The position of the column named <paramref name="name"/>, matched exactly.</summary>
    public override int GetOrdinal(string name) => Array.IndexOf(probe.Names, name);
    /// <summary>Always <see cref="object"/>: the fake has no typed columns.</summary>
    public override Type GetFieldType(int ordinal) => typeof(object);
    /// <summary>Always <c>object</c>.</summary>
    public override string GetDataTypeName(int ordinal) => "object";
    /// <summary>Whether the cell is a database null.</summary>
    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;
    /// <summary>The cell, cast to its type.</summary>
    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    /// <summary>The cell, cast to its type.</summary>
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
    /// <summary>The cell, cast to its type.</summary>
    public override char GetChar(int ordinal) => (char)GetValue(ordinal);
    /// <summary>The cell, cast to its type.</summary>
    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
    /// <summary>The cell, cast to its type.</summary>
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    /// <summary>The cell, cast to its type.</summary>
    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
    /// <summary>The cell, cast to its type.</summary>
    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
    /// <summary>The cell, cast to its type.</summary>
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
    /// <summary>The cell, cast to its type.</summary>
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
    /// <summary>The cell, cast to its type.</summary>
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
    /// <summary>The cell, cast to its type.</summary>
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    /// <summary>The cell, cast to its type.</summary>
    public override string GetString(int ordinal) => (string)GetValue(ordinal);
    /// <summary>Not supported.</summary>
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    /// <summary>Not supported.</summary>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    /// <summary>Not supported.</summary>
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    /// <summary>Closes the reader.</summary>
    public override void Close() => closed = true;
    /// <summary>Records the disposal; disposing a closed reader fails the test.</summary>
    public override ValueTask DisposeAsync()
    {
        try
        {
            if (closed) throw new InvalidOperationException("Reader disposed twice.");
            closed = true; probe.ReaderDisposals++; probe.Events.Add("reader-dispose");
            return ValueTask.CompletedTask;
        }
        catch (Exception error) { return ValueTask.FromException(error); }
    }
}
