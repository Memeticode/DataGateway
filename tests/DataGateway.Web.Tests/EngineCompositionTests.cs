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

public sealed class EngineCompositionTests
{
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

    private sealed class RecordingEngine : IDatabaseEngine
    {
        string IDatabaseEngine.ProviderName => "synthetic";
        internal int Calls { get; private set; }
        internal ValidatedQuery? Input { get; private set; }
        internal CompiledQuery? Output { get; private set; }
        internal int Validations, Reads, Timeout;
        internal string? Connection;
        internal CompiledQuery? ReadQuery;
        internal CancellationToken Token;
        public void ValidateSourceConnection(string? connection) { Validations++; Connection = connection; }
        public ProviderFailure? RecognizeFailure(Exception error) => null;
        public Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int commandTimeoutSeconds,
            Action opened, Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken cancellationToken)
        {
            Reads++; Connection = connection; ReadQuery = query; Timeout = commandTimeoutSeconds; Token = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            opened();
            return consume(Empty());
        }
        private static async IAsyncEnumerable<IDictionary<string, object?>> Empty() { await Task.CompletedTask; yield break; }

        public CompiledQuery Compile(ValidatedQuery query)
        {
            Calls++;
            Input = query;
            return Output = new SqlServerEngine().Compile(query);
        }
    }
}
