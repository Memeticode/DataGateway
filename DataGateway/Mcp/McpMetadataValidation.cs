using DataGateway.Core.Catalog;

namespace DataGateway.Mcp;

internal static class McpMetadataValidation
{
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

    private static void Text(string? value, string field, int maximum, bool title,
        Func<string, InvalidOperationException> refuse)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum
            || title && (value.Contains('\r') || value.Contains('\n')))
            throw refuse($"{field}: must contain 1-{maximum} characters, including a non-whitespace character."
                + (title ? " Titles must not contain CR or LF." : ""));
    }
}
