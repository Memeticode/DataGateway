using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Queries;
using DataGateway.Data.SqlServer;
using SqlKata;
using SqlKata.Compilers;
using Xunit;

namespace DataGateway.Core.Tests;

public sealed class EngineBoundaryTests
{
    [Fact]
    public void T1_returned_tree_and_every_nested_container_are_detached()
    {
        var validated = Validate("""{"select":["label"],"filters":[{"col":"label","op":"like","value":"a_[b"},{"or":[{"col":"label","op":"like","value":"%_%"},{"and":[{"col":"label","op":"contains","value":"50%_\\[x]"}]}]},{"col":"label","op":"in","value":["a","b"]}],"limit":2}""");
        var engine = new SqlServerEngine();
        var before = engine.Compile(validated);
        var rowLimit = validated.RowLimit;
        var columns = validated.Columns;
        var copy = validated.CloneQuery();
        var other = validated.CloneQuery();
        var copies = Walk(copy).ToArray();
        var others = Walk(other).ToArray();
        Assert.Equal(copies.Length, others.Length);
        Assert.True(copies.Length > 2);
        Assert.Null(copy.Parent);
        for (var i = 0; i < copies.Length; i++)
        {
            Assert.NotSame(copies[i], others[i]);
            Assert.NotSame(copies[i].Clauses, others[i].Clauses);
            Assert.NotSame(copies[i].Variables, others[i].Variables);
            Assert.NotSame(copies[i].Includes, others[i].Includes);
            foreach (var nested in copies[i].GetComponents<AbstractCondition>("where").OfType<NestedCondition<Query>>())
                Assert.Same(copies[i], nested.Query.Parent);
            copies[i].Variables["probe"] = "changed";
            copies[i].Includes.Add(null!);
            foreach (var pattern in copies[i].GetComponents<AbstractCondition>("where").OfType<BasicStringCondition>())
                pattern.Value = "changed";
        }
        var membership = Assert.Single(copy.GetComponents<AbstractCondition>("where").OfType<InCondition<object>>());
        Assert.IsAssignableFrom<IList<object>>(membership.Values)[0] = "changed";
        foreach (var child in copies.Skip(1))
            Assert.IsType<Query>(child.Parent).Limit(42);
        copy.From("changed.view").ClearComponent("select").Select("changed");
        copy.ClearComponent("where");
        for (var i = 0; i < 3; i++)
        {
            var again = engine.Compile(validated);
            Assert.Equal(before.Sql, again.Sql);
            Assert.Equal(before.Parameters.ToArray(), again.Parameters.ToArray());
            Assert.Equal(before.Keep, again.Keep);
            Assert.Equal(before.HasMatchedRowCount, again.HasMatchedRowCount);
            Assert.Equal(before.Columns, again.Columns);
            Assert.Same(rowLimit, validated.RowLimit);
            Assert.Same(columns, validated.Columns);
            Assert.Equal(2, validated.CloneQuery().GetOneComponent<LimitClause>("limit").Limit);
            foreach (var node in Walk(validated.CloneQuery()))
            {
                Assert.Empty(node.Variables);
                Assert.Empty(node.Includes);
            }
        }
    }

