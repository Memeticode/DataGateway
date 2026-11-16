using DataGateway.Core.Catalog;
using DataGateway.Failures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataGateway.Web.Tests;

[Collection("Console capture")]
public sealed class LastResortReportTests
{
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

    private sealed class ThrowingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("synthetic standard error is down");
    }

    private sealed class BrokenProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Broken();
        public void Dispose() { }

        private sealed class Broken : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => throw new IOException("synthetic sink is down");
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                throw new IOException("synthetic sink is down");
        }
    }
}
