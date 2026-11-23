using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace DataGateway.Web.Tests;

// Per-call decorators: every successful value is read from the real provider.
/// <summary>A recorder around real SQLite connections, commands and readers: it logs each call in order, can fail a
/// chosen read or cleanup, and can hold a cleanup open, so tests can see the engine's whole connection
/// lifecycle.</summary>
/// <remarks>
/// <para>Pass <see cref="Create"/> as the engine's or catalog's connection factory. Every value still comes from the
/// real provider, so the tests prove behaviour against SQLite itself, not against a fake.</para>
/// <para>EVENTS: the ordered trail in <see cref="Events"/>. <c>open</c>/<c>opened</c> around opening,
/// <c>command</c> on creating a command, <c>execute</c> then <c>reader</c> on executing, <c>read</c> then
/// <c>row</c> or <c>eof</c> per read, <c>schema</c> on reading a column name, and <c>reader-disposed</c>,
/// <c>command-disposed</c>, <c>connection-disposed</c> on cleanup. Tests compare positions in this trail to prove
/// order, such as opening before the caller's notification, or the reader closing before its command.</para>
/// </remarks>
internal sealed class SqliteProbe
{
    /// <summary>The ordered call trail; see the type's EVENTS note.</summary>
    internal readonly List<string> Events = [];
    /// <summary>Every command created, in order.</summary>
    internal readonly List<SqliteProbeCommand> Commands = [];
    /// <summary>Every connection created, in order.</summary>
    internal readonly List<SqliteProbeConnection> Connections = [];
    /// <summary>Every reader returned, in order.</summary>
    internal readonly List<SqliteProbeReader> Readers = [];
    /// <summary>Each bound parameter as the provider received it, with its database type, captured at
    /// execution.</summary>
    internal readonly List<(string Name, object? Value, DbType Type)> Parameters = [];
    /// <summary>Asynchronous read attempts, reads that returned a row, and next-result calls, across all
    /// readers.</summary>
    /// <remarks>The engine reads one result set only, so tests expect <see cref="NextResults"/> to stay 0.</remarks>
    internal int Reads, TrueReads, NextResults;
    /// <summary>The 1-based read attempt at which <see cref="ReadFailure"/> is thrown; -1 for never.</summary>
    internal int FailReadAt = -1;
    /// <summary>The fault thrown at read attempt <see cref="FailReadAt"/>.</summary>
    internal IOException? ReadFailure;
    /// <summary>The last exception the real provider threw on execute or read, so a test can check the engine passes
    /// on that same instance.</summary>
    internal Exception? ProviderFailure;
    /// <summary>The tokens the engine passed to open, execute and the last read.</summary>
    /// <remarks>Tests check they are the caller's token, so cancellation reaches the provider.</remarks>
    internal CancellationToken OpenToken, ExecuteToken, ReadToken;
    /// <summary>The resource (<c>reader</c>, <c>command</c> or <c>connection</c>) whose cleanup waits on
    /// <see cref="Release"/>; null for none.</summary>
    internal string? GateResource;
    /// <summary>Set when the gated cleanup (or a test's own gate) has been reached.</summary>
    internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Completed by the test to let the gated cleanup go on.</summary>
    internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Faults to throw after a resource has been disposed, keyed by <c>reader</c>, <c>command</c> or
    /// <c>connection</c>.</summary>
    internal readonly Dictionary<string, Exception> CleanupErrors = [];
    /// <summary>The connection factory: a recording connection over <paramref name="text"/>.</summary>
    internal SqliteConnection Create(string text)
    {
        var connection = new SqliteProbeConnection(text, this);
        Connections.Add(connection);
        return connection;
    }
    /// <summary>Holds the cleanup of <paramref name="resource"/> until <see cref="Release"/> when it is the gated
    /// one.</summary>
    /// <remarks>The wait is bounded, so a broken test fails instead of hanging.</remarks>
    internal async Task BeforeCleanup(string resource)
    {
        if (GateResource != resource) return;
        Entered.TrySetResult();
        await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    /// <summary>Records that <paramref name="resource"/> was disposed, then throws its planted cleanup fault, if
    /// any.</summary>
    /// <remarks>The fault comes after the real disposal, so a test can check both that the resource closed and that
    /// the engine kept the first exception.</remarks>
    internal void AfterCleanup(string resource)
    {
        Events.Add(resource + "-disposed");
        if (CleanupErrors.TryGetValue(resource, out var error)) throw error;
    }
}

/// <summary>A real SQLite connection that records opening, command creation and disposal on its
/// <see cref="SqliteProbe"/>.</summary>
internal sealed class SqliteProbeConnection(string text, SqliteProbe probe) : SqliteConnection(text)
{
    /// <summary>The connection text the factory received, so a test can check the engine passed it unchanged.</summary>
    internal string Original => ConnectionString;
    /// <summary>The position of this connection's <c>opened</c> event in the trail; -1 until opened.</summary>
    internal int OpenedEventIndex { get; private set; } = -1;
    /// <summary>True once disposed.</summary>
    internal bool IsDisposed;
    /// <summary>Opens the real connection, recording the token and the <c>open</c>/<c>opened</c> events.</summary>
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        probe.OpenToken = cancellationToken;
        probe.Events.Add("open");
        await base.OpenAsync(cancellationToken);
        OpenedEventIndex = probe.Events.Count;
        probe.Events.Add("opened");
    }
    /// <summary>Wraps the real command in a <see cref="SqliteProbeCommand"/>, recording the <c>command</c>
    /// event.</summary>
    protected override DbCommand CreateDbCommand()
    {
        var commandEventIndex = probe.Events.Count;
        probe.Events.Add("command");
        var command = new SqliteProbeCommand(base.CreateDbCommand(), probe, this, commandEventIndex);
        probe.Commands.Add(command);
        return command;
    }
    /// <summary>Disposes the real connection once, through the probe's cleanup gate and fault.</summary>
    /// <remarks>A second disposal throws, so an engine that disposes twice fails the test.</remarks>
    public override async ValueTask DisposeAsync()
    {
        if (IsDisposed) throw new InvalidOperationException("Repeated outer connection disposal.");
        await probe.BeforeCleanup("connection");
        await base.DisposeAsync();
        IsDisposed = true;
        probe.AfterCleanup("connection");
    }
}

