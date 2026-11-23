using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Core.Tests;
using DataGateway.Data;
using DataGateway.Data.Catalog;
using DataGateway.Data.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>Pins the provider boundary across the product projects: which projects may reference a database driver
/// or data-access package, and that no public signature outside them carries a driver type.</summary>
/// <remarks>
/// <para>Core and the DataGateway library stay provider-free, so one library serves any database. DataGateway.Data
/// alone uses Dapper.Data for scoped connections, each engine package brings its own driver, and the host's catalog
/// readers may reference drivers directly.</para>
/// <para>The checks read project files, restore graphs, source text and reflected public types, so a reference that
/// is declared but unused, transitive, or hidden in a generic or delegate signature still fails.</para>
/// </remarks>
public sealed class CatalogArchitectureTests
{
    /// <summary>Each product project's package and project references are exactly the allowed ones, and its sources use
    /// no driver, Dapper, catalog SQL or SQL Server dialect text outside the places allowed to.</summary>
    [Fact]
    public void T10_project_references_and_source_enforce_the_provider_boundary()
    {
        foreach (var layer in new[] { "DataGateway.Core", "DataGateway.Data", "DataGateway", "DataGateway.Web", "DataGateway.Data.SqlServer" })
        {
            var directory = TestFiles.InRepo(layer);
            var project = XDocument.Load(Path.Combine(directory, layer + ".csproj"));
            var packages = project.Descendants("PackageReference").ToDictionary(node => (string)node.Attribute("Include")!, node => (string)node.Attribute("Version")!);
            if (layer == "DataGateway.Data")
            {
                Assert.Equal(
                    new Dictionary<string, string> { ["Dapper.Data"] = "3.1.2" }.OrderBy(pair => pair.Key),
                    packages.OrderBy(pair => pair.Key));
                Assert.Equal("../DataGateway.Core/DataGateway.Core.csproj", (string?)Assert.Single(project.Descendants("ProjectReference")).Attribute("Include"));
            }
            if (layer == "DataGateway.Data.SqlServer")
            {
                Assert.Equal("../DataGateway.Data/DataGateway.Data.csproj", (string?)Assert.Single(project.Descendants("ProjectReference")).Attribute("Include"));
                Assert.Equal(new Dictionary<string, string> { ["Microsoft.Data.SqlClient"] = "7.1.1", ["System.Security.Cryptography.Pkcs"] = "10.0.14", ["SqlKata"] = "4.0.1" }.OrderBy(pair => pair.Key), packages.OrderBy(pair => pair.Key));
                continue;
            }
            if (layer == "DataGateway")
                Assert.DoesNotContain(packages.Keys, name => name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(packages.Keys,
                name => ProviderName(name) && !(layer == "DataGateway.Data" && name == "Dapper.Data"));
            Assert.DoesNotContain(project.Descendants("Reference"), node => ProviderName((string?)node.Attribute("Include") ?? ""));
            if (layer is "DataGateway.Core" or "DataGateway.Data" or "DataGateway") Assert.DoesNotContain(project.Descendants("ProjectReference"), node => ((string?)node.Attribute("Include"))?.Contains("Data.SqlServer", StringComparison.Ordinal) == true);
            foreach (var path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Where(IsSource))
            {
                var text = File.ReadAllText(path);
                if (layer == "DataGateway")
                {
                    Assert.DoesNotContain("SqlClient", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("Npgsql", text, StringComparison.Ordinal);
                }
                var hostCatalog = layer == "DataGateway.Web" &&
                    Path.GetRelativePath(directory, path).Replace('\\', '/').StartsWith("Catalog/", StringComparison.Ordinal);
                foreach (var fragment in new[] { "Microsoft.Data.SqlClient", "using Dapper", "System.Security.Cryptography.Pkcs", "SqlConnection", "SqlException", "DbContext<", "[dbo].[vDataSource]", "Latin1_General_100_CI_AS", "SELECT TOP (", "COUNT_BIG(" })
                {
                    var catalogService = layer == "DataGateway.Data" &&
                        Path.GetRelativePath(directory, path).Replace('\\', '/') is "Catalog/CatalogService.cs" or "DatabaseEngine.cs";
                    if (catalogService && (fragment is "using Dapper" or "DbContext<")) continue;
                    if (hostCatalog && (fragment is "Microsoft.Data.SqlClient" or "using Dapper" or "SqlConnection" or
                        "DbContext<" or "[dbo].[vDataSource]" or "Latin1_General_100_CI_AS")) continue;
                    Assert.DoesNotContain(fragment, text, StringComparison.Ordinal);
                }
                if (Path.GetFileName(path) != "Program.cs" && !hostCatalog)
                    Assert.DoesNotContain("SqlServerEngine", text, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>No exported type or public member of Core, Data or the library exposes a driver type, except the
    /// connection type parameter of the catalog service, the engine base and <c>UseCatalog</c>, which is constrained to
    /// <see cref="DbConnection"/>.</summary>
    [Fact]
    public void T10_public_signatures_are_exact_and_recursively_provider_free()
    {
        var methods = typeof(IDatabaseEngine).GetMethods();
        Assert.Equal(new[] { "Compile", "ReadSourceAsync", "RecognizeFailure", "ValidateSourceConnection", "get_ProviderName" }, methods.Select(method => method.Name).Order(StringComparer.Ordinal));
        Check("get_ProviderName", typeof(string), []);
        foreach (var member in methods)
        {
            Assert.True(member.IsAbstract); Assert.False(member.IsGenericMethod); Assert.False(member.IsStatic);
            Assert.False(ForbiddenType(member.ReturnType));
            foreach (var parameter in member.GetParameters())
            { Assert.False(parameter.IsOptional); Assert.False(parameter.HasDefaultValue); Assert.False(ForbiddenType(parameter.ParameterType)); }
        }
        foreach (var assembly in new[] { typeof(SourceContract).Assembly, typeof(IDatabaseEngine).Assembly, typeof(DataGateway.Mcp.McpEndpoint).Assembly })
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type == typeof(CatalogService<>) || type == typeof(DataGateway.Data.DatabaseEngine<>))
                Assert.Equal(typeof(DbConnection),
                    Assert.Single(type.GetGenericArguments()[0].GetGenericParameterConstraints()));
            else
                Assert.False(ForbiddenType(type));
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                Type? allowed = null;
                if (type == typeof(DataGatewayBuilder) && method.Name == "UseCatalog")
                {
                    Assert.True(method.IsGenericMethodDefinition);
                    allowed = Assert.Single(method.GetGenericArguments());
                    Assert.Equal(typeof(DbConnection), Assert.Single(allowed.GetGenericParameterConstraints()));
                    Assert.Equal(GenericParameterAttributes.DefaultConstructorConstraint, allowed.GenericParameterAttributes);
                    Assert.Equal(typeof(DataGatewayBuilder), method.ReturnType);
                    var parameter = Assert.Single(method.GetParameters());
                    Assert.Equal(typeof(Func<,>).MakeGenericType(typeof(IServiceProvider), typeof(CatalogService<>).MakeGenericType(allowed)), parameter.ParameterType);
                    Assert.False(parameter.IsOptional); Assert.False(parameter.HasDefaultValue);
                }
                Assert.False(ForbiddenType(method.ReturnType, allowed));
                Assert.All(method.GetGenericArguments(), argument => Assert.False(ForbiddenType(argument, allowed)));
                Assert.All(method.GetParameters(), parameter => Assert.False(ForbiddenType(parameter.ParameterType, allowed)));
            }
            foreach (var ctor in type.GetConstructors()) Assert.All(ctor.GetParameters(), parameter => Assert.False(ForbiddenType(parameter.ParameterType)));
            foreach (var property in type.GetProperties()) Assert.False(ForbiddenType(property.PropertyType));
            foreach (var field in type.GetFields()) Assert.False(ForbiddenType(field.FieldType));
        }
        Assert.DoesNotContain(typeof(SqlServerEngine).GetMethods(), method => method.Name.StartsWith("ReadCatalog", StringComparison.Ordinal) && method.IsGenericMethod);
        void Check(string name, Type result, Type[] arguments)
        {
            var method = Assert.Single(methods, candidate => candidate.Name == name);
            Assert.Equal(result, method.ReturnType); Assert.Equal(arguments, method.GetParameters().Select(parameter => parameter.ParameterType));
        }
    }

    /// <summary>The compiled assemblies reference no provider assembly beyond those allowed: Dapper.Data for Data, and
    /// the host's own driver and Dapper.Data for the web host.</summary>
    [Fact]
    public void T10_product_assembly_references_exclude_direct_provider_dependencies()
    {
        foreach (var assembly in new[] { typeof(SourceContract).Assembly, typeof(IDatabaseEngine).Assembly, typeof(Program).Assembly, typeof(DataGateway.Mcp.McpEndpoint).Assembly })
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(),
                name => ProviderName(name.Name ?? "") &&
                    !(assembly == typeof(IDatabaseEngine).Assembly && name.Name == "Dapper.Data") &&
                    !(assembly == typeof(Program).Assembly &&
                        (name.Name is "Microsoft.Data.SqlClient" or "Dapper.Data")));
            if (assembly != typeof(Program).Assembly) Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name => name.Name == "DataGateway.Data.SqlServer");
        }
    }

