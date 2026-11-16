using System.Globalization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlKata.JsonQuery;
using DataGateway.Web.Catalog;
using Microsoft.Extensions.Options;

namespace DataGateway.Web.Configuration;

public static class DataGatewayConfiguration
{
    public const string ConnectionSetting = "DATAGATEWAY_CATALOG_CONNECTION";
    public const string ConnectionName = "Catalog";
    public const string LimitsSection = "DataGateway:Limits";

    private const string ConnectionHelp =
        "CatalogConfiguration: supply a valid connection naming database Catalog through " +
        "the DATAGATEWAY_CATALOG_CONNECTION environment variable or project user secrets. " +
        "From src/, use: dotnet user-secrets set DATAGATEWAY_CATALOG_CONNECTION " +
        "\"<catalog connection>\" --project DataGateway.Web";

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

        var limits = new DataGatewayLimits
        {
            CommandTimeoutSeconds = ReadLimit(configuration, nameof(DataGatewayLimits.CommandTimeoutSeconds), 30, 1, int.MaxValue)
        };

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
            CommandTimeoutSeconds = limits.CommandTimeoutSeconds,
            MaxJsonLength = maxJsonLength,
            MaxFilterDepth = maxFilterDepth,
            MaxFilterConditions = maxFilterConditions,
            MaxInValues = maxInValues
        };
    }

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

    private static OptionsValidationException RejectConnection(string reason) =>
        new(Options.DefaultName, typeof(DataGatewayConfiguration), [reason + ConnectionHelp]);
}
