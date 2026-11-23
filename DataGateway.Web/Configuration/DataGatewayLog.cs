using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Debugging;
using Serilog.Extensions.Logging;

namespace DataGateway.Web.Configuration;

/// <summary>Makes Serilog the host's only logging provider, configured from the <c>Serilog</c> section.</summary>
/// <remarks>The library logs through <see cref="ILoggerFactory"/> only; choosing Serilog, its sinks and its file
/// format is this host's decision. The host refuses to start without a sink, because failures would then go
/// unlogged and an incident id given to a caller would lead nowhere.</remarks>
internal static class DataGatewayLog
{
    /// <summary>The startup refusal when no sink is configured, with the usual cause: starting the program from a
    /// folder without its <c>appsettings.json</c>.</summary>
    internal const string MissingSink = "InvalidConfiguration: Serilog:WriteTo names no sink, so failures would not be logged. Start DataGateway from its folder, whose appsettings.json declares the File and Console sinks, or set Serilog:WriteTo in configuration.";

    /// <summary>Replaces the default providers with one Serilog provider built from the configuration when the
    /// service provider first needs it.</summary>
    /// <remarks>The <c>Logging</c> filter rules are cleared and replaced with one that passes everything to Serilog,
    /// so <c>Serilog:MinimumLevel</c> is the only level setting. A <c>Logging</c> rule, provider-specific ones
    /// included, would otherwise drop events before Serilog sees them.</remarks>
    /// <param name="builder">The host's builder.</param>
    internal static void Use(WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ILoggerProvider>(services =>
            new SerilogLoggerProvider(Create(services.GetRequiredService<IConfiguration>()), dispose: true));
        // Serilog alone sets levels, including after a configuration reload and provider-specific Logging rules.
        builder.Services.PostConfigure<LoggerFilterOptions>(o =>
        {
            o.Rules.Clear();
            o.Rules.Add(new LoggerFilterRule(typeof(SerilogLoggerProvider).FullName, null, LogLevel.Trace, null));
        });
    }

    /// <summary>Builds the Serilog logger from configuration, refusing a configuration with no sink.</summary>
    /// <remarks>
    /// <para>Serilog's self-log goes to stderr, so a sink that cannot write (an unwritable log file) is reported
    /// while requests still answer.</para>
    /// <para>The failure category, <see cref="DataGatewayExtensions.FailureLogCategory"/>, is pinned to Warning
    /// after the configuration is read, so no category override can suppress a failure line.</para>
    /// </remarks>
    /// <param name="configuration">The configuration holding the <c>Serilog</c> section.</param>
    /// <returns>The logger, owned by the provider that disposes it.</returns>
    /// <exception cref="OptionsValidationException"><c>Serilog:WriteTo</c> names no sink.</exception>
    internal static Serilog.Core.Logger Create(IConfiguration configuration)
    {
        if (!configuration.GetSection("Serilog:WriteTo").GetChildren().Any())
            throw new OptionsValidationException(Options.DefaultName, typeof(DataGatewayLog), [MissingSink]);
        SelfLog.Enable(message => Console.Error.WriteLine(message));
        return new LoggerConfiguration().ReadFrom.Configuration(configuration)
            .MinimumLevel.Override(DataGatewayExtensions.FailureLogCategory, Serilog.Events.LogEventLevel.Warning)
            .CreateLogger();
    }
}
