namespace DataGateway.Data.Catalog;

/// <summary>A missing or malformed MCP projection cell, identified without its stored value.</summary>
public sealed class InvalidMcpMetadataException(string field) : Exception("The MCP catalog projection is invalid.")
{
    public string Field { get; } = field;
}
