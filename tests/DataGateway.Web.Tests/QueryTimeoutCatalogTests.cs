using System.Reflection;
using System.Text.Json;
using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data.Queries;
using DataGateway.Data.SqlServer;
using DataGateway.Failures;
using DataGateway.Web.Catalog;
using DataGateway.Web.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
using static DataGateway.Web.Tests.CatalogEngineTests;

namespace DataGateway.Web.Tests;

public sealed class QueryTimeoutCatalogTests
{
    private const string Requested = "requested_source";
    private const string Path = "$.detail.rows.source.queryTimeoutSeconds";
    private const string Correction = "Source 'requested_source'. Set query_timeout_seconds to an integer from 1 through 86400.";

    [Fact]
    public async Task T1_two_sources_share_a_runner_and_keep_independent_deadlines()
    {
        using var fixture = new SqliteFixture();
        fixture.Change("UPDATE gateway_source SET query_timeout_seconds=1 WHERE name='synthetic_items'");
        var engine = FakeRows.Engine(new DataGateway.Data.Sqlite.SqliteEngine(), FakeRows.From(async (_, _, opened, token) =>
        {
            opened();
            await Task.Delay(TimeSpan.FromMilliseconds(1400), token);
            return [];
        }));
        var runner = new QueryRunner(engine);
        var catalog = fixture.Catalog(provider: "sqlserver");
        var gateway = new GatewayCatalog(catalog.QueryCatalogAsync, catalog.QueryCatalogDetailAsync,
            new Dictionary<string, QueryRunner> { ["sqlserver"] = runner });
        var shortSource = (await gateway.TargetAsync("synthetic_items", default)).Value;
        var longSource = (await gateway.TargetAsync("synthetic_other", default)).Value;
        Assert.Same(shortSource.Runner, longSource.Runner);
        Assert.Equal(1, shortSource.Source.QueryTimeoutSeconds);
        Assert.Equal(30, longSource.Source.QueryTimeoutSeconds);
        var query = new CompiledQuery("SELECT id", new Dictionary<string, object>(), 3, false, [new("id", "number")]);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var shortRun = runner.RunAsync(shortSource.Source, query, bound.Token);
        var longRun = runner.RunAsync(longSource.Source, query, bound.Token);
        var shortResult = await shortRun;
        Assert.False(shortResult.IsSuccess);
        var failure = shortResult.Failure!;
        Assert.Equal("QueryTimeout", failure.Code);
        Assert.Contains("1-second command timeout", failure.Message);
        Assert.False(longRun.IsCompleted);
        Assert.True((await longRun).IsSuccess);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public async Task T2_timeout_message_and_driver_input_use_the_source_value(int seconds)
    {
        var engine = new TimeoutEngine();
        engine.Execute = (_, _, timeout, opened, _, _) =>
        {
            Assert.Equal(seconds, timeout);
            opened();
            throw new TimeoutException("provider sentinel");
        };
        var source = ErrorLogTests.Contract(queryTimeoutSeconds: seconds);
        var result = await new QueryRunner(engine).RunAsync(source, new CompiledQuery("SELECT id", new Dictionary<string, object>(), 3, false, [new("id", "number")]), default);
        Assert.Equal("QueryTimeout", result.Failure!.Code);
        Assert.Equal($"The query ran longer than the {seconds}-second command timeout and was stopped. Narrow the filters, aggregate, or request fewer rows.", result.Failure.Message);
    }

    public static IEnumerable<object?[]> InvalidCells()
    {
        yield return ["missing", null];
        yield return ["null", null];
        yield return ["DBNull", DBNull.Value];
        foreach (var value in new object[] { 0, -1, 86401, "planted bad cell", 30m, 30f, 30d, true, uint.MaxValue, long.MinValue, ulong.MaxValue, DayOfWeek.Monday })
            yield return ["value", value];
    }

    [Theory]
    [MemberData(nameof(InvalidCells))]
    public async Task T3_reader_refusal_is_typed_and_gateway_retains_only_safe_context(string kind, object? value)
    {
        var row = SourceRow();
        row["name"] = Requested;
        if (kind == "missing") row.Remove("query_timeout_seconds");
        else row["query_timeout_seconds"] = value;
        CatalogDetail Read() => CatalogTableReader.MapDetail([row], "sqlserver", true);
        var error = Assert.Throws<InvalidQueryTimeoutException>(() => Read());
        Assert.Null(error.InnerException);
        Assert.Empty(error.Data);
        var gateway = new GatewayCatalog(_ => Task.FromResult<IReadOnlyList<SourceListRow>>([]),
            (_, _) => Task.FromResult<CatalogDetail?>(Read()), new Dictionary<string, QueryRunner>());
        var result = await gateway.SourceAsync(Requested, default);
        SafeFailure(result.Failure!);
        using var cancel = new CancellationTokenSource();
        var cancellationWins = new GatewayCatalog(_ => Task.FromResult<IReadOnlyList<SourceListRow>>([]),
            (_, _) => { cancel.Cancel(); throw error; }, new Dictionary<string, QueryRunner>());
        Assert.Equal("CatalogCancelled", (await cancellationWins.SourceAsync(Requested, cancel.Token)).Failure!.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(86401)]
    [InlineData(1)]
    [InlineData(86400)]
    public async Task T3_application_detail_validation_and_gateway_use_the_same_correction(int seconds)
    {
        var detail = Detail(seconds);
        var failures = CatalogValidator.ValidateDetail(Requested, detail);
        var gateway = new GatewayCatalog(_ => Task.FromResult<IReadOnlyList<SourceListRow>>([]),
            (_, _) => Task.FromResult<CatalogDetail?>(detail),
            new Dictionary<string, QueryRunner> { ["sqlserver"] = new(new SqlServerEngine()) });
        var result = await gateway.SourceAsync(Requested, default);
        if (seconds is < 1 or > 86400)
        {
            var failure = Assert.Single(failures);
            Assert.Equal(Correction, failure.Message);
            Assert.Equal(Path, failure.Path);
            SafeFailure(result.Failure!);
        }
        else
        {
            Assert.Empty(failures);
            Assert.True(result.IsSuccess);
            Assert.Equal(seconds, result.Value.QueryTimeoutSeconds);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(86400)]
    public void T3_reader_accepts_the_same_integral_types_as_row_cap(int seconds)
    {
        foreach (var cell in new object[] { seconds, (uint)seconds, (long)seconds, (ulong)seconds })
        {
            var row = SourceRow();
            row["query_timeout_seconds"] = cell;
            Assert.Equal(seconds, CatalogTableReader.MapDetail([row], "sqlserver", true).Rows.Source.QueryTimeoutSeconds);
        }
        foreach (var cell in new object[] { (sbyte)30, (byte)30, (short)30, (ushort)30 })
        {
            var row = SourceRow();
            row["query_timeout_seconds"] = cell;
            Assert.Equal(30, CatalogTableReader.MapDetail([row], "sqlserver", false).Rows.Source.QueryTimeoutSeconds);
        }
    }

    [Fact]
    public void T3_detail_checks_timeout_immediately_after_row_cap()
    {
        var failures = CatalogValidator.ValidateDetail(Requested, Detail(0, rowCap: 0));
        Assert.Equal(new[] { "$.detail.rows.source.rowCap", Path }, failures.Select(failure => failure.Path));
        Assert.Equal(Correction, failures[1].Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T3_internal_log_has_safe_correction_and_public_answer_stays_InternalError(bool application)
    {
        using var settings = new ErrorLogSettingsScope();
        var state = new HostCatalogState
        {
            Detail = (_, _) =>
            {
                if (application) return Task.FromResult<CatalogDetail?>(Detail(0));
                var row = SourceRow();
                row["query_timeout_seconds"] = "planted confidential cell";
                return Task.FromResult<CatalogDetail?>(CatalogTableReader.MapDetail([row], "sqlserver", true));
            }
        };
        await using var host = new LoggedHost(state);
        await host.Start();
        using var response = await host.Client.PostAsync("/sources/describe", new StringContent("{\"source\":\"requested_source\"}"));
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("InternalError", json.RootElement.GetProperty("code").GetString());
        var line = Assert.Single(host.Lines());
        Assert.Equal("InvalidCatalog", line.GetProperty("Code").GetString());
        Assert.Equal(Path, line.GetProperty("Path").GetString());
        Assert.Equal(Correction, line.GetProperty("Message").GetString());
        Assert.Equal("catalog", line.GetProperty("Step").GetString());
        Assert.Equal(Requested, line.GetProperty("SourceName").GetString());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("DriverMessage").ValueKind);
        Assert.DoesNotContain("planted confidential cell", body + string.Join("\n", host.AllLines().Select(l => l.GetRawText())));
    }

    [Theory]
    [InlineData(false, "41")]
    [InlineData(true, "41")]
    [InlineData(false, "0")]
    [InlineData(true, "-1")]
    [InlineData(false, "")]
    [InlineData(true, " ")]
    [InlineData(false, "abc")]
    public void T6_removed_timeout_is_refused_in_configuration_and_environment(bool environment, string value)
    {
        using var settings = new ErrorLogSettingsScope();
        using var configuration = new ConfigurationManager();
        if (environment)
        {
            var previous = Environment.GetEnvironmentVariable("DataGateway__Limits__CommandTimeoutSeconds");
            try
            {
                Environment.SetEnvironmentVariable("DataGateway__Limits__CommandTimeoutSeconds", value);
                configuration.AddEnvironmentVariables();
                Refused();
            }
            finally { Environment.SetEnvironmentVariable("DataGateway__Limits__CommandTimeoutSeconds", previous); }
        }
        else
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["DataGateway:Limits:CommandTimeoutSeconds"] = value });
            Refused();
        }
        void Refused()
        {
            var error = Assert.Throws<OptionsValidationException>(() => DataGatewayConfiguration.Configure(configuration, new Microsoft.Extensions.DependencyInjection.ServiceCollection()));
            Assert.Contains("InvalidConfiguration", error.Message);
            Assert.Contains("query_timeout_seconds", error.Message);
            Assert.Contains("each source's", error.Message);
        }
    }

    [Theory]
    [InlineData(false, 15)]
    [InlineData(false, 60)]
    [InlineData(true, 15)]
    [InlineData(true, 60)]
    public async Task T7_catalog_constructor_timeout_reaches_reader_commands(bool pg, int seconds)
    {
        object catalog = pg
            ? seconds == 15 ? new PostgreSqlCatalog(DbContext<NpgsqlConnection>.FromFactory(() => new())) : new PostgreSqlCatalog(DbContext<NpgsqlConnection>.FromFactory(() => new()), seconds)
            : seconds == 15 ? new SqlServerCatalog(DbContext<SqlConnection>.FromFactory(() => new())) : new SqlServerCatalog(DbContext<SqlConnection>.FromFactory(() => new()), seconds);
        var timeout = (int)catalog.GetType().GetField("catalogTimeoutSeconds", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(catalog)!;
        var source = File.ReadAllText(DataGateway.Core.Tests.TestFiles.InRepo("DataGateway.Web/Catalog/" + (pg ? "PostgreSql" : "SqlServer") + "Catalog.cs"));
        Assert.Contains("CatalogTableReader.ListAsync(Db, ListSql, catalogTimeoutSeconds, cancellationToken)", source);
        Assert.Contains(pg ? "false, catalogTimeoutSeconds, cancellationToken" : "true, catalogTimeoutSeconds, cancellationToken", source);
        using var probe = new SourceProbe { AllowNextResult = true };
        Load(probe, SourceRow());
        var db = new DbContext<ProbeConnection>("synthetic");
        await CatalogTableReader.ListAsync(db, pg ? PostgreSqlCatalog.ListSql : SqlServerCatalog.ListSql, timeout, default);
        await CatalogTableReader.DetailAsync(db, pg ? PostgreSqlCatalog.ReadSql : SqlServerCatalog.ReadSql,
            pg ? PostgreSqlCatalog.AvailableSql : SqlServerCatalog.AvailableSql, "synthetic", "sqlserver", true, timeout, default);
        Assert.Equal(2, probe.Commands.Count);
        Assert.All(probe.Commands, command => Assert.Equal(seconds, command.CommandTimeout));
    }

    private sealed class TimeoutEngine : DataGateway.Data.IDatabaseEngine
    {
        public string ProviderName => "sqlserver";
        public CompiledQuery Compile(ValidatedQuery query) => new SqlServerEngine().Compile(query);
        public void ValidateSourceConnection(string? connection) { }
        public DataGateway.Data.ProviderFailure? RecognizeFailure(Exception error) => null;
        internal Func<string, CompiledQuery, int, Action, Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>>, CancellationToken, Task<QueryRows>> Execute = null!;
        public Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int commandTimeoutSeconds,
            Action opened, Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken cancellationToken) =>
            Execute(connection, query, commandTimeoutSeconds, opened, consume, cancellationToken);
    }

    private static CatalogDetail Detail(int seconds, int rowCap = 500) => new("sqlserver", new CatalogRows(
        new DataSourceRow("dbo.synthetic", "Server=synthetic;Database=synthetic")
        {
            Name = Requested, Title = "Synthetic", Description = "Synthetic", Grain = "One row",
            IsActive = true, RowCap = rowCap, QueryTimeoutSeconds = seconds
        }, [new("id", 1, "Id", "Synthetic", "number", null, null, null, true, true, true, true, true)]));

    private static void SafeFailure(CatalogFailure failure)
    {
        Assert.Equal("InvalidCatalog", failure.Code);
        Assert.Equal(Path, failure.Path);
        Assert.Equal(Correction, failure.Message);
        Assert.Equal("catalog", failure.Step);
        Assert.Equal(Requested, failure.SourceName);
        Assert.Null(failure.DriverException);
        Assert.Null(failure.ExceptionType);
    }
}
