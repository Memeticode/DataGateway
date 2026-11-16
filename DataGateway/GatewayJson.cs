using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DataGateway;

/// <summary>The JSON options for every DataGateway reply: HTTP and MCP, answer and error.</summary>
/// <remarks>The library owns them, so a host's own JSON settings (naming policy, dictionary keys, ignored nulls,
/// converters) never change the wire contract that the tool descriptions and the API document promise.
/// AddDataGateway registers the default instance; tests register their own to observe or fault serialization.</remarks>
internal sealed class GatewayJson
{
    internal static GatewayJson Default { get; } = new();

    internal GatewayJson(params JsonConverter[] converters)
    {
        // ASP.NET Core's own defaults for HTTP JSON, which the wire was built and frozen on: web naming, and the relaxed
        // encoder because replies are written to the response, never embedded in a page.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        foreach (var converter in converters) options.Converters.Add(converter);
        options.MakeReadOnly();
        Options = options;
    }

    internal JsonSerializerOptions Options { get; }

    internal static JsonSerializerOptions Of(IServiceProvider services) => (services.GetService<GatewayJson>() ?? Default).Options;
}
