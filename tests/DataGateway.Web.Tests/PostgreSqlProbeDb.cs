using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace DataGateway.Web.Tests;

// Runs the production Dapper.Data/Dapper scope without a network or database.
internal sealed class PostgreSqlProbe : IDisposable
{
    internal static readonly AsyncLocal<PostgreSqlProbe?> Current = new();
    private readonly PostgreSqlProbe? previous;
    internal readonly List<string> Events = [];
    internal readonly List<string> AssignedConnections = [];
    internal readonly List<PostgreSqlProbeConnection> Connections = [];
    internal readonly List<PostgreSqlProbeCommand> Commands = [];
    internal readonly List<CancellationToken> ReadTokens = [];
    internal string[] Names = ["z", "a", "Z"];
    internal object?[][] Values = [];
    internal bool AllowNextResult = false;
    internal int Reads, SuccessfulReads, ReaderDisposals, CommandDisposals, ConnectionDisposals, NextResults;
    internal int GateReadAt = 1;
    internal Exception? OpenError;
    internal TaskCompletionSource? OpenGate, OpenEntered, ExecuteGate, ExecuteEntered, ReadGate, ReadEntered;
    internal CancellationToken OpenToken, ExecuteToken;
    internal PostgreSqlProbe() { previous = Current.Value; Current.Value = this; }
    public void Dispose() => Current.Value = previous;
    internal static void ThrowAtProbe(Exception error) => throw error;
    internal static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class PostgreSqlProbeConnection : DbConnection
{
    internal readonly PostgreSqlProbe Probe = PostgreSqlProbe.Current.Value ?? throw new InvalidOperationException("Missing isolated probe scenario.");
    private string connection = "";
    private ConnectionState state;
    internal bool WasOpened, IsDisposed;
    public PostgreSqlProbeConnection() => Probe.Connections.Add(this);
    [AllowNull]
    public override string ConnectionString
    {
        get => connection;
        set { connection = value ?? ""; Probe.AssignedConnections.Add(connection); }
    }
    public override string Database => "Synthetic";
    public override string DataSource => "Synthetic";
    public override string ServerVersion => "1";
    public override ConnectionState State => state;
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
    public override void Open() => throw new NotSupportedException("Only async open is allowed.");
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
    public override void Close() => state = ConnectionState.Closed;
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand()
    {
        Probe.Events.Add("command");
        var command = new PostgreSqlProbeCommand(this);
        Probe.Commands.Add(command);
        return command;
    }
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

internal sealed class PostgreSqlProbeCommand(PostgreSqlProbeConnection connection) : DbCommand
{
    private readonly ProbeParameters parameters = new();
    internal CommandBehavior Behavior { get; private set; }
    internal bool IsDisposed;
    [AllowNull] public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; } = connection;
    protected override DbTransaction? DbTransaction { get; set; }
    protected override DbParameterCollection DbParameterCollection => parameters;
    public override void Cancel() { }
    public override int ExecuteNonQuery() => throw new NotSupportedException();
    public override object? ExecuteScalar() => throw new NotSupportedException();
    public override void Prepare() => throw new NotSupportedException();
    protected override DbParameter CreateDbParameter() => new ProbeParameter();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
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
    protected override void Dispose(bool disposing)
    {
        if (!disposing) { base.Dispose(false); return; }
        if (IsDisposed) throw new InvalidOperationException("Command disposed twice.");
        IsDisposed = true;
        connection.Probe.Events.Add("command-dispose");
        connection.Probe.CommandDisposals++;
        base.Dispose(disposing);
    }
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

internal sealed class PostgreSqlProbeReader(PostgreSqlProbe probe) : DbDataReader
{
    private int index = -1;
    private bool closed;
    public override int FieldCount => probe.Names.Length;
    public override bool HasRows => probe.Values.Length > 0;
    public override bool IsClosed => closed;
    public override int Depth => 0;
    public override int RecordsAffected => -1;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));
    public override string GetName(int ordinal)
    {
        probe.Events.Add("schema");
        return probe.Names[ordinal];
    }
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
    public override bool Read() => throw new NotSupportedException("Only async reads are allowed.");
    public override bool NextResult()
    {
        probe.NextResults++;
        if (probe.AllowNextResult) return false;
        throw new InvalidOperationException("Must not drain results.");
    }
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(NextResult());
    public override object GetValue(int ordinal) => probe.Values[index][ordinal] ?? DBNull.Value;
    public override int GetValues(object[] values) { for (var i = 0; i < Math.Min(values.Length, FieldCount); i++) values[i] = GetValue(i); return Math.Min(values.Length, FieldCount); }
    public override int GetOrdinal(string name) => Array.IndexOf(probe.Names, name);
    public override Type GetFieldType(int ordinal) => typeof(object);
    public override string GetDataTypeName(int ordinal) => "object";
    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;
    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
    public override char GetChar(int ordinal) => (char)GetValue(ordinal);
    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override string GetString(int ordinal) => (string)GetValue(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    public override void Close() => closed = true;
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
