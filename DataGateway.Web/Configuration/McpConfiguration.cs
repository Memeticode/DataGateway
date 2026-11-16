using System.Text;
using Microsoft.Extensions.Options;

namespace DataGateway.Web.Configuration;

internal static class McpConfiguration
{
    internal const string TitleSetting = "DataGateway:Mcp:Title";
    internal const string AboutSetting = "DataGateway:Mcp:About";

    internal static (string? Title, string? About) Read(IConfiguration configuration) =>
        (ReadLine(configuration, TitleSetting, 80), ReadLine(configuration, AboutSetting, 120));

    // Check bytes before trimming, preserving the executable's configuration refusal contract.
    private static string? ReadLine(IConfiguration configuration, string key, int maxBytes)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Contains('\n') || value.Contains('\r') || Encoding.UTF8.GetByteCount(value) > maxBytes)
            throw new OptionsValidationException(Options.DefaultName, typeof(McpConfiguration),
                [$"InvalidConfiguration: {key} must be one line of at most {maxBytes} bytes (UTF-8). Set it in appsettings, or leave it empty."]);
        return value.Trim();
    }
}
