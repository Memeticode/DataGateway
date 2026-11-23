using System.Globalization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlKata.JsonQuery;
using DataGateway.Web.Catalog;
using Microsoft.Extensions.Options;

namespace DataGateway.Web.Configuration;

/// <summary>Reads and validates the host's configuration: the query limits, and the one catalog connection.</summary>
/// <remarks>
/// <para>Every refusal is an <see cref="OptionsValidationException"/> whose message names the setting and the fix,
/// so <see cref="Program.Main"/> can print it and exit 2. No message carries a credential value.</para>
/// <para>The catalog connection is the host's only credential, and it has exactly one way in: the
/// <see cref="ConnectionSetting"/> variable or the same key in user secrets. A <c>ConnectionStrings</c> section in an
/// appsettings file or on the command line is refused, not merged: it is not a second credential source.</para>
/// </remarks>
public static class DataGatewayConfiguration
{
    /// <summary>The environment variable, or user-secrets key, that supplies the catalog connection.</summary>
    public const string ConnectionSetting = "DATAGATEWAY_CATALOG_CONNECTION";
    /// <summary>The logical connection name the validated catalog connection is exposed under, as
    /// <c>ConnectionStrings:Catalog</c>.</summary>
    public const string ConnectionName = "Catalog";
    /// <summary>The configuration section that holds the structural query limits.</summary>
    public const string LimitsSection = "DataGateway:Limits";

    /// <summary>The fix appended to every catalog-connection refusal: where the connection goes and the command
    /// that sets it.</summary>
    private const string ConnectionHelp =
        "CatalogConfiguration: supply a valid connection naming database Catalog through " +
        "the DATAGATEWAY_CATALOG_CONNECTION environment variable or project user secrets. " +
        "From src/, use: dotnet user-secrets set DATAGATEWAY_CATALOG_CONNECTION " +
        "\"<catalog connection>\" --project DataGateway.Web";

    /// <summary>Validates the configuration read before the build and registers the structural query limits.</summary>
    /// <remarks>
    /// <para>Retired settings (<c>ConnectTimeoutSeconds</c>, <c>RowCap</c>, <c>CommandTimeoutSeconds</c> under
    /// <see cref="LimitsSection"/>) are refused whenever present, even blank, instead of being ignored. Each now
    /// lives elsewhere (the connection string, or each source's catalog row), and the refusal says where.</para>
    /// <para>The shared <see cref="JsonQueryLimits"/> has no <c>MaxLimit</c>: each describe or query call sets it
    /// from the source's catalog <c>row_cap</c>.</para>
    /// </remarks>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="services">The host's service collection.</param>
    /// <returns>The same four limits, for the gateway's <c>UseLimits</c>.</returns>
    /// <exception cref="OptionsValidationException">A <c>ConnectionStrings</c> section or a retired setting is
    /// present, or a limit is not an integer in its range.</exception>
    public static GatewayLimits Configure(ConfigurationManager configuration, IServiceCollection services)
    {
        // ConnectionStrings in files/arguments are not a second credential source.
        if (configuration.GetSection("ConnectionStrings").GetChildren().Any())
        {
            throw RejectConnection("Remove the ConnectionStrings section; configure only the catalog. ");
        }

        if (configuration.GetSection("DataGateway:Limits:ConnectTimeoutSeconds").Exists())
            throw new OptionsValidationException(Options.DefaultName, typeof(DataGatewayLimits),
                ["InvalidConfiguration: DataGateway:Limits:ConnectTimeoutSeconds is no longer read, because each connection string governs its own connect behaviour. Remove it (DataGateway__Limits__ConnectTimeoutSeconds in the environment), and set Connect Timeout and ConnectRetryCount in the connection string if you want values other than the driver's."]);

        if (configuration.GetSection("DataGateway:Limits:RowCap").Exists())
            throw new OptionsValidationException(Options.DefaultName, typeof(DataGatewayLimits),
                ["InvalidConfiguration: DataGateway:Limits:RowCap is no longer read, because each source's row_cap in the catalog governs its cap. Remove it (DataGateway__Limits__RowCap in the environment), and set row_cap in the catalog."]);

        if (configuration.GetSection("DataGateway:Limits:CommandTimeoutSeconds").Exists())
            throw new OptionsValidationException(Options.DefaultName, typeof(DataGatewayLimits),
                ["InvalidConfiguration: DataGateway:Limits:CommandTimeoutSeconds is no longer read, because each source's query_timeout_seconds in the catalog governs its timeout. Remove it (DataGateway__Limits__CommandTimeoutSeconds in the environment), and set query_timeout_seconds in the catalog."]);

        var limits = new DataGatewayLimits();

        var maxJsonLength = ReadLimit(configuration, nameof(JsonQueryLimits.MaxJsonLength),
            JsonQueryLimits.Default.MaxJsonLength, 1, int.MaxValue);
        var maxFilterDepth = ReadLimit(configuration, nameof(JsonQueryLimits.MaxFilterDepth),
            JsonQueryLimits.Default.MaxFilterDepth, 0, JsonQueryLimits.None.MaxFilterDepth);
        var maxFilterConditions = ReadLimit(configuration, nameof(JsonQueryLimits.MaxFilterConditions),
            JsonQueryLimits.Default.MaxFilterConditions, 1, int.MaxValue);
        var maxInValues = ReadLimit(configuration, nameof(JsonQueryLimits.MaxInValues),
            JsonQueryLimits.Default.MaxInValues, 1, int.MaxValue);
        var queryLimits = JsonQueryLimits.Default with
        {
            MaxLimit = null,
            MaxJsonLength = maxJsonLength,
            MaxFilterDepth = maxFilterDepth,
            MaxFilterConditions = maxFilterConditions,
            MaxInValues = maxInValues
        };

        services.AddSingleton<IOptions<DataGatewayLimits>>(Options.Create(limits));
        services.TryAddSingleton(queryLimits);
        return new GatewayLimits
        {
            MaxJsonLength = maxJsonLength,
            MaxFilterDepth = maxFilterDepth,
            MaxFilterConditions = maxFilterConditions,
            MaxInValues = maxInValues
        };
    }

