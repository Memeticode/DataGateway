using System.Text.RegularExpressions;

namespace DataGateway.Mcp;

internal sealed record McpToolSettings(string? NamePrefix, string? DescriptionPrefix)
{
    internal string Name(string canonical) => NamePrefix is null ? canonical : NamePrefix + "_" + canonical;

    // Replace each original reference once, even when the prefix itself contains a canonical tool name.
    internal string Text(string text) => NamePrefix is null ? text : Regex.Replace(text,
        @"\b(list_sources|describe_source|query_source)\b", match => Name(match.Value));

    // The host's prefix is renamed with the rest, so a reference it makes to a tool names the tool that exists.
    internal string Description(string text) => Text(DescriptionPrefix is null ? text : DescriptionPrefix + " " + text);
}
