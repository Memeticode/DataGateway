using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace DataGateway.Web.Tests;

// Per-call decorators: every successful value is read from the real provider.
internal sealed class SqliteProbe
{
    internal readonly List<string> Events = [];
    internal readonly List<SqliteProbeCommand> Commands = [];
    internal readonly List<SqliteProbeConnection> Connections = [];
    internal readonly List<SqliteProbeReader> Readers = [];
    internal readonly List<(string Name, object? Value, DbType Type)> Parameters = [];
    internal int Reads, TrueReads, NextResults;
    internal int FailReadAt = -1;
    internal IOException? ReadFailure;
    internal Exception? ProviderFailure;
    internal CancellationToken OpenToken, ExecuteToken, ReadToken;
    internal string? GateResource;
    internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly Dictionary<string, Exception> CleanupErrors = [];
    internal SqliteConnection Create(string text)
    {
        var connection = new SqliteProbeConnection(text, this);
        Connections.Add(connection);
        return connection;
    }
    internal async Task BeforeCleanup(string resource)
    {
        if (GateResource != resource) return;
        Entered.TrySetResult();
        await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    internal void AfterCleanup(string resource)
    {
        Events.Add(resource + "-disposed");
        if (CleanupErrors.TryGetValue(resource, out var error)) throw error;
    }
}

internal sealed class SqliteProbeConnection(string text, SqliteProbe probe) : SqliteConnection(text)
{
    internal string Original => ConnectionString;
    internal int OpenedEventIndex { get; private set; } = -1;
    internal bool IsDisposed;
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        probe.OpenToken = cancellationToken;
        probe.Events.Add("open");
        await base.OpenAsync(cancellationToken);
        OpenedEventIndex = probe.Events.Count;
        probe.Events.Add("opened");
    }
    protected override DbCommand CreateDbCommand()
    {
        var commandEventIndex = probe.Events.Count;
        probe.Events.Add("command");
        var command = new SqliteProbeCommand(base.CreateDbCommand(), probe, this, commandEventIndex);
        probe.Commands.Add(command);
        return command;
    }
    public override async ValueTask DisposeAsync()
    {
        if (IsDisposed) throw new InvalidOperationException("Repeated outer connection disposal.");
        await probe.BeforeCleanup("connection");
        await base.DisposeAsync();
        IsDisposed = true;
        probe.AfterCleanup("connection");
    }
}

internal sealed class SqliteProbeCommand(DbCommand inner, SqliteProbe probe,
    SqliteProbeConnection owner, int commandEventIndex) : DbCommand
{
    internal SqliteProbeConnection Owner => owner;
    internal int CommandEventIndex => commandEventIndex;
    internal bool IsDisposed;
    [AllowNull] public override string CommandText { get => inner.CommandText; set => inner.CommandText = value!; }
    public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
    public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
    public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
    public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
    protected override DbConnection? DbConnection { get => inner.Connection; set => inner.Connection = value; }
    protected override DbTransaction? DbTransaction { get => inner.Transaction; set => inner.Transaction = value; }
    protected override DbParameterCollection DbParameterCollection => inner.Parameters;
    protected override DbParameter CreateDbParameter() => inner.CreateParameter();
    public override void Cancel() => inner.Cancel();
    public override int ExecuteNonQuery() => inner.ExecuteNonQuery();
    public override object? ExecuteScalar() => inner.ExecuteScalar();
    public override void Prepare() => inner.Prepare();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => inner.ExecuteReader(behavior);
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        probe.Events.Add("execute");
        probe.ExecuteToken = cancellationToken;
        foreach (DbParameter parameter in inner.Parameters)
            probe.Parameters.Add((parameter.ParameterName, parameter.Value, parameter.DbType));
        try
        {
            var reader = new SqliteProbeReader(await inner.ExecuteReaderAsync(behavior, cancellationToken), probe, inner.CommandText);
            probe.Events.Add("reader");
            probe.Readers.Add(reader);
            return reader;
        }
        catch (Exception error) { probe.ProviderFailure = error; throw; }
    }
    public override async ValueTask DisposeAsync()
    {
        if (IsDisposed) throw new InvalidOperationException("Repeated outer command disposal.");
        await probe.BeforeCleanup("command");
        await inner.DisposeAsync();
        IsDisposed = true;
        probe.AfterCleanup("command");
    }
}

internal sealed class SqliteProbeReader(DbDataReader inner, SqliteProbe probe, string sql) : DbDataReader
{
    internal string Sql => sql;
    internal int ReadAttempts, SuccessfulReads;
    internal bool IsDisposed;
    public override int FieldCount => inner.FieldCount;
    public override bool HasRows => inner.HasRows;
    public override bool IsClosed => inner.IsClosed;
    public override int Depth => inner.Depth;
    public override int RecordsAffected => inner.RecordsAffected;
    public override object this[int ordinal] => inner[ordinal];
    public override object this[string name] => inner[name];
    public override string GetName(int ordinal) { probe.Events.Add("schema"); return inner.GetName(ordinal); }
    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        probe.Reads++;
        ReadAttempts++;
        probe.ReadToken = cancellationToken;
        probe.Events.Add("read");
        cancellationToken.ThrowIfCancellationRequested();
        if (probe.Reads == probe.FailReadAt && probe.ReadFailure is { } fault) throw fault;
        try
        {
            var result = await inner.ReadAsync(cancellationToken);
            probe.Events.Add(result ? "row" : "eof");
            if (result) { probe.TrueReads++; SuccessfulReads++; }
            return result;
        }
        catch (Exception error) { probe.ProviderFailure = error; throw; }
    }
    public override bool Read() => inner.Read();
    public override bool NextResult() { probe.NextResults++; return inner.NextResult(); }
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) { probe.NextResults++; return inner.NextResultAsync(cancellationToken); }
    public override object GetValue(int ordinal) => inner.GetValue(ordinal);
    public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
    public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
    public override char GetChar(int ordinal) => inner.GetChar(ordinal);
    public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
    public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
    public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
    public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
    public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
    public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
    public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
    public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
    public override string GetString(int ordinal) => inner.GetString(ordinal);
    public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
    public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
    public override int GetOrdinal(string name) => inner.GetOrdinal(name);
    public override int GetValues(object[] values) => inner.GetValues(values);
    public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
    public override void Close() => inner.Close();
    public override async ValueTask DisposeAsync()
    {
        if (IsDisposed) throw new InvalidOperationException("Repeated outer reader disposal.");
        await probe.BeforeCleanup("reader");
        await inner.DisposeAsync();
        IsDisposed = true;
        probe.AfterCleanup("reader");
    }
}
