using DataGateway.Data;

namespace DataGateway.Failures;

/// <summary>Writes store-type findings (<see cref="StoreTypeFinding"/>) to their own log category.</summary>
/// <remarks>
/// <para>FailureLog has no path for these: every line it writes is a request failure with an incident ID returned to the
/// caller, and a store-type finding is neither (the query still succeeds, binding as before). So findings go to plain
/// ILogger lines under <see cref="Category"/>, at Error or Warning as the engine judged them. The engine reports each
/// problem once per process, so no deduplication happens here.</para>
/// <para>A logging provider that throws never changes the query: the finding is told on standard error instead.</para>
/// </remarks>
internal sealed class StoreTypeLog(ILoggerFactory loggers)
{
    internal const string Category = "DataGateway.StoreType";
    internal const string Template = "Store type {Severity} for source {SourceName}, column {ColumnName}: {Message}";
    private readonly ILogger logger = loggers.CreateLogger(Category);

    internal void Report(StoreTypeFinding finding)
    {
        try
        {
            logger.Log(finding.Severity == StoreTypeSeverity.Error ? LogLevel.Error : LogLevel.Warning, Template,
                finding.Severity, finding.SourceName, finding.ColumnName, finding.Message);
        }
        catch (Exception unwritten)
        {
            try { Console.Error.WriteLine($"DataGateway.StoreType: a finding could not be logged ({unwritten.GetType().FullName}): {finding.Message}"); }
            catch (Exception) { }
        }
    }
}
