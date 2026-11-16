namespace DataGateway.Core.Catalog;

/// <summary>Unvalidated source rows and the provider that executes their queries.</summary>
/// <remarks>
/// Provider and Rows retain the application's values for subsequent catalog validation.
/// Connection data remains on DataSourceRow, whose JSON attributes protect nested serialization.
/// </remarks>
public sealed class CatalogDetail(string provider, CatalogRows rows)
{
    public string Provider { get; } = provider;
    public CatalogRows Rows { get; } = rows;
}
