using DataGateway.Web.Catalog;
using DataGateway.Web.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Catalog;
using DataGateway.Data.Queries;
using DataGateway.Data.PostgreSql;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class PostgreSqlFailureTests
{
    internal static Dictionary<string,object?> Joined() => new(StringComparer.Ordinal)
    {
        ["id"]=7L,["name"]="synthetic_items",["title"]="Items",["description"]="Rows.",["grain"]="One item.",["row_cap"]=5L, ["query_timeout_seconds"] = 1,
        ["object_name"]="synthetic.items",["connection_string"]=PostgreSqlCompilationTests.Connection,
        ["column_name"]="id",["ordinal"]=1L,["column_title"]="Id",["column_description"]="Column id.",["data_type"]="number",
        ["unit"]=DBNull.Value,["magnitude"]=null,["display_format"]=null,
        ["can_select"]=true,["can_filter"]=false,["can_sort"]=true,["can_group"]=false,["can_aggregate"]=true
    };
    [Theory]
    [InlineData(null,"The catalog connection is missing. ","A source host is required.")]
    [InlineData(" ","The catalog connection is missing. ","A source host is required.")]
    [InlineData("Host=localhost","The connection must explicitly name a database. ","A source database is required.")]
    [InlineData("Database=dg_r5_test","The catalog connection must name a PostgreSQL host. ","A source host is required.")]
    [InlineData("Host=localhost;Database=dg_r5_test","The catalog connection must name a user. ","A source user is required.")]
    [InlineData("Username=dg_r5_reader","The connection must explicitly name a database. ","A source host is required.")]
    [InlineData("BadKey=planted-fragment","The catalog connection cannot be parsed. ","The source connection string cannot be parsed.")]
    [InlineData("Host=\"unterminated","The catalog connection cannot be parsed. ","The source connection string cannot be parsed.")]
    [InlineData("Host=localhost;Port=invalid","The catalog connection cannot be parsed. ","The source connection string cannot be parsed.")]
    public async Task Fixed_validation_precedence_zero_allocations(string? input,string catalog,string source)
    {
        using var probe=new PostgreSqlProbe();var engine=new PostgreSqlEngine();
        Assert.Equal(catalog, HostCatalogs.CatalogConnectionError(input, true));
        using var settings = new ErrorLogSettingsScope();
        Environment.SetEnvironmentVariable(DataGatewayConfiguration.ConnectionSetting, input ?? "");
        using var configuration = new ConfigurationManager();
        var refusal = Assert.Throws<OptionsValidationException>(() => DataGatewayConfiguration.ConfigureCatalog(configuration, true));
        const string help = "CatalogConfiguration: supply a valid connection naming database Catalog through " +
            "the DATAGATEWAY_CATALOG_CONNECTION environment variable or project user secrets. " +
            "From src/, use: dotnet user-secrets set DATAGATEWAY_CATALOG_CONNECTION " +
            "\"<catalog connection>\" --project DataGateway.Web";
        Assert.Equal(catalog + help, refusal.Message);
        Assert.Null(refusal.InnerException);
        Assert.Equal(Options.DefaultName, refusal.OptionsName);
        Assert.Equal(typeof(DataGatewayConfiguration), refusal.OptionsType);
        Assert.Equal(catalog + help, Assert.Single(refusal.Failures));
        var error=Assert.Throws<ArgumentException>(()=>engine.ValidateSourceConnection(input));Assert.Equal(source,error.Message);Assert.Null(error.InnerException);
        Assert.Empty(probe.Connections);
        var sourceError=await Assert.ThrowsAsync<ArgumentException>(()=>engine.ReadSourceAsync(input!,SqliteEngineTests.Raw("SELECT id"),7,()=>{},SqliteEngineTests.Collect,default));
        Assert.Equal(source,sourceError.Message);Assert.Null(sourceError.InnerException);
    }
    [Fact]
    public async Task Original_connection_timeout_tokens_and_precancel()
    {
        using var probe=new PostgreSqlProbe {Names=["id"]};
        const string connection=" Host=localhost ; Database=dg_r5_test ; Username=dg_r5_reader ";
        var engine=new PostgreSqlEngine();
        engine.ValidateSourceConnection(connection);
        using var caller=new CancellationTokenSource();
        await new PostgreSqlSourceTests.LifecycleEngine().ReadSourceAsync(connection,SqliteEngineTests.Raw("SELECT id"),9,()=>{},SqliteEngineTests.Collect,caller.Token);
        Assert.Equal(connection,Assert.Single(probe.AssignedConnections));Assert.Equal(caller.Token,probe.OpenToken);Assert.Equal(caller.Token,probe.ExecuteToken);
        Assert.Equal(9,Assert.Single(probe.Commands).CommandTimeout);Assert.Equal("SELECT id",probe.Commands[0].CommandText);
        caller.Cancel();var count=probe.Connections.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>PostgreSqlSourceTests.Read(token:caller.Token));Assert.Equal(count,probe.Connections.Count);
    }
    [Theory]
    [InlineData("57014",true)][InlineData("42P01",false)][InlineData("42501",false)][InlineData("28P01",false)]
    public void SQLSTATE_only_recognizes_provider_root(string state,bool timeout)
    {
        var engine=new PostgreSqlEngine();var error=new PostgresException("controlled","ERROR","ERROR",state);
        var failure=engine.RecognizeFailure(error);Assert.NotNull(failure);Assert.Equal(timeout,failure.IsTimeout);Assert.Null(failure.Number);
        Assert.Null(engine.RecognizeFailure(new Exception("wrapper",error)));
    }
    [Fact]
    public void Provider_inner_timeout_only_and_unrelated_errors()
    {
        var engine=new PostgreSqlEngine();Assert.True(engine.RecognizeFailure(new NpgsqlException("controlled",new IOException("chain",new TimeoutException())))!.IsTimeout);
        Assert.False(engine.RecognizeFailure(new NpgsqlException("controlled"))!.IsTimeout);
        Assert.Null(engine.RecognizeFailure(new TimeoutException()));Assert.Null(engine.RecognizeFailure(new OperationCanceledException()));
        Assert.Throws<ArgumentNullException>(()=>engine.RecognizeFailure(null!));
    }
    [Theory]
    [InlineData(false,"57014","SourceUnavailable","connect")][InlineData(true,"57014","QueryTimeout","command")]
    [InlineData(true,"42P01","SourceUnavailable","command")]
    public async Task Runner_open_versus_command_policy(bool opened,string state,string code,string step)
    {
        var error=new PostgresException("controlled","ERROR","ERROR",state);
        var engine=new PostgreSqlSourceTests.Engine {SourceError=error,SourceOpened=opened};
        var result=await new QueryRunner(engine).RunAsync(PostgreSqlCompilationTests.Source(queryTimeoutSeconds: 7),SqliteEngineTests.Raw("SELECT id"),default);
        Assert.Equal(code,result.Failure?.Code);Assert.Equal(step,result.Failure?.Step);Assert.Null(result.Failure?.SqlNumber);
        Assert.Same(error,result.Failure!.DriverException);
        Assert.Equal(code=="QueryTimeout"?"$.query":"$.source",result.Failure.Path);
    }
    [Fact]
    public async Task Source_and_catalog_cancellation_policies()
    {
        using var probe=new PostgreSqlProbe {OpenError=new OperationCanceledException()};
        var runner=new QueryRunner(new PostgreSqlSourceTests.Engine {SourceError=probe.OpenError});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>runner.RunAsync(PostgreSqlCompilationTests.Source(queryTimeoutSeconds: 7),SqliteEngineTests.Raw("SELECT id"),default));
        using var caller=new CancellationTokenSource();caller.Cancel();
        Assert.Equal("QueryCancelled",(await runner.RunAsync(PostgreSqlCompilationTests.Source(queryTimeoutSeconds: 7),SqliteEngineTests.Raw("SELECT id"),caller.Token)).Failure?.Code);
    }
    private sealed class HostEngine(string mode) : IDatabaseEngine
    {
        string IDatabaseEngine.ProviderName => ((IDatabaseEngine)inner).ProviderName;
        private readonly PostgreSqlEngine inner=new();
        internal bool Armed;
        public CompiledQuery Compile(ValidatedQuery q)=>inner.Compile(q);
        public void ValidateSourceConnection(string? c)=>inner.ValidateSourceConnection(c);
        public ProviderFailure? RecognizeFailure(Exception e)=>inner.RecognizeFailure(e);
        public Task<QueryRows> ReadSourceAsync(string c,CompiledQuery q,int t,Action opened,Func<IAsyncEnumerable<IDictionary<string,object?>>,Task<QueryRows>> consume,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(mode!="open")opened();
            var error=new PostgresException("controlled provider failure","ERROR","ERROR",mode=="timeout"?"57014":"42P01");
            return Task.FromException<QueryRows>(error);
        }
    }
    private sealed class ProviderCatalog(HostEngine engine, string mode) : CatalogService<NpgsqlConnection>(Dapper.Data.DbContext<NpgsqlConnection>.FromFactory(() => throw new InvalidOperationException("Synthetic provider only.")))
    {
    public override Task<global::DataGateway.Core.Catalog.McpMetadata> QueryMcpMetadataAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(global::DataGateway.Web.Tests.McpMetadataFixture.Seed);
    }

        public override Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<SourceListRow>>([new("synthetic_items", "Items", "Rows.", "Item", true, 5)]);
        }
        public override Task<CatalogDetail?> QueryCatalogDetailAsync(string name, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var row = Joined();
            if (engine.Armed && mode == "malformed")
                row["connection_string"] = "unparseable";
            var detail = CatalogTableReader.MapDetail([row], "postgresql", true);
            token.ThrowIfCancellationRequested();
            return Task.FromResult<CatalogDetail?>(detail);
        }
    }
    [Theory]
    [InlineData("open","SourceUnavailable","connect")][InlineData("missing","SourceUnavailable","command")]
    [InlineData("timeout","SourceTimeout","command")][InlineData("malformed","InvalidCatalog","connect")]
    public async Task Provider_and_validation_failures_preserve_public_wire_and_incident_logs(string mode,string golden,string step)
    {
        using var settings=new ErrorLogSettingsScope();Environment.SetEnvironmentVariable("DATAGATEWAY_CATALOG_CONNECTION",PostgreSqlCompilationTests.Connection);
        var engine = new HostEngine(mode);
        await using var host=new LoggedHost(settings: new Dictionary<string,string?> { ["DataGateway:DatabaseEngine"] = "PostgreSql" }, configure:b=>
        {
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IDatabaseEngine>(b.Services,engine);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddTransient<CatalogService<NpgsqlConnection>>(b.Services, _ => new ProviderCatalog(engine, mode));
        });
        await host.Start();
        engine.Armed = true;
        const string body="""{"source":"synthetic_items","query":{"select":["id"]}}""";
        using var response=await host.Client.PostAsync("query",new System.Net.Http.StringContent(body,System.Text.Encoding.UTF8,"application/json"));
        var http=await response.Content.ReadAsStringAsync();var expected=WireGolden.Cases[golden+"/route"];
        string Normalize(string text)
        {
            var node=System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();Assert.Matches("^[0-9a-f]{32}$",node["incidentId"]!.GetValue<string>());
            node["incidentId"]="INCIDENT";return node.ToJsonString(new JsonSerializerOptions {Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
        }
        Assert.Equal(expected.Status,(int)response.StatusCode);Assert.Equal(expected.Body,Normalize(http));
        await using var client=await McpWire.Connect(host.Client);var tool=await McpWire.Call(client,"query_source",body);
        Assert.Equal(WireGolden.Cases[golden+"/tool"].IsError,tool.IsError);Assert.Equal(expected.Body,Normalize(McpWire.Text(tool)));
        var scan=LeakScan.Synthetic(("PostgreSQL catalog",PostgreSqlCompilationTests.Connection),("malformed synthetic source","unparseable"));
        scan.Response("PostgreSQL HTTP",response,http);scan.Body("PostgreSQL MCP",McpWire.Text(tool));
        var lines=host.Lines();Assert.Equal(2,lines.Length);
        foreach(var text in new[]{http,McpWire.Text(tool)})
        {
            var answer=JsonSerializer.Deserialize<JsonElement>(text);var incident=answer.GetProperty("incidentId").GetString();
            var line=Assert.Single(lines,l=>l.GetProperty("IncidentId").GetString()==incident);
            scan.LogLine("PostgreSQL failure",line);Assert.Equal(step,line.GetProperty("Step").GetString());Assert.Equal(JsonValueKind.Null,line.GetProperty("SqlNumber").ValueKind);
            Assert.Equal(mode=="malformed"?"InvalidCatalog":mode=="timeout"?"QueryTimeout":"SourceUnavailable",line.GetProperty("Code").GetString());
        }
    }

}
