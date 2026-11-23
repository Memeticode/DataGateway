namespace DataGateway.Core.Catalog;

/// <summary>Catalog-owned MCP server and tool presentation text.</summary>
/// <remarks>Read from the catalog, so the text a model sees can be edited without a code change. The library still
/// generates the tool schemas and the fixed protocol rules.</remarks>
/// <param name="Title">The MCP server's title.</param>
/// <param name="About">The text that opens the server instructions.</param>
/// <param name="Tools">The presentation text for each of the three tools.</param>
public sealed record McpMetadata(string Title, string About, IReadOnlyList<McpToolMetadata> Tools);

/// <summary>Presentation text for one of the library's three tools.</summary>
/// <param name="ToolName">The tool's canonical name: <c>list_sources</c>, <c>describe_source</c> or
/// <c>query_source</c>.</param>
/// <param name="Title">The tool's title.</param>
/// <param name="Description">The tool's description.</param>
public sealed record McpToolMetadata(string ToolName, string Title, string Description);
