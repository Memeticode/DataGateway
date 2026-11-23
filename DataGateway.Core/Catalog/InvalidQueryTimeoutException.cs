namespace DataGateway.Core.Catalog;

/// <summary>A catalog detail has an absent or invalid query_timeout_seconds cell.</summary>
/// <remarks>A catalog reader throws it when it cannot read the cell as an integer from 1 through 86400. The gateway
/// turns this exception, and only this one, into the steward's correction from <see cref="Failure"/>; any other
/// reader exception becomes <c>CatalogUnavailable</c>. The message names the cell, never its stored value.</remarks>
public sealed class InvalidQueryTimeoutException : Exception
{
    /// <summary>Creates the exception with its fixed message.</summary>
    public InvalidQueryTimeoutException() : base("The catalog query_timeout_seconds cell is invalid.") { }

    /// <summary>The <c>InvalidCatalog</c> failure for an invalid timeout on one source.</summary>
    /// <remarks>Shared by the reader path (this exception) and <see cref="CatalogValidator.ValidateDetail"/> (a value
    /// out of range), so both name the same path and correction.</remarks>
    /// <param name="requestedName">The source name the caller asked for.</param>
    /// <returns>The failure at <c>$.detail.rows.source.queryTimeoutSeconds</c>.</returns>
    public static CatalogFailure Failure(string requestedName) => new("InvalidCatalog",
        "$.detail.rows.source.queryTimeoutSeconds",
        $"Source '{requestedName}'. Set query_timeout_seconds to an integer from 1 through 86400.")
    { SourceName = requestedName };
}
