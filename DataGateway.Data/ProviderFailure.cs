namespace DataGateway.Data;

/// <summary>Provider facts only; null recognition means the exception is not recognized.</summary>
public sealed record ProviderFailure(bool IsTimeout, int? Number);
