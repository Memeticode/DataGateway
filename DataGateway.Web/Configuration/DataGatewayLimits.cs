namespace DataGateway.Web.Configuration;

public sealed class DataGatewayLimits
{
    public int CommandTimeoutSeconds { get; init; } = 30;
}
