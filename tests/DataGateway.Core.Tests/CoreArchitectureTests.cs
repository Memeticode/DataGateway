using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace DataGateway.Core.Tests;

/// <summary>Keeps DataGateway.Core pure: its catalog, validation and query rules reference no database driver, data
/// access, logging, web host, MCP or API-page package.</summary>
/// <remarks>The check reads the restore graph (<c>obj/project.assets.json</c>) and the project files rather than the
/// loaded assemblies, so a reference that is declared but not yet used still fails the test.</remarks>
public class CoreArchitectureTests
{
    /// <summary>No library in Core's restore graph, no framework reference and no explicit assembly reference, in Core
    /// or in any project it references, is forbidden by <see cref="IsForbidden"/>.</summary>
    [Fact]
    public void Core_has_no_direct_or_transitive_io_references()
    {
        var coreProject = TestFiles.InRepo("DataGateway.Core/DataGateway.Core.csproj");
        var violations = new SortedSet<string>(StringComparer.Ordinal);
        using var assets = ReadAssets(coreProject);
        var libraries = assets.RootElement.GetProperty("libraries");

        // Restore includes unused references too; reflection on loaded assemblies would miss them.
        foreach (var library in libraries.EnumerateObject())
        {
            var name = library.Name.Split('/')[0];
            Check(name, "Core dependency graph", violations);
            if (library.Value.GetProperty("type").GetString() == "project")
            {
                var project = TestFiles.InRepo(Path.Combine(Path.GetDirectoryName(coreProject)!,
                    library.Value.GetProperty("msbuildProject").GetString()!));
                CheckProject(project, violations);
            }
        }

        CheckProject(coreProject, violations);
        Assert.True(violations.Count == 0,
            "DataGateway.Core must stay pure. Forbidden references: " + string.Join("; ", violations));
    }

    /// <summary>Adds a project's forbidden framework references and explicit assembly references to
    /// <paramref name="violations"/>.</summary>
    /// <param name="project">The project file to check.</param>
    /// <param name="violations">Collects each forbidden name with the file it came from.</param>
    private static void CheckProject(string project, ISet<string> violations)
    {
        using var assets = ReadAssets(project);
        foreach (var framework in assets.RootElement.GetProperty("project")
                     .GetProperty("frameworks").EnumerateObject())
        {
            if (framework.Value.TryGetProperty("frameworkReferences", out var references))
            {
                foreach (var reference in references.EnumerateObject())
                {
                    Check(reference.Name, Path.GetFileName(project), violations);
                }
            }
        }

        // Also guard explicit assembly references, which are not NuGet libraries.
        foreach (var reference in XDocument.Load(project).Descendants()
                     .Where(element => element.Name.LocalName == "Reference"))
        {
            var name = ((string?)reference.Attribute("Include") ?? "").Split(',')[0].Trim();
            Check(name, Path.GetFileName(project), violations);
        }
    }

    /// <summary>Reads the restore output that sits beside a project; the caller disposes it.</summary>
    private static JsonDocument ReadAssets(string project) =>
        JsonDocument.Parse(File.ReadAllBytes(TestFiles.InRepo(
            Path.Combine(Path.GetDirectoryName(project)!, "obj/project.assets.json"))));

    /// <summary>Records <paramref name="name"/> with its origin when it is forbidden.</summary>
    private static void Check(string name, string origin, ISet<string> violations)
    {
        if (IsForbidden(name))
        {
            violations.Add($"{name} ({origin})");
        }
    }
    /// <summary>The packages and projects Core must never reference: DataGateway's data layer, MCP, logging, ASP.NET
    /// Core, the SQL Server driver, Dapper, and the API page's packages.</summary>
    /// <remarks>Names match without regard to case and by package prefix, so a new package in a forbidden family is
    /// caught; <see cref="Forbidden_names_are_pinned"/> pins the edges, such as a lookalike name that must pass.</remarks>
    internal static bool IsForbidden(string name) =>
            name.Equals("DataGateway.Data", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("DataGateway.Data.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ModelContextProtocol", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Serilog", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Microsoft.Extensions.Logging", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Microsoft.Data.SqlClient", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Microsoft.Data.SqlClient.", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Dapper", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Dapper.Data", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Scalar.", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Microsoft.OpenApi", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Microsoft.OpenApi.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pins which names <see cref="IsForbidden"/> catches, including lookalikes (a longer word that only starts
    /// like a forbidden prefix) that must pass and the query builder Core is allowed to use.</summary>
    [Theory]
    [InlineData("ModelContextProtocol", true)]
    [InlineData("ModelContextProtocol.Core", true)]
    [InlineData("ModelContextProtocol.AspNetCore", true)]
    [InlineData("Scalar.AspNetCore", true)]
    [InlineData("scalar.aspnetcore", true)]
    [InlineData("Microsoft.OpenApi", true)]
    [InlineData("Microsoft.OpenApi.YamlReader", true)]
    [InlineData("Microsoft.AspNetCore.OpenApi", true)]
    [InlineData("Serilog", true)]
    [InlineData("Dapper", true)]
    [InlineData("Dapper.Data", true)]
    [InlineData("Microsoft.Data.SqlClient", true)]
    [InlineData("DataGateway.Data", true)]
    [InlineData("DataGateway.Data.SqlServer", true)]
    [InlineData("datagateway.data.synthetic", true)]
    [InlineData("DataGateway.Database", false)]
    [InlineData("SqlKata", false)]
    [InlineData("SqlKata.JsonQuery", false)]
    [InlineData("System.Text.Json", false)]
    [InlineData("Microsoft.OpenApiX", false)]
    [InlineData("ScalarMath", false)]
    public void Forbidden_names_are_pinned(string name, bool expected) => Assert.Equal(expected, IsForbidden(name));

}
