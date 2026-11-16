using System.Text.Json;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Queries;
using DataGateway.Data.PostgreSql;
using Npgsql;
using NpgsqlTypes;
using Xunit;

using DataGateway.Core.Tests;
using System.Reflection;

namespace DataGateway.Web.Tests;

public sealed class PostgreSqlArchitectureTests
{
    [Fact]
    public void Eight_member_boundary_and_adapter_local_dependencies()
    {
        Assert.Equal(5,typeof(IDatabaseEngine).GetMethods().Length);
        Assert.True(typeof(PostgreSqlEngine).IsSealed);Assert.NotNull(typeof(PostgreSqlEngine).GetConstructor(Type.EmptyTypes));
        foreach(var assembly in new[]{typeof(SourceContract).Assembly,typeof(IDatabaseEngine).Assembly,typeof(Program).Assembly,typeof(DataGateway.Failures.FailureLog).Assembly})
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name == "Dapper" ||
                (reference.Name == "Npgsql" && assembly != typeof(Program).Assembly) ||
                (reference.Name == "Dapper.Data" && assembly != typeof(IDatabaseEngine).Assembly &&
                    assembly != typeof(Program).Assembly));
        }
        var refs=typeof(PostgreSqlEngine).Assembly.GetReferencedAssemblies().Select(a=>a.Name).ToArray();
        Assert.Contains("Npgsql",refs);
        Assert.DoesNotContain("Dapper.Data",refs);
        foreach (var assembly in new[] { typeof(Program).Assembly, typeof(DataGateway.Failures.FailureLog).Assembly })
            Assert.DoesNotContain("DataGateway.Data.Sqlite",assembly.GetReferencedAssemblies().Select(a=>a.Name));
        var friends=typeof(PostgreSqlEngine).Assembly.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>();
        Assert.Equal("DataGateway.Web.Tests",Assert.Single(friends).AssemblyName);
        using var json=JsonDocument.Parse(File.ReadAllText(TestFiles.InRepo("DataGateway.Data.PostgreSql/packages.lock.json")));
        var npg=json.RootElement.GetProperty("dependencies").GetProperty("net10.0").GetProperty("Npgsql");
        Assert.Equal("10.0.3",npg.GetProperty("resolved").GetString());Assert.Equal("[10.0.3, 10.0.3]",npg.GetProperty("requested").GetString());
    }
}
