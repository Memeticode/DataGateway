using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Debugging;
using Serilog.Extensions.Logging;

namespace DataGateway.Web.Configuration;

internal static class DataGatewayLog
{
    internal const string MissingSink = "InvalidConfiguration: Serilog:WriteTo names no sink, so failures would not be logged. Start DataGateway from its folder, whose appsettings.json declares the File and Console sinks, or set Serilog:WriteTo in configuration.";

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
