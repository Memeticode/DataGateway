using DataGateway.Core.Catalog;

namespace DataGateway.Web.Tests;

/// <summary>The MCP metadata the test catalogs return unless a test sets its own: a server title, an about text and
/// the presentation text of the three tools.</summary>
internal static class McpMetadataFixture
{
    /// <summary>A fresh copy of the seed metadata, so a test can change it with <c>with</c> without touching others.</summary>
    internal static McpMetadata Seed => new("DataGateway", "Read-only access to cataloged data sources.",
    [
        new("list_sources", "List sources", """No source named? See all data sources and what each holds. Takes no arguments and returns {"sources":[...]}: each active source's name, title, description, grain (what one row represents) and rowCap (maximum rows per query). Pick the source whose description and grain fit the question, then call describe_source with its name."""),
        new("describe_source", "Describe a source", """Named source not yet described? Get schema and query rules. Returns one source (name, title, description, grain, rowCap) with querySchema: every column with its meaning, type, unit, magnitude, display format and can* flags, then the rules of each query clause. Use only listed columns, as their flags allow. Before stating a number, read its unit and magnitude: "millions" means 523.06 is 523.06 million, for sum_, avg_, min_ and max_ outputs too; a count_ output is a plain count."""),
        new("query_source", "Query a source", """Source already described? Run a read-only query, get rows. Build query from that source's querySchema (describe_source). Returns {source, columns, rows, rowCount, more, hint}. Prefer filters and aggregates over pulling raw rows. With or without limit, more is true exactly when rows were left out: never report that answer as complete. Narrow or aggregate; there is no way to ask for the rest. Empty rows, or an aggregate's 0 or null, can mean no row matched or a misspelled value."""),
    ]);
}
