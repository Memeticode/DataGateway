using SqlKata.JsonQuery;

namespace DataGateway;

/// <summary>Host-supplied command and JSON query limits for one named gateway.</summary>
/// <remarks>Values are validated when AddDataGateway completes. Each UseLimits call replaces all five values.
/// Unspecified properties receive their defaults. Source row caps come from the catalog.</remarks>
/// <example><code>gateway.UseLimits(new GatewayLimits { CommandTimeoutSeconds = 7, MaxInValues = 3 });</code></example>
public sealed class GatewayLimits
{
    /// <summary>Command timeout in seconds. Default 30. Range 1 through int.MaxValue.</summary>
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>Maximum query JSON length in characters. Package default 65536. Range 1 through int.MaxValue.</summary>
    public int MaxJsonLength { get; init; } = JsonQueryLimits.Default.MaxJsonLength;

    /// <summary>Maximum filter group depth. Package default 8. Range 0 through 30. Zero permits flat filters.</summary>
    public int MaxFilterDepth { get; init; } = JsonQueryLimits.Default.MaxFilterDepth;

    /// <summary>Maximum total leaf filter conditions. Package default 50. Range 1 through int.MaxValue.</summary>
    public int MaxFilterConditions { get; init; } = JsonQueryLimits.Default.MaxFilterConditions;

    /// <summary>Maximum raw entries in one in or not_in array. Package default 100. Range 1 through int.MaxValue.</summary>
    public int MaxInValues { get; init; } = JsonQueryLimits.Default.MaxInValues;
}
