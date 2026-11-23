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
    /// <summary>The options every gateway uses unless a test registers its own.</summary>
    internal static GatewayJson Default { get; } = new();

    /// <summary>Builds the frozen wire options, with extra converters added last.</summary>
    /// <remarks>The converters exist for tests, which add one to observe or fault the serialization of a reply.</remarks>
    /// <param name="converters">Converters added after the defaults; none in production.</param>
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

    /// <summary>The read-only serializer options for replies.</summary>
    internal JsonSerializerOptions Options { get; }

    /// <summary>The options registered in <paramref name="services"/>, or <see cref="Default"/> when none are.</summary>
    /// <param name="services">The request's or the call's services.</param>
    /// <returns>The options to serialize the reply with.</returns>
    internal static JsonSerializerOptions Of(IServiceProvider services) => (services.GetService<GatewayJson>() ?? Default).Options;
}