/// <summary>A command that forwards to the real SQLite command and records execution, parameters, provider faults and
/// disposal on its <see cref="SqliteProbe"/>.</summary>
/// <remarks>Members without their own note forward to the inner command unchanged.</remarks>
internal sealed class SqliteProbeCommand(DbCommand inner, SqliteProbe probe,
    SqliteProbeConnection owner, int commandEventIndex) : DbCommand
{
    /// <summary>The connection that created this command.</summary>
    internal SqliteProbeConnection Owner => owner;
    /// <summary>The position of this command's <c>command</c> event in the trail.</summary>
    /// <remarks>Compared with <see cref="SqliteProbeConnection.OpenedEventIndex"/> to prove the command was created
    /// only after its own connection opened.</remarks>
    internal int CommandEventIndex => commandEventIndex;
    /// <summary>True once disposed.</summary>
    internal bool IsDisposed;
    /// <inheritdoc/>
    [AllowNull] public override string CommandText { get => inner.CommandText; set => inner.CommandText = value!; }
    /// <inheritdoc/>
    public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
    /// <inheritdoc/>
    public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
    /// <inheritdoc/>
    public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
    /// <inheritdoc/>
    public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
    /// <inheritdoc/>
    protected override DbConnection? DbConnection { get => inner.Connection; set => inner.Connection = value; }
    /// <inheritdoc/>
    protected override DbTransaction? DbTransaction { get => inner.Transaction; set => inner.Transaction = value; }
    /// <inheritdoc/>
    protected override DbParameterCollection DbParameterCollection => inner.Parameters;
    /// <inheritdoc/>
    protected override DbParameter CreateDbParameter() => inner.CreateParameter();
    /// <inheritdoc/>
    public override void Cancel() => inner.Cancel();
    /// <inheritdoc/>
    public override int ExecuteNonQuery() => inner.ExecuteNonQuery();
    /// <inheritdoc/>
    public override object? ExecuteScalar() => inner.ExecuteScalar();
    /// <inheritdoc/>
    public override void Prepare() => inner.Prepare();
    /// <inheritdoc/>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => inner.ExecuteReader(behavior);
    /// <summary>Executes the real command, recording the token, the bound parameters and the <c>execute</c> and
    /// <c>reader</c> events, and wraps the reader in a <see cref="SqliteProbeReader"/>.</summary>
    /// <remarks>A provider fault is kept in <see cref="SqliteProbe.ProviderFailure"/> and rethrown as is.</remarks>
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
    /// <summary>Disposes the real command once, through the probe's cleanup gate and fault; a second disposal
    /// throws.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (IsDisposed) throw new InvalidOperationException("Repeated outer command disposal.");
        await probe.BeforeCleanup("command");
        await inner.DisposeAsync();
        IsDisposed = true;
        probe.AfterCleanup("command");
    }
}