    [Fact]
    public void T2_compiled_result_snapshots_neutral_bindings_and_columns()
    {
        var parameters = new Dictionary<string, object> { [":value"] = "original", [":limit"] = 3 };
        OutputColumn[] columns = [new("label", "text")];
        var compiled = new CompiledQuery("select :value", parameters, 2, false, columns);
        parameters.Clear();
        columns[0] = new("changed", "number");
        Assert.Equal(new[] { ":value", ":limit" }, compiled.Parameters.Keys);
        Assert.Equal("original", compiled.Parameters[":value"]);
        Assert.Equal(3, Assert.IsType<int>(compiled.Parameters[":limit"]));
        Assert.Equal(new OutputColumn("label", "text"), Assert.Single(compiled.Columns));
        Assert.Equal(2, compiled.Keep);
        Assert.False(compiled.HasMatchedRowCount);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object>)compiled.Parameters).Add("changed", 1));
        Assert.Throws<NotSupportedException>(() => ((IList<OutputColumn>)compiled.Columns).Add(new("changed", "text")));
        Assert.Equal("sql", Assert.Throws<ArgumentNullException>(() => new CompiledQuery(null!, parameters, 1, false, columns)).ParamName);
        Assert.Equal("parameters", Assert.Throws<ArgumentNullException>(() => new CompiledQuery("select 1", null!, 1, false, columns)).ParamName);
        Assert.Equal("columns", Assert.Throws<ArgumentNullException>(() => new CompiledQuery("select 1", parameters, 1, false, null!)).ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompiledQuery("select 1", parameters, 0, false, columns));
    }

    [Fact]
    public void T3_an_outside_adapter_compiles_using_only_public_contracts()
    {
        var validated = Validate("""{"select":["label"],"limit":2}""");
        IDatabaseEngine engine = new PortableProbeEngine();
        var first = engine.Compile(validated);
        var second = engine.Compile(validated);
        Assert.Contains("\"dbo\".\"synthetic_view\"", first.Sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT @p0", first.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP", first.Sql, StringComparison.Ordinal);
        Assert.Equal(new KeyValuePair<string, object>[] { new("@p0", 3) }, first.Parameters.ToArray());
        Assert.Equal(2, first.Keep);
        Assert.False(first.HasMatchedRowCount);
        Assert.Equal(validated.Columns, first.Columns);
        Assert.Equal(first.Sql, second.Sql);
        Assert.Equal(first.Parameters.ToArray(), second.Parameters.ToArray());
        Assert.Equal(2, validated.CloneQuery().GetOneComponent<LimitClause>("limit").Limit);
    }

    [Fact]
    public void T4_eight_engine_capabilities_and_core_has_no_dialect()
    {
        var methods = typeof(IDatabaseEngine).GetMethods();
        Assert.Equal(new[] { "Compile", "ReadSourceAsync", "RecognizeFailure", "ValidateSourceConnection", "get_ProviderName" }, methods.Select(m => m.Name).Order(StringComparer.Ordinal));
        foreach (var member in methods)
        {
            Assert.False(member.IsGenericMethod);
            Assert.True(member.IsAbstract);
            InspectType(member.ReturnType);
            foreach (var parameter in member.GetParameters())
            {
                Assert.False(parameter.IsOptional);
                Assert.False(parameter.HasDefaultValue);
                InspectType(parameter.ParameterType);
            }
        }
        var method = Assert.Single(methods, m => m.Name == "Compile");
        Assert.Equal("Compile", method.Name);
        Assert.Equal(typeof(CompiledQuery), method.ReturnType);
        Assert.Equal(typeof(ValidatedQuery), Assert.Single(method.GetParameters()).ParameterType);
        var read = Assert.Single(methods, m => m.Name == "ReadSourceAsync");
        Assert.Equal(typeof(Task<QueryRows>), read.ReturnType);
        Assert.Equal(new[] { typeof(string), typeof(CompiledQuery), typeof(int), typeof(Action),
            typeof(Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>>), typeof(CancellationToken) },
            read.GetParameters().Select(p => p.ParameterType));
        var validate = Assert.Single(methods, m => m.Name == "ValidateSourceConnection");
        Assert.Equal(typeof(void), validate.ReturnType);
        Assert.Equal(typeof(string), Assert.Single(validate.GetParameters()).ParameterType);
        var recognize = Assert.Single(methods, m => m.Name == "RecognizeFailure");
        Assert.Equal(typeof(ProviderFailure), recognize.ReturnType);
        Assert.Equal(typeof(Exception), Assert.Single(recognize.GetParameters()).ParameterType);
        Assert.Equal(new[] { "IsTimeout", "Number" }, typeof(ProviderFailure).GetProperties().Select(p => p.Name).Order());
        Assert.Equal(typeof(bool), typeof(ProviderFailure).GetProperty("IsTimeout")!.PropertyType);
        Assert.Equal(typeof(int?), typeof(ProviderFailure).GetProperty("Number")!.PropertyType);
        var provider = Assert.Single(typeof(IDatabaseEngine).GetProperties());
        Assert.Equal("ProviderName", provider.Name);
        Assert.Equal(typeof(string), provider.PropertyType);
        Assert.Null(provider.SetMethod);
        Assert.True(provider.GetMethod!.IsAbstract);
        Assert.Empty(provider.GetMethod.GetParameters());
        Assert.Empty(typeof(IDatabaseEngine).GetEvents());
        Assert.Empty(typeof(IDatabaseEngine).GetInterfaces());
        Assert.DoesNotContain(typeof(ValidatedQuery).Assembly.GetCustomAttributesData(),
            attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute");
        foreach (var file in Directory.EnumerateFiles(TestFiles.InRepo("DataGateway.Core"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "obj" or "bin")) continue;
            var text = File.ReadAllText(file);
            foreach (var forbidden in new[] { "SqlServerCompiler", "COUNT_BIG(", "SELECT TOP (", "BracketLikeWildcards", "using SqlKata.Compilers;" })
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
        var dataReference = Assert.Single(System.Xml.Linq.XDocument.Load(TestFiles.InRepo("DataGateway.Data/DataGateway.Data.csproj"))
            .Descendants("ProjectReference"));
        Assert.Equal("../DataGateway.Core/DataGateway.Core.csproj", (string?)dataReference.Attribute("Include"));
        var engineReference = Assert.Single(System.Xml.Linq.XDocument.Load(TestFiles.InRepo("DataGateway.Data.SqlServer/DataGateway.Data.SqlServer.csproj"))
            .Descendants("ProjectReference"));
        Assert.Equal("../DataGateway.Data/DataGateway.Data.csproj", (string?)engineReference.Attribute("Include"));
    }

    private static void InspectType(Type type)
    {
        foreach (var forbidden in new[] { "SqlClient", "Dapper", "DbConnection", "DbDataReader", "DbSession", "IDbConnection", "IDataReader" })
            Assert.DoesNotContain(forbidden, type.FullName ?? type.Name, StringComparison.Ordinal);
        if (type.HasElementType) InspectType(type.GetElementType()!);
        foreach (var argument in type.GetGenericArguments()) InspectType(argument);
        if (type.IsGenericParameter)
            foreach (var constraint in type.GetGenericParameterConstraints()) InspectType(constraint);
        if (type.BaseType == typeof(MulticastDelegate))
        {
            var invoke = type.GetMethod("Invoke")!;
            InspectType(invoke.ReturnType);
            foreach (var argument in invoke.GetParameters()) InspectType(argument.ParameterType);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(902)]
    public async Task T5_outside_adapter_executes_and_recognizes_its_own_failure(int? number)
    {
        var source = CatalogMapping.Map(new(new DataSourceRow("dbo.synthetic_view", "portable:source")
        { QueryTimeoutSeconds = 41,
            Id = 7319, Name = "synthetic", Title = "Synthetic", Description = "Synthetic", Grain = "One row", RowCap = 500, IsActive = true
        }, [new("label", 1, "Label", "Synthetic", "text", null, null, null, true, true, true, true, true)])).Value;
        var engine = new ExecutingProbeEngine(number);
        var runner = new QueryRunner(engine);
        var compiled = runner.Compile(Validate("""{"select":["label"],"limit":2}"""));
        var success = await runner.RunAsync(source, compiled, CancellationToken.None);
        Assert.True(success.IsSuccess);
        Assert.Same(engine.Row, Assert.Single(success.Value.Rows));
        Assert.Equal("portable:source", engine.Connection);
        engine.Error = new ProbeException("probe", new Exception("inner"));
        var failed = await runner.RunAsync(source, compiled, CancellationToken.None);
        Assert.Equal("SourceUnavailable", failed.Failure?.Code);
        Assert.Equal("command", failed.Failure?.Step);
        Assert.Equal(number, failed.Failure?.SqlNumber);
        Assert.Same(engine.Error, failed.Failure?.DriverException);
        Assert.Equal(2, engine.Opened);
    }

    private sealed class ProbeException(string message, Exception inner) : Exception(message, inner);

    private sealed class ExecutingProbeEngine(int? number) : PortableProbeEngine
    {
        internal Exception? Error;
        internal string? Connection;
        internal int Opened;
        internal readonly IDictionary<string, object?> Row = new Dictionary<string, object?> { ["label"] = "portable" };
        public override ProviderFailure? RecognizeFailure(Exception error) => error is ProbeException ? new(false, number) : null;
        public override async Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int commandTimeoutSeconds,
            Action opened, Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connection = connection;
            Assert.Equal(41, commandTimeoutSeconds);
            opened(); Opened++;
            if (Error is not null) throw Error;
            return await consume(Rows());
        }
        private async IAsyncEnumerable<IDictionary<string, object?>> Rows() { await Task.Yield(); yield return Row; }
    }

    private class PortableProbeEngine : IDatabaseEngine
    {
        string IDatabaseEngine.ProviderName => "synthetic";
        public void ValidateSourceConnection(string? connection) => ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        public virtual ProviderFailure? RecognizeFailure(Exception error) => null;
        public virtual Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int commandTimeoutSeconds,
            Action opened, Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            opened();
            return consume(Empty());
        }
        private static async IAsyncEnumerable<IDictionary<string, object?>> Empty() { await Task.CompletedTask; yield break; }
        public CompiledQuery Compile(ValidatedQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);
            var runnable = query.CloneQuery().Limit(query.RowLimit.Fetch);
            var result = new PostgresCompiler().Compile(runnable);
            return new CompiledQuery(result.Sql, result.NamedBindings,
                query.RowLimit.Keep, false, query.Columns);
        }
    }

    private static IEnumerable<Query> Walk(Query query)
    {
        yield return query;
        foreach (var nested in query.GetComponents<AbstractCondition>("where").OfType<NestedCondition<Query>>())
            foreach (var child in Walk(nested.Query)) yield return child;
    }

    private static ValidatedQuery Validate(string json)
    {
        var source = new DataSourceRow("dbo.synthetic_view", "fixture-connection-removed")
        { QueryTimeoutSeconds = 41,
            Id = 7319, Name = "synthetic", Title = "Synthetic", Description = "Synthetic",
            Grain = "One row", RowCap = 500, IsActive = true
        };
        var mapped = CatalogMapping.Map(new(source,
            [new("label", 1, "Label", "Synthetic", "text", null, null, null, true, true, true, true, true)]));
        Assert.True(mapped.IsSuccess, mapped.Failure?.Message);
        var validated = QueryBuilding.Validate(mapped.Value, json, QueryLimits.ForRowCap(500));
        Assert.True(validated.IsSuccess, validated.Failure?.Message);
        return validated.Value;
    }
}
