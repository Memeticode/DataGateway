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

using DataGateway.Data.SqlServer;
using DataGateway.Web.Catalog;
using DataGateway.Web.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DataGateway.Web.Tests;

public sealed class PostgreSqlCompositionTests
{
    [Theory]
    [InlineData(null,false)][InlineData("SqlServer",false)][InlineData("sqlserver",false)]
    [InlineData("PostgreSql",true)][InlineData("pOsTgReSqL",true)]
    public async Task Selector_and_singleton_without_network(string? value,bool postgres)
    {
        using var settings=new ErrorLogSettingsScope();
        if(postgres)Environment.SetEnvironmentVariable("DATAGATEWAY_CATALOG_CONNECTION",PostgreSqlCompilationTests.Connection);
        await using var host=new LoggedHost(settings:new Dictionary<string,string?>{["DataGateway:DatabaseEngine"]=value});
        var selected=host.App.Services.GetRequiredService<IDatabaseEngine>();
        Assert.Equal(postgres?typeof(PostgreSqlEngine):typeof(SqlServerEngine),selected.GetType());
        Assert.Same(selected,Assert.Single(host.App.Services.GetServices<IDatabaseEngine>()));
        if(postgres) Assert.IsType<PostgreSqlCatalog>(host.App.Services.GetRequiredService<CatalogService<NpgsqlConnection>>());
        else
        {
            Assert.IsType<HostCatalogFixture>(host.App.Services.GetRequiredService<CatalogService<Microsoft.Data.SqlClient.SqlConnection>>());
            var runner = await HostCatalogState.NamedRunner(host.App.Services);
            var forwarding = Assert.IsType<HostDatabaseEngine>(typeof(QueryRunner).GetField("engine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(runner));
            Assert.Equal("sqlserver", forwarding.ProviderName);
            Assert.Same(selected, typeof(HostDatabaseEngine).GetField("selected", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(forwarding));
        }
    }
    [Theory]
    [InlineData("")][InlineData(" ")][InlineData("unknown")]
    public void Invalid_selector_fixed_message(string value)
    {
        using var settings=new ErrorLogSettingsScope();
        var error=Assert.Throws<OptionsValidationException>(()=>new LoggedHost(settings:new Dictionary<string,string?>{["DataGateway:DatabaseEngine"]=value}));
        Assert.Equal("InvalidConfiguration: DataGateway:DatabaseEngine must be SqlServer or PostgreSql.",Assert.Single(error.Failures));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Earlier_instance_or_factory_has_precedence(bool factory)
    {
        using var settings = new ErrorLogSettingsScope();
        var injected=new PostgreSqlEngine();
        await using var host=new LoggedHost(settings:new Dictionary<string,string?>{["DataGateway:DatabaseEngine"]="SqlServer"},configure:b=>
        {if(factory)b.Services.AddSingleton<IDatabaseEngine>(_=>injected);else b.Services.AddSingleton<IDatabaseEngine>(injected);});
        Assert.Same(injected,Assert.Single(host.App.Services.GetServices<IDatabaseEngine>()));
    }
}
