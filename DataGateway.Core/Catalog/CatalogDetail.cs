namespace DataGateway.Core.Catalog;

/// <summary>Unvalidated source rows and the provider that executes their queries.</summary>
/// <remarks>
/// Provider and Rows retain the application's values for subsequent catalog validation.
/// Connection data remains on DataSourceRow, whose JSON attributes protect nested serialization.
/// </remarks>
/// <param name="provider">The registration name of the engine that runs this source's queries, as the catalog stores
/// it.</param>
/// <param name="rows">The source row and its column rows.</param>
public sealed class CatalogDetail(string provider, CatalogRows rows)
{
    /// <summary>The registration name of the engine that runs this source's queries.</summary>
    /// <remarks>Kept as stored, untrimmed; <see cref="CatalogValidator.ValidateDetail"/> refuses a blank value, and the
    /// host matches it against its registered engines.</remarks>
    public string Provider { get; } = provider;
    /// <summary>The source row and its column rows, still unvalidated.</summary>
    public CatalogRows Rows { get; } = rows;
}
