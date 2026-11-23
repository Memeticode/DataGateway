namespace DataGateway.Web.Configuration;

/// <summary>The host's limits options type, registered as <c>IOptions&lt;DataGatewayLimits&gt;</c> and named by the
/// limit refusals in <see cref="DataGatewayConfiguration"/>.</summary>
/// <remarks>It has no members: the settings it once carried moved out. Each source's row cap and query timeout
/// come from its catalog row, each connection string sets its own connect behaviour, and the structural query limits
/// go to the gateway as <see cref="GatewayLimits"/>. Tests check that none of the retired properties comes
/// back.</remarks>
public sealed class DataGatewayLimits
{
}
