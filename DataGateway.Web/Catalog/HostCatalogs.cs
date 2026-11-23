using Dapper.Data;
using DataGateway.Data.Catalog;
using DataGateway.Web.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace DataGateway.Web.Catalog;

/// <summary>The host's catalog wiring for the selected engine: the catalog-connection check, the reader's
/// registration, and the gateway's catalog factory.</summary>
/// <remarks>Each method takes the same <c>postgreSql</c> choice that <see cref="Program.CreateApplication"/> made
/// from <c>DataGateway:DatabaseEngine</c>, so the catalog reader always matches the source engine.</remarks>
internal static class HostCatalogs
{
    /// <summary>Checks the catalog connection without opening it.</summary>
    /// <remarks>The connection must name everything explicitly: for SQL Server a server and a database, for
    /// PostgreSQL a host, a database and a user. A driver default, such as the login's default database, is never
    /// used in place of a missing value. The result is a sentence fragment that ends with a space, because
    /// <see cref="DataGatewayConfiguration"/> appends the fix to it. It never quotes the connection, which can hold a
    /// password.</remarks>
    /// <param name="connection">The connection as supplied, possibly null or blank.</param>
    /// <param name="postgreSql">Whether the catalog is PostgreSQL rather than SQL Server.</param>
    /// <returns>Null when the connection is acceptable; otherwise what is wrong with it.</returns>
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

    /// <summary>Registers the catalog reader for the selected engine, over a context that creates a new connection
    /// to the validated catalog connection for each read.</summary>
    /// <remarks>Transient, so each request gets a reader of its own and none is shared between requests.
    /// <c>TryAdd</c>, so a reader registered earlier, through the builder hook of
    /// <see cref="Program.CreateApplication"/>, wins; the tests replace the catalog this way.</remarks>
    /// <param name="services">The host's service collection.</param>
    /// <param name="postgreSql">Whether the catalog is PostgreSQL rather than SQL Server.</param>
    internal static void Configure(IServiceCollection services, bool postgreSql)
    {
        if (postgreSql)
            services.TryAddTransient<CatalogService<NpgsqlConnection>>(provider =>
                new PostgreSqlCatalog(DbContext<NpgsqlConnection>.FromFactory(() =>
                    new NpgsqlConnection(Connection(provider)))));
        else
            services.TryAddTransient<CatalogService<SqlConnection>>(provider =>
                new SqlServerCatalog(DbContext<SqlConnection>.FromFactory(() =>
                    new SqlConnection(Connection(provider)))));
    }

    /// <summary>Gives the gateway its catalog: the reader registered by <see cref="Configure"/>, resolved from the
    /// request's services.</summary>
    /// <param name="gateway">The gateway being registered.</param>
    /// <param name="postgreSql">Whether the catalog is PostgreSQL rather than SQL Server.</param>
    internal static void Use(DataGatewayBuilder gateway, bool postgreSql)
    {
        if (postgreSql)
            gateway.UseCatalog<NpgsqlConnection>(services => services.GetRequiredService<CatalogService<NpgsqlConnection>>());
        else
            gateway.UseCatalog<SqlConnection>(services => services.GetRequiredService<CatalogService<SqlConnection>>());
    }

    /// <summary>The validated catalog connection, as <see cref="DataGatewayConfiguration.ConfigureCatalog"/> exposed
    /// it.</summary>
    /// <remarks>Read when a connection is created rather than at registration, because the connection reaches
    /// the configuration only after the host is built. Its absence then is a composition bug, and throws.</remarks>
    private static string Connection(IServiceProvider services) =>
        services.GetRequiredService<IConfiguration>().GetConnectionString(DataGatewayConfiguration.ConnectionName)
            ?? throw new InvalidOperationException("The validated catalog connection is missing.");
}
