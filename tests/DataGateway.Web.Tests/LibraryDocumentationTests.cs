using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataGateway.Core.Tests;
using GatewaySample;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class LibraryDocumentationTests
{
    [Fact]
    public async Task T1_fences_compile_and_the_sample_serves_list_describe_and_query()
    {
        var documents = new[] { "README.md", "CONFIGURATION.md", "CATALOG-READERS.md", "ENGINES.md",
            "DataGateway.Web/README.md", "DataGateway.Web/CONFIGURATION.md", "DataGateway.Data.Sqlite/README.md" };
        var utf8 = new UTF8Encoding(false, true);
        var source = utf8.GetString(File.ReadAllBytes(TestFiles.InRepo("tests/DataGateway.Web.Tests/LibraryQuickstart.cs")));
        var regions = Regex.Matches(source, @"(?ms)^#region (\w+)\n(.*?)^#endregion(?:\n|$)");
        Assert.Equal(3, regions.Count);
        Assert.Equal(3, Regex.Matches(source, @"(?m)^#region\b").Count);
        Assert.Equal(3, Regex.Matches(source, @"(?m)^#endregion\b").Count);
        Assert.Equal(new[] { "Catalog", "Database", "Program" }, regions.Select(r => r.Groups[1].Value).Order());
        var payloads = regions.ToDictionary(r => r.Groups[1].Value, r => utf8.GetBytes(r.Groups[2].Value));
        var expected = new[] { "README.md:Program", "README.md:Catalog", "README.md:Database", "CATALOG-READERS.md:Catalog" }.Order().ToArray();
        var bindings = new List<string>();
        foreach (var document in documents)
        {
            var text = utf8.GetString(File.ReadAllBytes(TestFiles.InRepo(document)));
            var markers = Regex.Matches(text, @"(?im)^.*<!--\s*sample\b.*$");
            var found = 0;
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var prefixed = Regex.Match(lines[i], @"^(?:[ \t>]|[-+*][ \t]|\d{1,9}[.)][ \t])+(`{3,}|~{3,})(.*)$");
                var opening = Regex.Match(lines[i], @"^\s*(`{3,}|~{3,})([^\r\n]*)\r?$");
                var candidate = opening.Success ? opening : prefixed;
                if (!candidate.Success) continue;
                var info = candidate.Groups[2].Value.Trim();
                var isCSharp = Regex.IsMatch(info, @"(?i)(?<![a-z])(csharp|c#|cs)(?![a-z])");
                Assert.False(prefixed.Success && isCSharp,
                    "C# fence not at column 0 (indented or inside a container) in " + document);
                if (!opening.Success) continue;
                var delimiter = opening.Groups[1].Value;
                var start = i + 1;
                while (++i < lines.Length && !Regex.IsMatch(lines[i], @"^\s*" + Regex.Escape(delimiter[0].ToString()) + "{" + delimiter.Length + @",}\s*$")) { }
                Assert.True(i < lines.Length, "Unclosed fence in " + document);
                if (!isCSharp) continue;
                Assert.Equal("```csharp", lines[start - 1]);
                Assert.Equal("```", lines[i]);
                Assert.True(start >= 2, "Missing sample marker in " + document);
                var marker = Regex.Match(lines[start - 2], @"^<!-- sample region=(Program|Catalog|Database) -->$");
                Assert.True(marker.Success, "Missing canonical sample marker in " + document);
                var region = marker.Groups[1].Value;
                var binding = document + ":" + region;
                Assert.DoesNotContain(binding, bindings);
                bindings.Add(binding);
                found++;
                var payload = utf8.GetBytes(string.Join("\n", lines[start..i]) + "\n");
                Assert.True(payloads[region].SequenceEqual(payload), "Raw UTF-8 sample bytes differ: " + binding);
            }
            Assert.Equal(markers.Count, found);
        }
        Assert.Equal(expected, bindings.Order());

        var library = ReadmeTests.LibraryMarkdown;
        var host = ReadmeTests.Markdown;
        var libraryExamples = ReadmeExamples.Parse(library);
        var hostExamples = ReadmeExamples.Parse(host);
        Assert.Equal(7, libraryExamples.Count);
        Assert.Equal(7, hostExamples.Count);
        for (var i = 0; i < hostExamples.Count; i++)
        {
            var left = hostExamples[i];
            var right = libraryExamples[i];
            Assert.Equal(left.Name, right.Name);
            Assert.Equal(left.Route, right.Route);
            Assert.Equal(left.Request, right.Request);
            Assert.Equal(left.Status, right.Status);
            Assert.Equal(left.Anonymous, right.Anonymous);
            Assert.Equal(left.Answer?.GetRawText(), right.Answer?.GetRawText());
        }
        string[] MarkedBlocks(string text) => Regex.Matches(text.Replace("\r", ""),
            @"(?ms)^<!-- example .*? -->\n```json\n.*?\n```$").Select(m => m.Value).ToArray();
        Assert.Equal(MarkedBlocks(host), MarkedBlocks(library));
        Assert.Equal(new[] { "$.query" }, ReadmeExamples.ErrorTable(library).Single(row => row.Code == "QueryTimeout").Paths);
        var mcpStart = library.IndexOf("## MCP", StringComparison.Ordinal);
        Assert.True(mcpStart >= 0);
        var mcpEnd = library.IndexOf("\n## ", mcpStart + 1, StringComparison.Ordinal);
        var mcp = mcpEnd < 0 ? library[mcpStart..] : library[mcpStart..mcpEnd];
        foreach (var term in new[] { "notifications/cancelled", "QueryCancelled", "CatalogCancelled", "query_timeout_seconds", "source's catalog value" })
            Assert.Contains(term, mcp);
        Assert.Contains("Apache License 2.0: [LICENSE](LICENSE), [NOTICE](NOTICE).", library);
        Assert.Contains(ReadmeTests.Attribution, library);

        var directory = Path.Combine(Path.GetTempPath(), "gateway-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "sample.db");
            using var seedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await SampleDatabase.CreateAsync(file, seedTimeout.Token);
            var app = SampleProgram.CreateApplication(["--environment", "Production", "--contentRoot", directory,
                "--urls", "http://127.0.0.1:0"], file);
            try
            {
                using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await app.StartAsync(startupTimeout.Token);
                using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)), Timeout = TimeSpan.FromSeconds(20) };
                async Task<string> Post(string route, string body)
                {
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync(route, content);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
                    return await response.Content.ReadAsStringAsync();
                }
                const string metadata = """{"name":"sample_items","title":"Sample items","description":"Three invented items","grain":"One item","rowCap":2}""";
                Assert.Equal("{\"sources\":[" + metadata + "]}", await Post("/sources/list", "{}"));
                using var describe = JsonDocument.Parse(await Post("/sources/describe", """{"source":"sample_items"}"""));
                Assert.Equal(metadata, describe.RootElement.GetProperty("source").GetRawText());
                var schema = describe.RootElement.GetProperty("querySchema");
                var column = Assert.Single(schema.GetProperty("columns").EnumerateArray());
                Assert.Equal("""{"name":"id","title":"Id","description":"Item identifier","dataType":"number","unit":null,"magnitude":null,"displayFormat":null,"canSelect":true,"canFilter":true,"canSort":true,"canGroup":true,"canAggregate":true}""", column.GetRawText());
                Assert.Equal(2, schema.GetProperty("limit").GetProperty("maximum").GetInt32());
                Assert.Equal("""{"source":"sample_items","columns":[{"name":"id","dataType":"number"}],"rows":[{"id":1},{"id":2}],"rowCount":2,"more":true,"hint":"The result set was capped at 2 rows."}""",
                    await Post("/query", """{"source":"sample_items","query":{"select":["id"],"order_by":[{"col":"id","dir":"asc"}],"limit":2}}"""));
            }
            finally
            {
                try
                {
                    using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await app.StopAsync(shutdownTimeout.Token);
                }
                finally { await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)); }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
