using DataGateway.Core.Tests;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class OperationsTests
{
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