/// <summary>A reader that forwards to the real SQLite reader and records reads, column-name lookups, planted read
/// faults and disposal on its <see cref="SqliteProbe"/>.</summary>
/// <remarks>Members without their own note forward to the inner reader unchanged.</remarks>
internal sealed class SqliteProbeReader(DbDataReader inner, SqliteProbe probe, string sql) : DbDataReader
{
    /// <summary>The command text this reader came from, so a test can pick out source reads from catalog reads.</summary>
    internal string Sql => sql;
    /// <summary>This reader's asynchronous read attempts, and those that returned a row.</summary>
    internal int ReadAttempts, SuccessfulReads;
    /// <summary>True once disposed.</summary>
    internal bool IsDisposed;
    /// <inheritdoc/>
    public override int FieldCount => inner.FieldCount;
    /// <inheritdoc/>
    public override bool HasRows => inner.HasRows;
    /// <inheritdoc/>
    public override bool IsClosed => inner.IsClosed;
    /// <inheritdoc/>
    public override int Depth => inner.Depth;
    /// <inheritdoc/>
    public override int RecordsAffected => inner.RecordsAffected;
    /// <inheritdoc/>
    public override object this[int ordinal] => inner[ordinal];
    /// <inheritdoc/>
    public override object this[string name] => inner[name];
    /// <summary>Returns the real column name, recording a <c>schema</c> event.</summary>
    public override string GetName(int ordinal) { probe.Events.Add("schema"); return inner.GetName(ordinal); }
    /// <summary>Reads the next real row, recording the token and the <c>read</c> and <c>row</c> or <c>eof</c>
    /// events.</summary>
    /// <remarks>It throws <see cref="SqliteProbe.ReadFailure"/> at attempt <see cref="SqliteProbe.FailReadAt"/>, and
    /// keeps any real provider fault in <see cref="SqliteProbe.ProviderFailure"/>.</remarks>
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
    /// <inheritdoc/>
    public override bool Read() => inner.Read();
    /// <summary>Moves to the next result set, counting the call in <see cref="SqliteProbe.NextResults"/>.</summary>
    public override bool NextResult() { probe.NextResults++; return inner.NextResult(); }
    /// <summary>Moves to the next result set, counting the call in <see cref="SqliteProbe.NextResults"/>.</summary>
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) { probe.NextResults++; return inner.NextResultAsync(cancellationToken); }
    /// <inheritdoc/>
    public override object GetValue(int ordinal) => inner.GetValue(ordinal);
    /// <inheritdoc/>
    public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
    /// <inheritdoc/>
    public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
    /// <inheritdoc/>
    public override char GetChar(int ordinal) => inner.GetChar(ordinal);
    /// <inheritdoc/>
    public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
    /// <inheritdoc/>
    public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
    /// <inheritdoc/>
    public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
    /// <inheritdoc/>
    public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
    /// <inheritdoc/>
    public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
    /// <inheritdoc/>
    public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
    /// <inheritdoc/>
    public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
    /// <inheritdoc/>
    public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
    /// <inheritdoc/>
    public override string GetString(int ordinal) => inner.GetString(ordinal);
    /// <inheritdoc/>
    public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
    /// <inheritdoc/>
    public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
    /// <inheritdoc/>
    public override int GetOrdinal(string name) => inner.GetOrdinal(name);
    /// <inheritdoc/>
    public override int GetValues(object[] values) => inner.GetValues(values);
    /// <inheritdoc/>
    public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
    /// <inheritdoc/>
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    /// <inheritdoc/>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    /// <inheritdoc/>
    public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
    /// <inheritdoc/>
    public override void Close() => inner.Close();
    /// <summary>Disposes the real reader once, through the probe's cleanup gate and fault; a second disposal
    /// throws.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (IsDisposed) throw new InvalidOperationException("Repeated outer reader disposal.");
        await probe.BeforeCleanup("reader");
        await inner.DisposeAsync();
        IsDisposed = true;
        probe.AfterCleanup("reader");
    }
}