    /// <summary>Reads the catalog connection from its one source, validates it, and exposes it to the catalog reader
    /// under <c>ConnectionStrings:Catalog</c>.</summary>
    /// <remarks>
    /// <para>The connection is read from a configuration of its own (user secrets, then environment variables, so
    /// the environment wins) rather than from the application's configuration. ASP.NET Core loads user secrets
    /// only in Development, and this host needs them in every environment. An environment value wins even when it
    /// is blank or invalid: validation fails instead of falling back to user secrets.</para>
    /// <para>The validation parses the connection but never opens it
    /// (<see cref="HostCatalogs.CatalogConnectionError"/>).</para>
    /// </remarks>
    /// <param name="configuration">The host's configuration, which receives the validated connection.</param>
    /// <param name="postgreSql">Whether the catalog is PostgreSQL rather than SQL Server.</param>
    /// <exception cref="OptionsValidationException">The connection is missing, blank, unparseable or does not name
    /// what its engine requires.</exception>
    public static void ConfigureCatalog(ConfigurationManager configuration, bool postgreSql)
    {
        // Explicit loading supports user secrets in every host environment; environment wins.
        using var credentials = new ConfigurationManager();
        credentials.AddUserSecrets<Program>(optional: true, reloadOnChange: false);
        credentials.AddEnvironmentVariables();
        var connection = credentials[ConnectionSetting];
        if (HostCatalogs.CatalogConnectionError(connection, postgreSql) is { } error)
            throw RejectConnection(error);

        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Expose only the validated entry, not a second copy at the input key.
            [ConnectionSetting] = null,
            [$"ConnectionStrings:{ConnectionName}"] = connection
        });
    }

    /// <summary>Reads one limit under <see cref="LimitsSection"/>: its default when absent, otherwise an invariant
    /// integer within the range.</summary>
    /// <remarks>A present but blank or malformed value is refused, never replaced by the default. The refusal names
    /// both the configuration key and its environment-variable spelling.</remarks>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="name">The limit's name, as on <see cref="JsonQueryLimits"/>.</param>
    /// <param name="defaultValue">The value used when the setting is absent.</param>
    /// <param name="minimum">The smallest accepted value.</param>
    /// <param name="maximum">The largest accepted value.</param>
    /// <returns>The configured or default value.</returns>
    private static int ReadLimit(IConfiguration configuration, string name, int defaultValue, int minimum, int maximum)
    {
        var key = $"{LimitsSection}:{name}";
        var raw = configuration[key];
        if (raw is null)
        {
            return defaultValue;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
            value >= minimum && value <= maximum)
        {
            return value;
        }

        throw new OptionsValidationException(Options.DefaultName, typeof(DataGatewayLimits),
            [$"InvalidConfiguration: {key} must be an integer from {minimum} to {maximum}. " +
             $"Set {key} in configuration or {key.Replace(":", "__")} in the environment."]);
    }

    /// <summary>A catalog-connection refusal: the reason, then <see cref="ConnectionHelp"/>.</summary>
    /// <param name="reason">What is wrong, ending with a space.</param>
    private static OptionsValidationException RejectConnection(string reason) =>
        new(Options.DefaultName, typeof(DataGatewayConfiguration), [reason + ConnectionHelp]);
}
