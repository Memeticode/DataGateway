using System.Text.RegularExpressions;

namespace DataGateway.Mcp;

/// <summary>The host's optional MCP tool prefixes and how they apply to names and text.</summary>
/// <remarks>A prefix lets one client tell two gateways' tools apart. Every mention of a tool in text the client reads
/// is renamed with it, so guidance never names a tool that does not exist.</remarks>
/// <param name="NamePrefix">Joined to each tool name with an underscore; null for none.</param>
/// <param name="DescriptionPrefix">Put before each tool description; null for none.</param>
internal sealed record McpToolSettings(string? NamePrefix, string? DescriptionPrefix)
{
    /// <summary>A tool's name as clients see it.</summary>
    /// <param name="canonical">The canonical tool name.</param>
    /// <returns>The prefixed name, or the canonical one without a prefix.</returns>
    internal string Name(string canonical) => NamePrefix is null ? canonical : NamePrefix + "_" + canonical;

    /// <summary>Renames every whole-word mention of a tool in <paramref name="text"/>.</summary>
    /// <remarks>Replace each original reference once, even when the prefix itself contains a canonical tool name.</remarks>
    /// <param name="text">Instructions, a title, a description or a schema.</param>
    /// <returns>The text with prefixed tool names.</returns>
    internal string Text(string text) => NamePrefix is null ? text : Regex.Replace(text,
        @"\b(list_sources|describe_source|query_source)\b", match => Name(match.Value));

    /// <summary>A tool description as clients see it: the description prefix, then the catalog's text, renamed.</summary>
    /// <remarks>The host's prefix is renamed with the rest, so a reference it makes to a tool names the tool that exists.</remarks>
    /// <param name="text">The catalog's description.</param>
    /// <returns>The final description.</returns>
    internal string Description(string text) => Text(DescriptionPrefix is null ? text : DescriptionPrefix + " " + text);
}
