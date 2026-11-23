using DataGateway.Core.Catalog;
using DataGateway.Failures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>Pins the failure log's last resort: when every logger and standard error itself throw, a failure still
/// gets an incident id.</summary>
/// <remarks>The incident id goes into the error reply; a broken log sink must not turn an expected failure into an
/// unexpected one. In the console-capture collection because it replaces standard error.</remarks>
[Collection("Console capture")]
public sealed class LastResortReportTests
{
    /// <summary>A logging scope that cannot begin is reported as none, and writing the failure line returns a
    /// well-formed incident id although the line and the standard-error notice both fail.</summary>
    [Fact]
    public void T1_a_failing_log_and_a_failing_standard_error_still_give_an_incident_and_no_scope()
    {
        var saved = Console.Error;
        Console.SetError(new ThrowingWriter());
        try
        {
            using var loggers = LoggerFactory.Create(builder => builder.AddProvider(new BrokenProvider()));
            var log = new FailureLog(loggers, QueryLimits.ForRowCap(500));
            Assert.Null(log.BeginGatewayScope("a"));
            var incident = log.Write(new System.Security.Claims.ClaimsPrincipal(), false, "list", null,
                new CatalogFailure("SourceUnavailable", "$.source", "synthetic"), new RequestTrail());
            Assert.Matches("^[0-9a-f]{32}$", incident);
        }
        finally { Console.SetError(saved); }
    }

    /// <summary>A standard error that throws on every line.</summary>
    private sealed class ThrowingWriter : StringWriter
    {
        /// <summary>Throws, as a closed or broken standard error would.</summary>
        public override void WriteLine(string? value) => throw new IOException("synthetic standard error is down");
    }

    /// <summary>A logger provider whose loggers throw on every scope and every write.</summary>
    private sealed class BrokenProvider : ILoggerProvider
    {
        /// <summary>Creates a broken logger.</summary>
        public ILogger CreateLogger(string categoryName) => new Broken();
        /// <summary>Nothing to release.</summary>
        public void Dispose() { }

        /// <summary>A logger that throws on every scope and every write.</summary>
        private sealed class Broken : ILogger
        {
            /// <summary>Throws.</summary>
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => throw new IOException("synthetic sink is down");
            /// <summary>Always true, so the write is attempted.</summary>
            public bool IsEnabled(LogLevel logLevel) => true;
            /// <summary>Throws.</summary>
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                throw new IOException("synthetic sink is down");
        }
    }
}
