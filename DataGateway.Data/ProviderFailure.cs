namespace DataGateway.Data;

/// <summary>Provider facts only; null recognition means the exception is not recognized.</summary>
/// <param name="IsTimeout">Whether the driver reports that the command timed out or was cancelled.</param>
/// <param name="Number">The driver's numeric error code, if it has one; logged, never shown to a caller.</param>
public sealed record ProviderFailure(bool IsTimeout, int? Number);
