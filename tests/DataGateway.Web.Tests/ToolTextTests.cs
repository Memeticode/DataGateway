using System.Text;
using DataGateway.Mcp;
using DataGateway.Core.Tests;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>The text a model reads from the MCP server: the server instructions, each tool's description and each
/// argument's description, and the query schema's rule texts.</summary>
/// <remarks>
/// <para>Each text has a byte budget, so it stays short enough for a model to read whole, and exact counts make any
/// change of wording deliberate. Each tool description opens with a short question that tells the model when to pick
/// that tool, and those openings are frozen.</para>
/// <para>The query grammar lives only in describe_source's querySchema; repeating it in query_source's text would
/// let the two copies disagree.</para>
/// </remarks>
public sealed class ToolTextTests
{
    /// <summary>Instructions, tool and argument texts stay within their budgets; each tool opens with a short
    /// question; query_source's text repeats none of the query grammar.</summary>
    [Fact]
    public void T19_model_text_budgets_openings_and_no_duplicated_query_grammar()
    {
        Assert.InRange(Encoding.UTF8.GetByteCount(DataGatewayTools.Instructions), 1, 500);
        Assert.StartsWith("A thin read-only layer over relational databases: ", DataGatewayTools.Instructions);
        Assert.Contains("list_sources", DataGatewayTools.Instructions);
        foreach (var tool in PublicGatewayProbe.Tools())
        {
            var definition = tool.ProtocolTool;
            var description = definition.Description!;
            Assert.InRange(Encoding.UTF8.GetByteCount(description), 1, 500);
            var opening = description.Split(". ", 2)[0] + ".";
            Assert.InRange(opening.Length, 1, 60);
            Assert.Contains("?", opening);
            if (definition.Name == "describe_source")
                Assert.StartsWith("Named source not yet described? Get schema and query rules.", description);
            foreach (var argument in definition.InputSchema.GetProperty("properties").EnumerateObject())
            {
                var text = argument.Value.GetProperty("description").GetString()!;
                Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, 200);
                if (definition.Name == "query_source" && argument.Name == "query") NoGrammar(text);
            }
            if (definition.Name == "query_source") NoGrammar(description);
        }
    }

    /// <summary>Exact byte counts for the instructions and the query argument, the first 60 characters of each tool
    /// description, and the budgets of every result hint and query schema rule.</summary>
    [Fact]
    public void T16_all_model_texts_fit_budgets_and_openings_are_frozen()
    {
        var openings = new Dictionary<string, string>
        {
            ["list_sources"] = "No source named? See all data sources and what each holds. T",
            ["describe_source"] = "Named source not yet described? Get schema and query rules. ",
            ["query_source"] = "Source already described? Run a read-only query, get rows. B"
        };
        Assert.Equal(482, Encoding.UTF8.GetByteCount(DataGatewayTools.Instructions));
        foreach (var hint in new[] { DataGateway.Core.Queries.QueryResponse.CappedAt(int.MaxValue), DataGateway.Core.Queries.QueryResponse.CappedAt(int.MaxValue) + " " + DataGateway.Core.Queries.QueryResponse.NoParticularOrder, DataGateway.Core.Queries.QueryResponse.NothingFound, DataGateway.Core.Queries.QueryResponse.CompleteSet })
            Assert.InRange(Encoding.UTF8.GetByteCount(hint), 1, 200);
        foreach (var tool in PublicGatewayProbe.Tools())
        {
            var definition = tool.ProtocolTool;
            Assert.InRange(Encoding.UTF8.GetByteCount(definition.Description!), 1, 500);
            Assert.Equal(openings[definition.Name], definition.Description![..60]);
            foreach (var argument in definition.InputSchema.GetProperty("properties").EnumerateObject())
            {
                var bytes = Encoding.UTF8.GetByteCount(argument.Value.GetProperty("description").GetString()!);
                Assert.InRange(bytes, 1, 200);
                if (argument.Name == "query") Assert.Equal(197, bytes);
            }
        }
        var schema = DataGateway.Core.Catalog.QuerySchema.For(ErrorLogTests.Contract(), DataGateway.Core.Catalog.QueryLimits.ForRowCap(500));
        foreach (var rule in schema.Filters!.Operators) Assert.InRange(Encoding.UTF8.GetByteCount(rule.Rule), 1, 200);
        foreach (var rule in schema.Aggregates!.Functions.Where(f => f.Rule is not null)) Assert.InRange(Encoding.UTF8.GetByteCount(rule.Rule!), 1, 200);
        foreach (var rule in new[] { schema.Select!.Rule, schema.Filters.Rule, schema.Filters.Values, schema.GroupBy!.Rule,
            schema.Aggregates.Rule, schema.OrderBy!.Rule, schema.Limit.Rule }) Assert.InRange(Encoding.UTF8.GetByteCount(rule), 1, 400);
    }


    /// <summary>list_sources and describe_source descriptions are frozen word for word, and both name rowCap, the
    /// per-source cap.</summary>
    [Fact]
    public void T22_per_source_cap_texts_have_exact_budgets()
    {
        var list_sources = Assert.Single(PublicGatewayProbe.Tools(), t => t.ProtocolTool.Name == "list_sources").ProtocolTool.Description!;
        Assert.Equal("""No source named? See all data sources and what each holds. Takes no arguments and returns {"sources":[...]}: each active source's name, title, description, grain (what one row represents) and rowCap (maximum rows per query). Pick the source whose description and grain fit the question, then call describe_source with its name.""", list_sources);
        Assert.Equal(327, Encoding.UTF8.GetByteCount(list_sources));
        Assert.Equal("""No source named? See all data sources and what each holds. T""", list_sources[..60]);
        Assert.Contains("rowCap", list_sources);
        var describe_source = Assert.Single(PublicGatewayProbe.Tools(), t => t.ProtocolTool.Name == "describe_source").ProtocolTool.Description!;
        Assert.Equal("""Named source not yet described? Get schema and query rules. Returns one source (name, title, description, grain, rowCap) with querySchema: every column with its meaning, type, unit, magnitude, display format and can* flags, then the rules of each query clause. Use only listed columns, as their flags allow. Before stating a number, read its unit and magnitude: "millions" means 523.06 is 523.06 million, for sum_, avg_, min_ and max_ outputs too; a count_ output is a plain count.""", describe_source);
        Assert.Equal(481, Encoding.UTF8.GetByteCount(describe_source));
        Assert.Equal("""Named source not yet described? Get schema and query rules. """, describe_source[..60]);
        Assert.Contains("rowCap", describe_source);
    }

    /// <summary>Asserts <paramref name="text"/> holds none of the query grammar's clause names or syntax.</summary>
    private static void NoGrammar(string text)
    {
        foreach (var word in new[] { "group_by", "order_by", "<fn>", "{col", "{fn", "nested", "count, sum" })
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>No product file outside the tests repeats a retired claim about the schema or about
    /// <c>more</c>.</summary>
    /// <remarks>Those phrases described earlier behaviour; finding one means a document still promises
    /// it.</remarks>
    [Fact]
    public void T21_product_text_has_no_stale_schema_or_more_claims()
    {
        var root = TestFiles.InRepo(".");
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar).Any(part => part is "tests" or "bin" or "obj")))
        {
            var text = File.ReadAllText(file);
            foreach (var stale in new[] { "exact JSON Schema", "exact querySchema", "exact authority", "does not probe", "`more` is `false`", "set a smaller limit" })
                Assert.False(text.Contains(stale, StringComparison.Ordinal), Path.GetRelativePath(root, file) + ": " + stale);
        }
    }
}
