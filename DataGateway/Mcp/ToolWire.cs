using System.Text.Encodings.Web;
using System.Text.Json;
using DataGateway.Core.Errors;
using ModelContextProtocol.Protocol;

namespace DataGateway.Mcp;

internal static class ToolWire
{
    internal const string Guidance = "An unexpected error stopped DataGateway from completing this call. Do not retry it. Tell the user to report incident ID {0} to DataGateway's operator; the details are only in DataGateway's log.";

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

    internal static CallToolResult Answer(string json) => new() { Content = [new TextContentBlock { Text = json }] };
    internal static CallToolResult Failed(string json) => new() { Content = [new TextContentBlock { Text = json }], IsError = true };
    internal static CallToolResult Unexpected(string incidentId) => new()
    {
        IsError = true,
        Content = [new TextContentBlock
        {
            Text = JsonSerializer.Serialize(ErrorEnvelope.From(ErrorEnvelope.Unexpected, incidentId).Body, GatewayJson.Default.Options)
        }, new TextContentBlock { Text = string.Format(Guidance, incidentId) }]
    };
}
