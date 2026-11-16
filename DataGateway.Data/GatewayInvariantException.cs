namespace DataGateway.Data;

/// <summary>A broken assumption inside DataGateway itself, such as a compiled query whose rows lack a column it added.</summary>
/// <remarks>It is never classified as a source failure, so it is logged and answered as an unexpected error with an
/// incident ID instead of a temporary outage the caller may retry.</remarks>
internal sealed class GatewayInvariantException(string message) : Exception(message);
