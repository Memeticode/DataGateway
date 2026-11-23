using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using DataGateway.Core.Tests;
using DataGateway.Data;
using DataGateway.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace DataGateway.Web.Tests;

/// <summary>Where the tests-only SQLite engine may appear: its project depends only on the data layer and one pinned
/// SQLite package, no product project references it, and no product restore graph contains SQLite.</summary>
/// <remarks>The engine exists to prove that a new database fits through the public engine contract alone. If it used
/// internals, reflection or another engine, or if a product picked it up, it would prove nothing and could ship by
/// accident.</remarks>
public sealed class SqliteArchitectureTests(ITestOutputHelper output)
{
    /// <summary>True when a project, package or file name mentions SQLite in any case.</summary>
    private static bool SqliteName(string name) =>
        name.Contains("sqlite", StringComparison.OrdinalIgnoreCase);

    /// <summary>The engine implements exactly the five public engine members; its project has one project reference,
    /// one exactly pinned package and nothing publishable or packable; only tests and samples reference it; and its
    /// source uses no internals, reflection, SQL Server types, Dapper or stub exceptions.</summary>
    [Fact]
    public void T1_project_seam_solution_and_incoming_reference_boundary()
    {
        Assert.True(typeof(SqliteEngine).IsSealed);
        Assert.True(typeof(IDatabaseEngine).IsAssignableFrom(typeof(SqliteEngine)));
        Assert.Equal(new[] { "Compile", "ReadSourceAsync", "RecognizeFailure", "ValidateSourceConnection", "get_ProviderName" },
            typeof(IDatabaseEngine).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal));
        var project = XDocument.Load(TestFiles.InRepo("DataGateway.Data.Sqlite/DataGateway.Data.Sqlite.csproj"));
        Assert.Equal("../DataGateway.Data/DataGateway.Data.csproj", (string?)Assert.Single(project.Descendants("ProjectReference")).Attribute("Include"));
        var package = Assert.Single(project.Descendants("PackageReference"));
        Assert.Equal("Microsoft.Data.Sqlite", (string?)package.Attribute("Include"));
        Assert.Equal("[10.0.13]", (string?)package.Attribute("Version"));
        Assert.Equal("false", Assert.Single(project.Descendants("IsPublishable")).Value);
        Assert.Equal("false", Assert.Single(project.Descendants("IsPackable")).Value);
        Assert.Empty(project.Descendants("Compile"));
        Assert.Empty(project.Descendants("Reference"));
        var solution = XDocument.Load(TestFiles.InRepo("DataGateway.slnx"));
        Assert.Single(solution.Descendants("Project"), node => (string?)node.Attribute("Path") == "DataGateway.Data.Sqlite/DataGateway.Data.Sqlite.csproj");
        var incoming = new List<string>();
        foreach (var path in Directory.EnumerateFiles(TestFiles.RepoRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(path);
            foreach (var reference in document.Descendants("ProjectReference"))
                if (SqliteName((string?)reference.Attribute("Include") ?? "")) incoming.Add(Path.GetRelativePath(TestFiles.RepoRoot, path).Replace('\\', '/'));
        }
        // The tests and example apps under samples/ may use the tests-only adapter; no product project may.
        Assert.Contains("tests/DataGateway.Web.Tests/DataGateway.Web.Tests.csproj", incoming);
        Assert.All(incoming, path => Assert.True(path == "tests/DataGateway.Web.Tests/DataGateway.Web.Tests.csproj" ||
            path.StartsWith("samples/", StringComparison.Ordinal), "SQLite adapter referenced by " + path));
        var assembly = typeof(SqliteEngine).Assembly;
        Assert.DoesNotContain(assembly.GetCustomAttributesData(), attr => attr.AttributeType.Name == "InternalsVisibleToAttribute");
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name => new[] { "SqlServer", "SqlClient", "Dapper", "EntityFramework", "SqlKata.Execution" }.Any(fragment => name.Name!.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
        foreach (var path in Directory.EnumerateFiles(TestFiles.InRepo("DataGateway.Data.Sqlite"), "*.cs"))
        {
            var text = File.ReadAllText(path);
            foreach (var fragment in new[] { "InternalsVisibleTo", "System.Reflection", "BindingFlags", "SqlServerEngine", "Microsoft.Data.SqlClient", "using Dapper", "EntityFramework", "SqlKata.Execution", "NotImplementedException", "NotSupportedException" })
                Assert.DoesNotContain(fragment, text);
        }
    }

    /// <summary>No product project's restore graph contains a SQLite package, and the lower layers carry no SQL Server
    /// driver and only the pinned Dapper packages the data layer uses.</summary>
    /// <remarks>Reads the restore output, so it needs a restore first and fails with that message otherwise.</remarks>
    [Fact]
    public void T1_every_product_restored_target_and_library_excludes_sqlite()
    {
        foreach (var layer in new[] { "DataGateway.Core", "DataGateway.Data", "DataGateway", "DataGateway.Data.SqlServer", "DataGateway.Web", "DataGateway.Demo", "DataGateway.Proxy" })
        {
            var path = TestFiles.InRepo(layer + "/obj/project.assets.json");
            Assert.True(File.Exists(path), "Restore product assets before testing.");
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            Assert.NotEmpty(doc.RootElement.GetProperty("targets").EnumerateObject());
            foreach (var target in doc.RootElement.GetProperty("targets").EnumerateObject())
            {
                Assert.DoesNotContain(target.Value.EnumerateObject(), entry => SqliteName(entry.Name));
            }
            Assert.DoesNotContain(doc.RootElement.GetProperty("libraries").EnumerateObject(), entry => SqliteName(entry.Name));
            if (layer is "DataGateway.Core" or "DataGateway.Data" or "DataGateway")
                Assert.DoesNotContain(doc.RootElement.GetProperty("libraries").EnumerateObject(),
                    entry => entry.Name.Contains("SqlClient", StringComparison.OrdinalIgnoreCase) ||
                        (entry.Name.StartsWith("Dapper", StringComparison.OrdinalIgnoreCase) &&
                         !((layer is "DataGateway.Data" or "DataGateway") &&
                           (entry.Name is "Dapper/2.1.89" or "Dapper.Data/3.1.2"))));
        }
        Assert.True(SqliteName("mIcRoSoFt.DaTa.SqLiTe/10.0.13"));
        Assert.True(SqliteName("sQLitePCLRaw.core"));
        Assert.True(SqliteName("DataGateway.Data.SQLITE"));
        Assert.True(SqliteName("e_sqlite3"));
        Assert.False(SqliteName("SqlKata"));
        Assert.False(SqliteName("System.Memory"));
    }

    /// <summary>The engine's lock file pins the SQLite package exactly, its graph holds no other engine or ORM, and the
    /// native SQLite library loads for this runtime and answers a query.</summary>
    /// <remarks>The smoke query catches a missing or suppressed native asset, which compiles fine and fails only at
    /// run time.</remarks>
    [Fact]
    public async Task T11_locked_graph_and_unsuppressed_native_provider_smoke()
    {
        using var lockFile = JsonDocument.Parse(File.ReadAllBytes(TestFiles.InRepo("DataGateway.Data.Sqlite/packages.lock.json")));
        var dependencies = lockFile.RootElement.GetProperty("dependencies").GetProperty("net10.0");
        Assert.Equal("10.0.13", dependencies.GetProperty("Microsoft.Data.Sqlite").GetProperty("resolved").GetString());
        Assert.Equal("[10.0.13, 10.0.13]", dependencies.GetProperty("Microsoft.Data.Sqlite").GetProperty("requested").GetString());
        using var assets = JsonDocument.Parse(File.ReadAllBytes(TestFiles.InRepo("DataGateway.Data.Sqlite/obj/project.assets.json")));
        foreach (var target in assets.RootElement.GetProperty("targets").EnumerateObject())
            foreach (var entry in target.Value.EnumerateObject())
            {
                Assert.DoesNotContain("SqlServer", entry.Name);
                Assert.False(entry.Name.Contains("Dapper", StringComparison.OrdinalIgnoreCase) &&
                    entry.Name is not ("Dapper/2.1.89" or "Dapper.Data/3.1.2"));
                Assert.DoesNotContain("EntityFramework", entry.Name);
                Assert.DoesNotContain("SqlKata.Execution", entry.Name);
            }
        foreach (var entry in assets.RootElement.GetProperty("libraries").EnumerateObject())
        {
            Assert.DoesNotContain(new[] { "SqlServer", "EntityFramework", "SqlKata.Execution" },
                fragment => entry.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            Assert.False(entry.Name.Contains("Dapper", StringComparison.OrdinalIgnoreCase) &&
                entry.Name is not ("Dapper/2.1.89" or "Dapper.Data/3.1.2"));
        }
        Assert.Equal("3.1.2", dependencies.GetProperty("Dapper.Data").GetProperty("resolved").GetString());
        Assert.Equal("Transitive", dependencies.GetProperty("Dapper.Data").GetProperty("type").GetString());
        Assert.Equal("2.1.89", dependencies.GetProperty("Dapper").GetProperty("resolved").GetString());
        Assert.Equal("Transitive", dependencies.GetProperty("Dapper").GetProperty("type").GetString());
        using var fixture = new SqliteFixture();
        var result = await SqliteEngineTests.Read(new SqliteEngine(), fixture,
            SqliteEngineTests.Raw("SELECT sqlite_version() AS version", new DataGateway.Core.Queries.OutputColumn("version", "text")));
        var version = Assert.IsType<string>(Assert.Single(result.Rows)["version"]);
        Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+$", version);
        output.WriteLine("RID " + RuntimeInformation.RuntimeIdentifier + "; SELECT sqlite_version() = " + version);
        var native = Directory.EnumerateFiles(AppContext.BaseDirectory, "*e_sqlite3*", SearchOption.AllDirectories)
            .Where(path => path.Contains(RuntimeInformation.RuntimeIdentifier, StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.NotEmpty(native);
        foreach (var path in native)
            output.WriteLine(Path.GetRelativePath(AppContext.BaseDirectory, path).Replace('\\', '/') + " SHA256 " + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }
}
