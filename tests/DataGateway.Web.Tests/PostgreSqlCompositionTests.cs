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

/// <summary>Pins how the example host picks its database engine from <c>DataGateway:DatabaseEngine</c>.</summary>
/// <remarks>The setting selects SQL Server (the default) or PostgreSQL, ignoring case; anything else stops startup
/// with a fixed message. An engine the app registers itself wins over the setting.</remarks>
public sealed class PostgreSqlCompositionTests
{
    /// <summary>The selector registers exactly one engine of the chosen kind, and its matching catalog, without
    /// opening a connection.</summary>
    /// <remarks>On SQL Server the query runner reaches the selected engine through the host's forwarding engine, which
    /// keeps the <c>sqlserver</c> provider name, so source aliases resolve unchanged.</remarks>
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
    /// <summary>An empty, blank or unknown selector fails startup with one fixed message that names both
    /// choices.</summary>
    [Theory]
    [InlineData("")][InlineData(" ")][InlineData("unknown")]
    public void Invalid_selector_fixed_message(string value)
    {
        using var settings=new ErrorLogSettingsScope();
        var error=Assert.Throws<OptionsValidationException>(()=>new LoggedHost(settings:new Dictionary<string,string?>{["DataGateway:DatabaseEngine"]=value}));
        Assert.Equal("InvalidConfiguration: DataGateway:DatabaseEngine must be SqlServer or PostgreSql.",Assert.Single(error.Failures));
    }
    /// <summary>An engine the app registered before the host's setup, as an instance or a factory, is kept, and the
    /// selector adds no second one.</summary>
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
