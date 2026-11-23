namespace DataGateway.Data.Catalog;

/// <summary>A missing or malformed MCP projection cell, identified without its stored value.</summary>
/// <remarks>A catalog reader throws it for a bad MCP presentation row; the gateway answers <c>CatalogUnavailable</c>
/// with the <see cref="Field"/> to correct.</remarks>
/// <param name="field">The table and field at fault, such as <c>mcpserver.id</c>.</param>
public sealed class InvalidMcpMetadataException(string field) : Exception("The MCP catalog projection is invalid.")
{
    /// <summary>The table and field at fault, such as <c>mcpserver.id</c>.</summary>
    public string Field { get; } = field;
}
