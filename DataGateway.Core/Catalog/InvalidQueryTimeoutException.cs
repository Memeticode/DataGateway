namespace DataGateway.Core.Catalog;

/// <summary>A catalog detail has an absent or invalid query_timeout_seconds cell.</summary>
public sealed class InvalidQueryTimeoutException : Exception
{
    public InvalidQueryTimeoutException() : base("The catalog query_timeout_seconds cell is invalid.") { }

    public static CatalogFailure Failure(string requestedName) => new("InvalidCatalog",
        "$.detail.rows.source.queryTimeoutSeconds",
        $"Source '{requestedName}'. Set query_timeout_seconds to an integer from 1 through 86400.")
    { SourceName = requestedName };
}
