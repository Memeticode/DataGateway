using Dapper.Data;
using DataGateway.Data.Catalog;
using DataGateway.Web.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace DataGateway.Web.Catalog;

internal static class HostCatalogs
{
    internal static string? CatalogConnectionError(string? connection, bool postgreSql)
    {
        if (string.IsNullOrWhiteSpace(connection))
            return "The catalog connection is missing. ";
        try
        {
            if (postgreSql)
            {
                var value = new NpgsqlConnectionStringBuilder(connection);
                if (string.IsNullOrWhiteSpace(value.Database))
                    return "The connection must explicitly name a database. ";
                if (string.IsNullOrWhiteSpace(value.Host))
                    return "The catalog connection must name a PostgreSQL host. ";
                if (string.IsNullOrWhiteSpace(value.Username))
                    return "The catalog connection must name a user. ";
                return null;
            }

            using var probe = new SqlConnection(connection);
            if (string.IsNullOrWhiteSpace(probe.Database))
                return "The connection must explicitly name a database. ";
            if (string.IsNullOrWhiteSpace(probe.DataSource))
                return "The catalog connection must name a SQL Server. ";
            return null;
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            return "The catalog connection cannot be parsed. ";
        }
    }

    internal static void Configure(IServiceCollection services, bool postgreSql, int timeout)
    {
        if (postgreSql)
            services.TryAddTransient<CatalogService<NpgsqlConnection>>(provider =>
                new PostgreSqlCatalog(DbContext<NpgsqlConnection>.FromFactory(() =>
                    new NpgsqlConnection(Connection(provider))), timeout));
        else
            services.TryAddTransient<CatalogService<SqlConnection>>(provider =>
                new SqlServerCatalog(DbContext<SqlConnection>.FromFactory(() =>
                    new SqlConnection(Connection(provider))), timeout));
    }

    internal static void Use(DataGatewayBuilder gateway, bool postgreSql)
    {
        if (postgreSql)
            gateway.UseCatalog<NpgsqlConnection>(services => services.GetRequiredService<CatalogService<NpgsqlConnection>>());
        else
            gateway.UseCatalog<SqlConnection>(services => services.GetRequiredService<CatalogService<SqlConnection>>());
    }

    private static string Connection(IServiceProvider services) =>
        services.GetRequiredService<IConfiguration>().GetConnectionString(DataGatewayConfiguration.ConnectionName)
            ?? throw new InvalidOperationException("The validated catalog connection is missing.");
}
