using DataGateway.Core.Tests;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>Pins where each step of the three operations (list, describe, query) lives in the gateway's source.</summary>
/// <remarks>Each step (read the catalog, parse the request, validate, compile, run, map the answer) must appear in
/// exactly one operation file, so the HTTP routes and the MCP tools share one flow instead of each keeping a copy that
/// can drift. For the same reason, routes and tools parse no JSON themselves.</remarks>
public sealed class OperationsTests
{
    /// <summary>Each step's call text is found only in its expected file, and no route or MCP tool parses JSON
    /// itself.</summary>
    [Fact]
    public void T14_operation_flow_has_one_home()
    {
        var files = new[] { "DataGateway.Web", "DataGateway" }
            .SelectMany(project => Directory.GetFiles(TestFiles.InRepo(project), "*.cs", SearchOption.AllDirectories))
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .ToDictionary(p => Path.GetRelativePath(TestFiles.RepoRoot, p).Replace('\\', '/'), File.ReadAllText);
        foreach (var (text, expected) in new Dictionary<string, string[]>
        {
            ["readList(token)"] = ["ListSources.cs"],
            ["readSource(request.Value, token)"] = ["DescribeSource.cs"],
            ["readTarget(request.Value.Source, token)"] = ["QuerySource.cs"],
            ["ReadSourceAsync("] = ["../DataGateway.Web/Configuration/HostDatabaseEngine.cs"],
            ["QueryBuilding.Validate("] = ["QuerySource.cs"], ["runner.Compile("] = ["QuerySource.cs"],
            ["runner.RunAsync("] = ["QuerySource.cs"], ["QueryAnswer.Map("] = ["QuerySource.cs"],
            ["SourceListing.Map("] = ["ListSources.cs"], ["SourceDescription.Map("] = ["DescribeSource.cs"],
            ["QueryAnswer.ParseRequest("] = ["QuerySource.cs"], ["SourceDescription.ParseRequest("] = ["DescribeSource.cs"],
            ["SourceListing.CheckRequest("] = ["ListSources.cs"]
        })
            Assert.Equal(expected.Select(p => p.StartsWith("../", StringComparison.Ordinal) ? p[3..] : "DataGateway/Operations/" + p).Order(), files.Where(p => p.Value.Contains(text, StringComparison.Ordinal)).Select(p => p.Key).Order());
        Assert.DoesNotContain(files, p => (p.Key.Contains("/Routes/", StringComparison.Ordinal) || p.Key.Contains("/Mcp/", StringComparison.Ordinal)) && p.Value.Contains("JsonDocument.Parse(", StringComparison.Ordinal));
    }
}