    /// <summary>Every target in each provider-free project's restore graph holds only allowed packages, including
    /// transitive ones; the engine and host graphs carry the pinned driver versions.</summary>
    [Fact]
    public void T10_each_restored_product_graph_has_the_intended_transitive_closure_in_all_targets()
    {
        foreach (var layer in new[] { "DataGateway.Core", "DataGateway.Data", "DataGateway", "DataGateway.Web", "DataGateway.Data.SqlServer" })
        {
            var path = TestFiles.InRepo(layer + "/obj/project.assets.json");
            Assert.True(File.Exists(path), "Restore assets must exist for each product project.");
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var targets = document.RootElement.GetProperty("targets").EnumerateObject().ToArray();
            Assert.NotEmpty(targets);
            foreach (var target in targets)
            {
                var entries = target.Value.EnumerateObject().Select(library => library.Name).ToArray();
                if (layer is "DataGateway.Core" or "DataGateway.Data" or "DataGateway")
                {
                    Assert.DoesNotContain(entries,
                        item => !AllowedGraphEntry(layer, item.Split('/')[0]));
                    if (layer is "DataGateway.Data" or "DataGateway")
                    {
                        Assert.Contains("Dapper.Data/3.1.2", entries);
                        Assert.Contains("Dapper/2.1.89", entries);
                    }
                    Assert.DoesNotContain(entries, item => item.StartsWith("DataGateway.Data.SqlServer/", StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    Assert.Contains("Microsoft.Data.SqlClient/7.1.1", entries);
                    Assert.Contains("Dapper.Data/3.1.2", entries);
                    Assert.Equal("System.Security.Cryptography.Pkcs/10.0.14", Assert.Single(entries, item => item.StartsWith("System.Security.Cryptography.Pkcs/", StringComparison.OrdinalIgnoreCase)));
                }
            }
            foreach (var library in document.RootElement.GetProperty("libraries").EnumerateObject())
                if (layer is "DataGateway.Core" or "DataGateway.Data" or "DataGateway") Assert.True(AllowedGraphEntry(layer, library.Name.Split('/')[0]));
        }
    }

    /// <summary>Pins the predicates the other tests rely on, so a weakened predicate cannot make them pass vacuously.</summary>
    [Fact]
    public void T10_predicates_reject_direct_nested_and_delegate_provider_types_but_accept_neutral_contracts_and_web_transitives()
    {
        var useCatalog = typeof(DataGatewayBuilder).GetMethod("UseCatalog")!;
        var allowed = Assert.Single(useCatalog.GetGenericArguments());
        var factory = Assert.Single(useCatalog.GetParameters()).ParameterType;
        Assert.True(ForbiddenType(factory));
        Assert.False(ForbiddenType(factory, allowed));
        Assert.True(ForbiddenType(typeof(DbConnection), allowed));
        Assert.True(ForbiddenType(typeof(Task<IReadOnlyList<SqlConnection>>), allowed));
        Assert.True(ForbiddenType(typeof(Func<Task<DbDataReader>>), allowed));
        Assert.True(ForbiddenType(typeof(SqlConnection)));
        Assert.True(ForbiddenType(typeof(Task<IReadOnlyList<DbConnection>>)));
        Assert.True(ForbiddenType(typeof(Func<Task<DbDataReader>>)));
        Assert.False(ForbiddenType(typeof(Task<IReadOnlyList<IDictionary<string, object?>>>)));
        Assert.True(ProviderName("Microsoft.Data.SqlClient"));
        Assert.True(ProviderName("Dapper.Data"));
        Assert.False(ProviderName("SqlKata"));
        // Web's catalog uses direct SqlClient, Npgsql and Dapper.Data references.
        Assert.True(AllowedGraphEntry("DataGateway.Web", "Microsoft.Data.SqlClient"));
        Assert.False(AllowedGraphEntry("DataGateway.Data", "Microsoft.Data.SqlClient"));
        Assert.True(AllowedGraphEntry("DataGateway.Data", "SqlKata"));
        Assert.True(AllowedGraphEntry("DataGateway.Data", "Dapper"));
        Assert.True(AllowedGraphEntry("DataGateway.Data", "Dapper.Data"));
        Assert.False(AllowedGraphEntry("DataGateway.Core", "Dapper"));
        Assert.False(AllowedGraphEntry("DataGateway.Core", "Dapper.Data"));
        Assert.False(AllowedGraphEntry("DataGateway.Data", "Dapper.Unapproved"));
        Assert.True(AllowedGraphEntry("DataGateway", "Dapper"));
        Assert.True(AllowedGraphEntry("DataGateway", "Dapper.Data"));
        Assert.False(AllowedGraphEntry("DataGateway", "Dapper.Unapproved"));
        Assert.False(AllowedGraphEntry("DataGateway", "Microsoft.Data.SqlClient"));
        Assert.False(AllowedGraphEntry("DataGateway", "Npgsql"));
        Assert.False(AllowedGraphEntry("DataGateway", "Microsoft.Data.Sqlite"));
        Assert.False(AllowedGraphEntry("DataGateway", "DataGateway.Data.SqlServer"));
        Assert.False(AllowedGraphEntry("DataGateway", "DataGateway.Data.PostgreSql"));
    }

    /// <summary>The DataGateway library has exactly its project settings, project references and packages, reads no
    /// configuration or environment, knows nothing of sign-in, and exports only its builder, extensions and limits.</summary>
    /// <remarks>Sign-in belongs to the host; the API page must not mention it, so a host that uses no sign-in or another
    /// scheme gets a page that is still correct.</remarks>
    [Fact]
    public void T11_library_has_only_the_intended_dependencies_and_no_host_dependencies()
    {
        var directory = TestFiles.InRepo("DataGateway");
        var project = XDocument.Load(Path.Combine(directory, "DataGateway.csproj"));
        Assert.Equal("Microsoft.NET.Sdk", (string?)project.Root!.Attribute("Sdk"));
        Assert.Equal("net10.0", Assert.Single(project.Descendants("TargetFramework")).Value);
        Assert.Equal("DataGateway", Assert.Single(project.Descendants("AssemblyName")).Value);
        Assert.Equal("DataGateway", Assert.Single(project.Descendants("RootNamespace")).Value);
        Assert.Equal("enable", Assert.Single(project.Descendants("Nullable")).Value);
        Assert.Equal("enable", Assert.Single(project.Descendants("ImplicitUsings")).Value);
        Assert.Equal("Microsoft.AspNetCore.App",
            (string?)Assert.Single(project.Descendants("FrameworkReference")).Attribute("Include"));
        Assert.Equal(new[] { "../DataGateway.Core/DataGateway.Core.csproj", "../DataGateway.Data/DataGateway.Data.csproj" },
            project.Descendants("ProjectReference").Select(node => (string)node.Attribute("Include")!).Order(StringComparer.Ordinal));
        Assert.Equal(new Dictionary<string, string>
        {
            ["ModelContextProtocol.AspNetCore"] = "2.1.0",
            ["Microsoft.AspNetCore.OpenApi"] = "10.0.14",
            ["Scalar.AspNetCore"] = "2.18.0"
        }.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            project.Descendants("PackageReference").ToDictionary(node => (string)node.Attribute("Include")!,
                node => (string)node.Attribute("Version")!).OrderBy(pair => pair.Key, StringComparer.Ordinal));
        var paths = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Where(IsSource).ToArray();
        Assert.NotEmpty(paths);
        foreach (var path in paths)
        {
            var text = File.ReadAllText(path);
            foreach (var fragment in new[] { "IConfiguration", "IHostEnvironment", "Environment.GetEnvironmentVariable",
                "using Serilog", "Microsoft.AspNetCore.Authentication", "WindowsSignIn" })
                Assert.DoesNotContain(fragment, text, StringComparison.Ordinal);
        }
        var pagePaths = paths.Where(path =>
            Path.GetRelativePath(directory, path).Replace('\\', '/').StartsWith("Page/", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(pagePaths);
        foreach (var path in pagePaths)
        {
            var text = File.ReadAllText(path);
            foreach (var fragment in new[] { "sign-in", "Negotiate", "WWW-Authenticate", "NotAuthenticated" })
                Assert.DoesNotContain(fragment, text, StringComparison.OrdinalIgnoreCase);
        }
        var assembly = typeof(DataGateway.Failures.FailureLog).Assembly;
        Assert.Equal("DataGateway", assembly.GetName().Name);
        Assert.Equal(new[] { typeof(DataGatewayBuilder), typeof(DataGatewayExtensions), typeof(GatewayLimits) },
            assembly.GetExportedTypes().OrderBy(type => type.FullName, StringComparer.Ordinal));
        Assert.Equal(new[] { "DataGateway.Web.Tests" },
            assembly.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
                .Select(attribute => attribute.AssemblyName).Order(StringComparer.Ordinal));
    }

    /// <summary>Whether <paramref name="layer"/>'s restore graph may contain the package <paramref name="name"/>.</summary>
    private static bool AllowedGraphEntry(string layer, string name) =>
        (layer is "DataGateway.Web" or "DataGateway.Data.SqlServer") ||
        ((layer is "DataGateway.Data" or "DataGateway") && (name is "Dapper" or "Dapper.Data")) ||
        (!ProviderName(name) && (layer != "DataGateway" ||
            !new[] { "Npgsql", "Sqlite", "DataGateway.Data.SqlServer", "DataGateway.Data.PostgreSql" }
                .Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase))));
    /// <summary>Whether a file is a source file rather than build output.</summary>
    private static bool IsSource(string path) => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "obj" or "bin");
    /// <summary>Whether a package, assembly or namespace name belongs to a database provider or data-access package.</summary>
    private static bool ProviderName(string name) => name.Contains("SqlClient", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Dapper", StringComparison.OrdinalIgnoreCase) || name.Contains("Cryptography.Pkcs", StringComparison.OrdinalIgnoreCase);
    /// <summary>Whether <paramref name="type"/>, or any element, generic argument, constraint or delegate signature
    /// inside it, is a provider or connection type.</summary>
    /// <param name="type">The type to inspect.</param>
    /// <param name="allowed">A generic parameter that is allowed even though it is constrained to a connection type.</param>
    private static bool ForbiddenType(Type type, Type? allowed = null)
    {
        if (type.IsGenericParameter && type == allowed) return false;
        if (ProviderName(type.Namespace ?? "") || new[] { "DbConnection", "DbDataReader", "DbSession", "IDbConnection", "IDataReader" }.Contains(type.Name)) return true;
        if (type.HasElementType && ForbiddenType(type.GetElementType()!, allowed)) return true;
        if (type.GetGenericArguments().Any(argument => ForbiddenType(argument, allowed))) return true;
        if (type.IsGenericParameter && type.GetGenericParameterConstraints().Any(constraint => ForbiddenType(constraint, allowed))) return true;
        if (type.BaseType == typeof(MulticastDelegate))
        {
            var invoke = type.GetMethod("Invoke")!;
            if (ForbiddenType(invoke.ReturnType, allowed) || invoke.GetParameters().Any(parameter => ForbiddenType(parameter.ParameterType, allowed))) return true;
        }
        return false;
    }
}
