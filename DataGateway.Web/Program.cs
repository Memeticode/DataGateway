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

/// <summary>The example ASP.NET Core host: one named gateway over a SQL Server or PostgreSQL catalog, with Windows
/// sign-in, the JSON routes, <c>/mcp</c>, and the API page in Development.</summary>
/// <remarks>
/// <para>Everything the library needs comes from here: the catalog reader, the engine, the limits, logging and the
/// sign-in policy. The library reads no setting of its own, so another host can make every one of these choices
/// differently.</para>
/// <para>Startup refuses bad configuration before the server listens, and the process exit code says which kind of
/// failure stopped it (see <see cref="Main"/>).</para>
/// </remarks>
public partial class Program
{
    /// <summary>Runs the host until it stops, and turns a startup failure into a message on stderr and an exit
    /// code.</summary>
    /// <remarks>Exit 0 is a normal stop, 2 is invalid configuration (an <see cref="OptionsValidationException"/>,
    /// whose message is written as is because every such message here names the fix and carries no credential), and
    /// 4 is any other failure. For that last case only the exception's type name is written: an unexpected framework
    /// error can quote configuration values, connection strings included.</remarks>
    /// <param name="args">The command-line arguments, passed to the web application builder.</param>
    /// <returns>The process exit code: 0, 2 or 4.</returns>
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

    /// <summary>Builds the configured host application without starting it.</summary>
    /// <remarks>
    /// <para>Executable and test hosts use the same public composition: <see cref="Main"/> calls this, and tests
    /// call it with their own options and builder hook, so a test exercises the host that ships rather than a
    /// copy of it.</para>
    /// <para><c>DataGateway:DatabaseEngine</c> selects SQL Server (the default when absent) or PostgreSQL, ignoring
    /// case; any other value, blank included, refuses startup. The choice picks both the catalog reader and the
    /// engine that runs source queries.</para>
    /// <para>The gateway is configured before the service provider exists, so it receives a forwarding engine
    /// (<see cref="HostDatabaseEngine"/>) that is bound to the selected engine singleton after the build. The
    /// registration uses <c>TryAddSingleton</c>, so an engine registered earlier through
    /// <paramref name="configureBuilder"/> wins.</para>
    /// <para>If binding or catalog-connection validation fails after the build, the half-built application is
    /// disposed before the original exception is rethrown, so the caller sees the real refusal and no service is
    /// left running.</para>
    /// </remarks>
    /// <param name="options">The options for the web application builder, including the arguments.</param>
    /// <param name="configureBuilder">Optional changes to the builder, applied after logging is set up and before
    /// the host registers its own services.</param>
    /// <returns>The built application, with its middleware and endpoints mapped.</returns>
    /// <exception cref="OptionsValidationException">The configuration is invalid; the message names the fix.</exception>
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
