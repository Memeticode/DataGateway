using DataGateway.Core.Catalog;

namespace DataGateway.Mcp;

/// <summary>Checks the catalog's MCP text: one server row and exactly the three tool rows, each with text of the
/// allowed length.</summary>
/// <remarks>Each message names the catalog table and field to fix (<c>mcpserver.title</c>, <c>mcptool.tool_name</c>),
/// because an operator fixes it in the catalog, not in code.</remarks>
internal static class McpMetadataValidation
{
    /// <summary>Refuses metadata that is missing, incomplete or out of bounds, and any final description that is too
    /// long.</summary>
    /// <param name="metadata">The metadata the catalog returned.</param>
    /// <param name="settings">The host's tool prefixes, applied before the final descriptions are measured.</param>
    /// <param name="refuse">Builds the exception, naming the gateway.</param>
    internal static void Validate(McpMetadata? metadata, McpToolSettings settings,
        Func<string, InvalidOperationException> refuse)
    {
        if (metadata is null) throw refuse("mcpserver.id: return the row with id 1.");
        Text(metadata.Title, "mcpserver.title", 80, true, refuse);
        Text(metadata.About, "mcpserver.about", 1000, false, refuse);
        if (metadata.Tools is null) throw refuse("mcptool.tool_name: return all three tool rows.");
        var expected = new HashSet<string>(DataGatewayTools.Routes.Keys, StringComparer.Ordinal);
        foreach (var tool in metadata.Tools)
        {
            if (tool is null || !expected.Remove(tool.ToolName ?? ""))
                throw refuse("mcptool.tool_name: return each of list_sources, describe_source, query_source exactly once.");
            Text(tool.Title, "mcptool.title", 80, true, refuse);
            Text(tool.Description, "mcptool.description", 1000, false, refuse);
        }
        if (expected.Count != 0) throw refuse("mcptool.tool_name: missing " + string.Join(", ", expected) + ".");
        DataGatewayTools.ValidateDescriptions(DataGatewayTools.Definitions(settings, metadata), refuse);
    }

    /// <summary>Refuses text that is blank or too long, and a title that spans lines.</summary>
    /// <param name="value">The text.</param>
    /// <param name="field">The table and column, for the message.</param>
    /// <param name="maximum">The largest length in UTF-16 characters.</param>
    /// <param name="title">True for a title, which must be one line.</param>
    /// <param name="refuse">Builds the exception.</param>
    private static void Text(string? value, string field, int maximum, bool title,
        Func<string, InvalidOperationException> refuse)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum
            || title && (value.Contains('\r') || value.Contains('\n')))
            throw refuse($"{field}: must contain 1-{maximum} characters, including a non-whitespace character."
                + (title ? " Titles must not contain CR or LF." : ""));
    }
}
