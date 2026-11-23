// A DataGateway app that admits only Windows-signed-in callers, over a one-file SQLite database it seeds itself.
// The catalog and database files are the library README's quickstart unchanged; this file adds the sign-in.
namespace WindowsAuthSample
{
    using Dapper.Data;
    using DataGateway;
    using DataGateway.Data.Sqlite;
    using Microsoft.AspNetCore.Authentication.Negotiate;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.Data.Sqlite;

    public static class SampleProgram
    {
        public static async Task Main(string[] args)
        {
            // Beside the program, not the current folder: IIS and services start the app from elsewhere.
            var file = Path.Combine(AppContext.BaseDirectory, "sample.db");
            if (args is ["--seed"])
            {
                await SampleDatabase.CreateAsync(file, CancellationToken.None);
                return;
            }
            await using var app = CreateApplication(args, file);
            await app.RunAsync();
        }

        public static WebApplication CreateApplication(string[] args, string file)
        {
            var connection = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(file),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddDataGateway("sample", gateway => gateway
                .UseCatalog<SqliteConnection>(_ => new SampleCatalog(
                    DbContext<SqliteConnection>.FromFactory(
                        () => new SqliteConnection(connection)), connection))
                .AddEngine(new SqliteEngine()));

            // Windows sign-in: Kerberos or NTLM through HTTP Negotiate. Under IIS with Windows Authentication on, the
            // handler leaves the handshake to IIS. The fallback policy makes every endpoint, /mcp included, refuse
            // anonymous callers, so no route can be left open by forgetting an attribute.
            builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
            builder.Services.AddAuthorizationBuilder()
                .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapDataGateway("sample", "");
            return app;
        }
    }
}
