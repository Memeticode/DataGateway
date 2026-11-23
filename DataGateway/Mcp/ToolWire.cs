using System.Text.Encodings.Web;
using System.Text.Json;
using DataGateway.Core.Errors;
using ModelContextProtocol.Protocol;

namespace DataGateway.Mcp;

/// <summary>Turns tool arguments into a request body, and answers into tool results.</summary>
internal static class ToolWire
{
    /// <summary>The text block an unexpected failure adds for the calling model; <c>{0}</c> is the incident ID.</summary>
    /// <remarks>The error body says little, the same as on HTTP; what the model should do lives here, in the MCP layer,
    /// as a second content block.</remarks>
    internal const string Guidance = "An unexpected error stopped DataGateway from completing this call. Do not retry it. Tell the user to report incident ID {0} to DataGateway's operator; the details are only in DataGateway's log.";

    /// <summary>Writes the call's arguments as a compact JSON object, the body the matching HTTP route would
    /// receive.</summary>
    /// <remarks>No arguments give <c>{}</c>. Non-ASCII text is written as itself, not as escapes.</remarks>
    /// <param name="arguments">The call's arguments, or null.</param>
    /// <returns>The UTF-8 body.</returns>
    internal static byte[] Body(IDictionary<string, JsonElement>? arguments)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            if (arguments is not null)
                foreach (var (name, value) in arguments)
                {
                    writer.WritePropertyName(name);
                    value.WriteTo(writer);
                }
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>A successful result holding the answer JSON.</summary>
    /// <param name="json">The serialized answer.</param>
    /// <returns>The tool result.</returns>
    internal static CallToolResult Answer(string json) => new() { Content = [new TextContentBlock { Text = json }] };
    /// <summary>A tool error holding the error body.</summary>
    /// <param name="json">The serialized error body.</param>
    /// <returns>The tool result, marked as an error.</returns>
    internal static CallToolResult Failed(string json) => new() { Content = [new TextContentBlock { Text = json }], IsError = true };
    /// <summary>The tool error for an unexpected failure: the fixed error body, then <see cref="Guidance"/>.</summary>
    /// <remarks>Serialized with <see cref="GatewayJson.Default"/>, not the registered options, so a fault in the
    /// serialization that failed the answer cannot fail this reply too.</remarks>
    /// <param name="incidentId">The incident ID of the logged failure.</param>
    /// <returns>The tool result, marked as an error.</returns>
    internal static CallToolResult Unexpected(string incidentId) => new()
    {
        IsError = true,
        Content = [new TextContentBlock
        {
            Text = JsonSerializer.Serialize(ErrorEnvelope.From(ErrorEnvelope.Unexpected, incidentId).Body, GatewayJson.Default.Options)
        }, new TextContentBlock { Text = string.Format(Guidance, incidentId) }]
    };
}
