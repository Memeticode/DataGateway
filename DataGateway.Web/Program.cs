using DataGateway;
using DataGateway.Core.Errors;
using DataGateway.Data;
using DataGateway.Data.SqlServer;
using DataGateway.Data.PostgreSql;
using DataGateway.Web.Catalog;
using DataGateway.Web.Configuration;
using DataGateway.Web.SignIn;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

public partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            await using var app = CreateApplication(new WebApplicationOptions { Args = args });
            await app.RunAsync();
            return 0;
        }
        catch (OptionsValidationException failure)
        {
            Console.Error.WriteLine(failure.Message);
            return 2;
        }
        catch (Exception failure)
        {
            // Unexpected framework errors can contain configuration values too.
            Console.Error.WriteLine($"{DataGatewayError.InternalError.Code}: DataGateway.Web failed ({failure.GetType().Name}). " +
                "ESCALATE: reproduce with dotnet run --project DataGateway.Web and the same configuration sources; " +
                "credential details are suppressed.");
            return 4;
        }
    }

    // Executable and test hosts use the same public composition.
    public static WebApplication CreateApplication(WebApplicationOptions options,
        Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var builder = WebApplication.CreateBuilder(options);
        DataGatewayLog.Use(builder);
        configureBuilder?.Invoke(builder);
        var limits = DataGatewayConfiguration.Configure(builder.Configuration, builder.Services);
        WindowsSignIn.Configure(builder.Services);
        var databaseEngine = builder.Configuration["DataGateway:DatabaseEngine"];
        bool postgreSql;
        if (databaseEngine is null ||
            string.Equals(databaseEngine, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            postgreSql = false;
            builder.Services.TryAddSingleton<IDatabaseEngine, SqlServerEngine>();
        }
        else if (string.Equals(databaseEngine, "PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            postgreSql = true;
            builder.Services.TryAddSingleton<IDatabaseEngine, PostgreSqlEngine>();
        }
        else
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(IDatabaseEngine),
                ["InvalidConfiguration: DataGateway:DatabaseEngine must be SqlServer or PostgreSql."]);
        }
        var forwarding = new HostDatabaseEngine(postgreSql ? PostgreSqlEngine.ProviderName : SqlServerEngine.ProviderName);
        HostCatalogs.Configure(builder.Services, postgreSql);
        builder.Services.AddDataGateway("DataGateway.Web", gateway =>
        {
            gateway.AddEngine(forwarding).UseLimits(limits)
                .UseHostPolicy(WindowsSignIn.NotAuthenticated, WindowsSignIn.Description, WindowsSignIn.ResponseDescription);
            HostCatalogs.Use(gateway, postgreSql);
        });
        var app = builder.Build();
        try
        {
            var selected = app.Services.GetRequiredService<IDatabaseEngine>();
            forwarding.Bind(selected);
            DataGatewayConfiguration.ConfigureCatalog(builder.Configuration, postgreSql);
        }
        catch
        {
            try { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* Preserve the original startup refusal after awaited cleanup. */ }
            throw;
        }
        app.UseDataGatewayErrors("DataGateway.Web");
        app.UseRouting();
        WindowsSignIn.Require(app);
        app.MapDataGateway("DataGateway.Web", "");
        if (app.Environment.IsDevelopment())
            app.MapDataGatewayApiPage("DataGateway.Web", "");
        return app;
    }
}
