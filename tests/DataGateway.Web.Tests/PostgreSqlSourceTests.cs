using System.Data.Common;
using System.Reflection;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.PostgreSql;
using Npgsql;
using SqlKata.Compilers;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>Pins the PostgreSQL engine's source read: it reuses the shared base's scoped lifetime, keeps cells as the
/// provider returns them, and cleans up at every cancellation point.</summary>
public sealed class PostgreSqlSourceTests
{
    /// <summary>A PostgreSQL engine whose source read fails with a chosen error, optionally after reporting the
    /// connection open.</summary>
    internal sealed class Engine : IDatabaseEngine
    {
        /// <summary>The real engine's provider name.</summary>
        string IDatabaseEngine.ProviderName => ((IDatabaseEngine)engine).ProviderName;
        /// <summary>The real engine that compiles, validates and recognizes failures.</summary>
        private readonly PostgreSqlEngine engine = new();
        /// <summary>The error the read fails with; <see cref="NotSupportedException"/> when null.</summary>
        internal Exception? SourceError;
        /// <summary>Whether the read reports the connection open before failing.</summary>
        internal bool SourceOpened;
        /// <summary>Compiles with the real engine.</summary>
        public CompiledQuery Compile(ValidatedQuery q) => engine.Compile(q);
        /// <summary>Validates with the real engine.</summary>
        public void ValidateSourceConnection(string? c) => engine.ValidateSourceConnection(c);
        /// <summary>Recognizes failures with the real engine.</summary>
        public ProviderFailure? RecognizeFailure(Exception e) => engine.RecognizeFailure(e);
        /// <summary>Fails with <see cref="SourceError"/>, after calling <c>opened</c> when
        /// <see cref="SourceOpened"/> is set.</summary>
        public Task<QueryRows> ReadSourceAsync(string c, CompiledQuery q, int t, Action opened,
            Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (SourceOpened) opened();
            return Task.FromException<QueryRows>(SourceError ?? new NotSupportedException());
        }
    }

    /// <summary>An engine on the shared base over the fake connection, for testing the base's read lifetime.</summary>
    /// <remarks>These tests exercise the shared base lifetime, not an NpgsqlConnection substitute; compiling hooks
    /// throw because no test compiles through it.</remarks>
    internal sealed class LifecycleEngine() : DatabaseEngine<PostgreSqlProbeConnection>(
        text => new PostgreSqlProbeConnection { ConnectionString = text })
    {
        /// <summary>Not used.</summary>
        protected override Compiler CreateCompiler() => throw new NotSupportedException();
        /// <summary>Not used.</summary>
        protected override string MatchedRowCountExpression => throw new NotSupportedException();
        /// <summary>Not used.</summary>
        protected override (string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern) =>
            throw new NotSupportedException();
        /// <summary>Not used: the raw test query has no parameters.</summary>
        protected override void PrepareParameter(DbParameter parameter, object? value) =>
            throw new NotSupportedException();
        /// <summary>Keeps each cell as read.</summary>
        protected override object ConvertCell(object value, OutputColumn? column) => value;
        /// <summary>Validates with the PostgreSQL engine's rules.</summary>
        public override void ValidateSourceConnection(string? connection) => new PostgreSqlEngine().ValidateSourceConnection(connection);
        /// <summary>Recognizes failures with the PostgreSQL engine's rules.</summary>
        public override ProviderFailure? RecognizeFailure(Exception error) => new PostgreSqlEngine().RecognizeFailure(error);
    }

    /// <summary>Reads <c>SELECT id</c> through <see cref="LifecycleEngine"/> with a seven-second command timeout.</summary>
    /// <param name="consume">The row consumer; collects every row when null.</param>
    /// <param name="opened">Called once the connection opens.</param>
    /// <param name="token">The caller's token.</param>
    internal static Task<QueryRows> Read(Func<IAsyncEnumerable<IDictionary<string,object?>>,Task<QueryRows>>? consume=null,Action? opened=null,CancellationToken token=default) =>
        new LifecycleEngine().ReadSourceAsync(PostgreSqlCompilationTests.Connection,SqliteEngineTests.Raw("SELECT id"),7,opened??(()=>{}),consume??SqliteEngineTests.Collect,token);

