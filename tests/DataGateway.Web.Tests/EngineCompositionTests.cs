using System.Text;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Queries;
using DataGateway.Data.SqlServer;
using DataGateway.Failures;
using DataGateway.Operations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>Pins how the host wires its database engine: SQL Server by default, a registered engine otherwise, and
/// that the query runner and the query operation use that engine for both compiling and reading.</summary>
public sealed class EngineCompositionTests
{
    /// <summary>With no engine registered, the host resolves one shared SQL Server engine.</summary>
    [Fact]
    public async Task T1_default_engine_is_one_sql_server_instance()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(ErrorLogTests.Catalog());
        var engine = host.App.Services.GetRequiredService<IDatabaseEngine>();
        Assert.IsType<SqlServerEngine>(engine);
        Assert.Same(engine, host.App.Services.GetRequiredService<IDatabaseEngine>());
        Assert.Single(host.App.Services.GetServices<IDatabaseEngine>());
    }

    /// <summary>An engine the host registers is the one the runner compiles with and reads through, receiving the
    /// source's connection, its timeout and a deadline token of its own.</summary>
    [Fact]
    public async Task T2_composition_passes_the_registered_engine_to_the_runner()
    {
        using var settings = new ErrorLogSettingsScope();
        var engine = new RecordingEngine();
        await using var host = new LoggedHost(ErrorLogTests.Catalog(),
            configure: builder => builder.Services.AddSingleton<IDatabaseEngine>(engine));
        var runner = await HostCatalogState.NamedRunner(host.App.Services);
        var validated = QueryBuilding.Validate(ErrorLogTests.Contract(), "{}", QueryLimits.ForRowCap(500)).Value;
        Assert.Same(engine, host.App.Services.GetRequiredService<IDatabaseEngine>());
        Assert.Same(runner, await HostCatalogState.NamedRunner(host.App.Services));
        var compiled = runner.Compile(validated);
        Assert.Equal(1, engine.Calls);
        Assert.Same(validated, engine.Input);
        Assert.Same(engine.Output, compiled);
        using var caller = new CancellationTokenSource();
        var source = ErrorLogTests.Contract();
        Assert.True((await runner.RunAsync(source, compiled, caller.Token)).IsSuccess);
        Assert.Equal(1, engine.Validations);
        Assert.Equal(1, engine.Reads);
        Assert.Same(compiled, engine.ReadQuery);
        Assert.Equal(source.ConnectionString, engine.Connection);
        Assert.Equal(source.QueryTimeoutSeconds, engine.Timeout);
        DeadlineToken.Passed(caller.Token, engine.Token);
    }

    /// <summary>The query operation compiles through the runner's engine and, when the read fails, still records the
    /// compiled query and the source in the request trail for the error log.</summary>
    [Fact]
    public async Task T3_operation_uses_runner_compilation_and_preserves_the_diagnostic_trail()
    {
        using var settings = new ErrorLogSettingsScope();
        var engine = new RecordingEngine();
        CompiledQuery? received = null;
        var runner = FakeRows.Runner(engine, (_, compiled, connected, _) =>
        {
            received = compiled;
            connected();
            throw new IOException("synthetic source failure");
        });
        var trail = new RequestTrail();
        var result = await QuerySource.RunAsync(Encoding.UTF8.GetBytes(ErrorLogTests.Body("query")),
            (_, _) => Task.FromResult(
                CatalogResult<QuerySourceContext>.Success(new(ErrorLogTests.Contract(), runner))), QueryLimits.ForRowCap(500), trail, CancellationToken.None);
        Assert.Equal("SourceUnavailable", result.Failure?.Code);
        Assert.Equal("command", result.Failure?.Step);
        Assert.Equal(1, engine.Calls);
        Assert.NotNull(received);
        Assert.Same(engine.Output, received);
        Assert.Same(received, trail.Query);
        Assert.NotNull(trail.Source);
    }

    /// <summary>An engine that records each call's arguments, compiles as SQL Server does and returns no rows.</summary>
    private sealed class RecordingEngine : IDatabaseEngine
    {
        /// <summary>The provider name, <c>synthetic</c>.</summary>
        string IDatabaseEngine.ProviderName => "synthetic";
        /// <summary>How many times <see cref="Compile"/> ran.</summary>
        internal int Calls { get; private set; }
        /// <summary>The last query given to <see cref="Compile"/>.</summary>
        internal ValidatedQuery? Input { get; private set; }
        /// <summary>The last query <see cref="Compile"/> returned.</summary>
        internal CompiledQuery? Output { get; private set; }
        /// <summary>Connection checks, reads, and the last read's command timeout in seconds.</summary>
        internal int Validations, Reads, Timeout;
        /// <summary>The last connection checked or read from.</summary>
        internal string? Connection;
        /// <summary>The last query read.</summary>
        internal CompiledQuery? ReadQuery;
        /// <summary>The last read's token.</summary>
        internal CancellationToken Token;
        /// <summary>Counts the check and records the connection; accepts any value.</summary>
        public void ValidateSourceConnection(string? connection) { Validations++; Connection = connection; }
        /// <summary>Recognizes no failure.</summary>
        public ProviderFailure? RecognizeFailure(Exception error) => null;
        /// <summary>Records the read, reports the connection as opened and hands the consumer no rows.</summary>
        public Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int commandTimeoutSeconds,
            Action opened, Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken cancellationToken)
        {
            Reads++; Connection = connection; ReadQuery = query; Timeout = commandTimeoutSeconds; Token = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            opened();
            return consume(Empty());
        }
        /// <summary>An empty asynchronous row sequence.</summary>
        private static async IAsyncEnumerable<IDictionary<string, object?>> Empty() { await Task.CompletedTask; yield break; }

        /// <summary>Records the input and output of a SQL Server compilation.</summary>
        public CompiledQuery Compile(ValidatedQuery query)
        {
            Calls++;
            Input = query;
            return Output = new SqlServerEngine().Compile(query);
        }
    }
}
