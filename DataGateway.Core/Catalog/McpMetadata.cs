namespace DataGateway.Core.Catalog;

/// <summary>Catalog-owned MCP server and tool presentation text.</summary>
public sealed record McpMetadata(string Title, string About, IReadOnlyList<McpToolMetadata> Tools);

/// <summary>Presentation text for one of the library's three tools.</summary>
public sealed record McpToolMetadata(string ToolName, string Title, string Description);