    /// <summary>The engine inherits compile and read from the shared base unchanged, and only its compiler hooks are
    /// PostgreSQL's own.</summary>
    /// <remarks>Keeping compile and read in the base means the engine reuses its bounded read and cleanup order
    /// instead of keeping a copy of its own.</remarks>
    [Fact]
    public void Source_methods_are_inherited_and_compiler_hooks_are_postgresql()
    {
        var type = typeof(PostgreSqlEngine);
        var baseType = typeof(DatabaseEngine<NpgsqlConnection>);
        Assert.Equal(baseType, type.BaseType);
        Assert.True(type.IsSealed);
        Assert.True(typeof(NpgsqlConnection).IsSealed);
        Assert.Empty(Assert.Single(type.GetConstructors()).GetParameters());
        foreach (var name in new[] { "Compile", "ReadSourceAsync" })
        {
            Assert.Equal(baseType, type.GetMethod(name)!.DeclaringType);
            Assert.DoesNotContain(type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), method => method.Name == name);
        }
        var map = type.GetInterfaceMap(typeof(IDatabaseEngine));
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
            if (map.InterfaceMethods[i].Name is "Compile" or "ReadSourceAsync")
            {
                var target = map.TargetMethods[i];
                Assert.Equal(type, target.DeclaringType);
                Assert.True(target.IsPrivate);
                Assert.True(target.IsFinal);
                Assert.True(target.IsVirtual);
            }
        var engine = new PostgreSqlEngine();
        object? Hook(string name, params object?[] args) =>
            type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, args);
        Assert.IsType<PostgresCompiler>(Hook("CreateCompiler"));
        Assert.NotSame(Hook("CreateCompiler"), Hook("CreateCompiler"));
        Assert.Equal("COUNT(*) AS \"__dg_matched_rows\"", Hook("get_MatchedRowCountExpression"));
        Assert.Equal((@"%\_[x]\\", "\\"), Hook("PrepareLikePattern", @"%_[x]\"));
    }

    /// <summary>The engine returns every cell as the same object the provider read, whatever the column's declared
    /// type, keeping a decimal's scale and a long beyond double precision.</summary>
    [Fact]
    public void Cells_are_identical_even_when_metadata_is_boolean()
    {
        var engine = new PostgreSqlEngine();
        var hook = typeof(PostgreSqlEngine).GetMethod("ConvertCell", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object[] values = [1.2500m, 9007199254740993L, new DateOnly(2025, 1, 2),
            new DateTime(2025, 1, 2), true, false, 0L, 1L, "", new object()];
        foreach (var value in values)
        foreach (var column in new OutputColumn?[] { null, new("flag", "bool"), new("day", "date") })
            Assert.Same(value, hook.Invoke(engine, [value, column]));
        Assert.Equal(4, (decimal.GetBits((decimal)values[0])[3] >> 16) & 0xff);
    }

    /// <summary>A consumer that never enumerates causes no reads, one that stops early reads no further, a
    /// cancellation on the consumer's own token is honored, and reading past the end reads no more.</summary>
    [Fact]
    public async Task No_enumeration_early_stop_and_distinct_cancellation()
    {
        using var probe=new PostgreSqlProbe {Names=["id"],Values=[[1],[2]]};
        await Read(_=>Task.FromResult(new QueryRows([],false)));Assert.Equal(0,probe.Reads);
        using var other=new CancellationTokenSource();other.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Read(async rows=>
        {
            await using var e=rows.GetAsyncEnumerator(other.Token);await e.MoveNextAsync();return new QueryRows([],false);
        }));
        Assert.Equal(0,probe.Reads);
        var result=await Read(async rows=>{await foreach(var row in rows)return new QueryRows([row],false);return new QueryRows([],false);});
        Assert.Single(result.Rows);Assert.Equal(1,probe.Reads);
        await Read(async rows=>
        {
            await using var e=rows.GetAsyncEnumerator();
            while(await e.MoveNextAsync()) { }
            var reads=probe.Reads;
            Assert.False(await e.MoveNextAsync());Assert.Equal(reads,probe.Reads);
            return new QueryRows([],false);
        });
    }
    /// <summary>Cancelling during open, execute or a read ends the call only after every resource acquired so far is
    /// disposed, once.</summary>
    [Theory]
    [InlineData("open")][InlineData("execute")][InlineData("read")]
    public async Task Cancellation_at_each_async_boundary_awaits_cleanup(string stage)
    {
        using var probe=new PostgreSqlProbe {Names=["id"],Values=[[1]]};
        var entered=PostgreSqlProbe.Gate();var gate=PostgreSqlProbe.Gate();
        if(stage=="open"){probe.OpenGate=gate;probe.OpenEntered=entered;}
        if(stage=="execute"){probe.ExecuteGate=gate;probe.ExecuteEntered=entered;}
        if(stage=="read"){probe.ReadGate=gate;probe.ReadEntered=entered;}
        using var caller=new CancellationTokenSource();var operation=Read(token:caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>operation);
        Assert.Equal(1,probe.ConnectionDisposals);Assert.Equal(stage=="open"?0:1,probe.CommandDisposals);
        Assert.Equal(stage=="read"?1:0,probe.ReaderDisposals);
    }
}
