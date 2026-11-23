using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Queries;
using DataGateway.Data.SqlServer;
using SqlKata;
using SqlKata.Compilers;
using Xunit;

namespace DataGateway.Core.Tests;

/// <summary>Pins the boundary between the validated query and a database engine: what <see cref="IDatabaseEngine"/>
/// asks of an engine, and what Core hands it.</summary>
/// <remarks>
/// <para>Core owns no SQL dialect. An engine receives a <see cref="ValidatedQuery"/>, compiles it to its own SQL and
/// returns a <see cref="CompiledQuery"/>, so a new database needs only a new engine, and no engine can change the
/// query another engine or a later request sees.</para>
/// <para>The probe engines here are written only against public types, as an outside package would be.</para>
/// </remarks>
public sealed class EngineBoundaryTests
{
    /// <summary><see cref="ValidatedQuery.CloneQuery"/> returns a tree, nested filter groups included, that an engine
    /// may change freely: compiling the validated query again gives the same SQL, bindings and row plan.</summary>
    /// <remarks>Without a deep copy, an engine that adds its row limit or rewrites a pattern would change the shared
    /// query, and the next compile would differ.</remarks>
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

    /// <summary><see cref="CompiledQuery"/> copies its bindings and columns, exposes them read-only and refuses missing
    /// arguments or a keep count below 1.</summary>
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

    /// <summary>An engine built only from public types compiles a query in another dialect (limit instead of top) with
    /// the planned fetch and keep counts.</summary>
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

    /// <summary>The engine interface is exactly its four methods and provider name, with no optional parameters and no
    /// driver or data-access types in any signature; Core holds no SQL Server dialect text and exposes no internals; the
    /// project references run Core, then Data, then the SQL Server engine.</summary>
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

    /// <summary>Fails when a type, or any element, generic argument, constraint or delegate signature inside it, names a
    /// database driver or data-access type.</summary>
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

    /// <summary>An outside engine runs through <c>QueryRunner</c> with the source's connection and timeout, and its own
    /// recognized failure becomes a source-unavailable failure carrying the driver's error number and exception.</summary>
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

    /// <summary>The driver exception the probe engine recognizes as its own.</summary>
    private sealed class ProbeException(string message, Exception inner) : Exception(message, inner);

    /// <summary>A probe engine that returns one row, records the connection it was given and can be made to fail.</summary>
    /// <param name="number">The error number the engine reports for its own failure.</param>
    private sealed class ExecutingProbeEngine(int? number) : PortableProbeEngine
    {
        /// <summary>When set, the next read throws it after the connection opens.</summary>
        internal Exception? Error;
        /// <summary>The connection the last read was given.</summary>
        internal string? Connection;
        /// <summary>How many reads opened a connection.</summary>
        internal int Opened;
        /// <summary>The single row every successful read returns.</summary>
        internal readonly IDictionary<string, object?> Row = new Dictionary<string, object?> { ["label"] = "portable" };
        /// <summary>Recognizes only <see cref="ProbeException"/>, as a failure that is not a timeout.</summary>
        public override ProviderFailure? RecognizeFailure(Exception error) => error is ProbeException ? new(false, number) : null;
        /// <summary>Records the connection, checks the source's timeout arrives, then fails or returns <see cref="Row"/>.</summary>
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
        /// <summary>Streams <see cref="Row"/> asynchronously.</summary>
        private async IAsyncEnumerable<IDictionary<string, object?>> Rows() { await Task.Yield(); yield return Row; }
    }

    /// <summary>A minimal engine written against public contracts only, compiling with the PostgreSQL dialect.</summary>
    private class PortableProbeEngine : IDatabaseEngine
    {
        /// <summary>The probe's provider name.</summary>
        string IDatabaseEngine.ProviderName => "synthetic";
        /// <summary>Accepts any non-blank connection.</summary>
        public void ValidateSourceConnection(string? connection) => ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        /// <summary>Recognizes no failure.</summary>
        public virtual ProviderFailure? RecognizeFailure(Exception error) => null;
        /// <summary>Reports the connection open and returns no rows.</summary>
        public virtual Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int commandTimeoutSeconds,
            Action opened, Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            opened();
            return consume(Empty());
        }
        /// <summary>An empty row stream.</summary>
        private static async IAsyncEnumerable<IDictionary<string, object?>> Empty() { await Task.CompletedTask; yield break; }
        /// <summary>Applies the row plan's fetch count to a copy of the query and compiles it.</summary>
        public CompiledQuery Compile(ValidatedQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);
            var runnable = query.CloneQuery().Limit(query.RowLimit.Fetch);
            var result = new PostgresCompiler().Compile(runnable);
            return new CompiledQuery(result.Sql, result.NamedBindings,
                query.RowLimit.Keep, false, query.Columns);
        }
    }

    /// <summary>The query and every nested filter-group query below it, depth first.</summary>
    private static IEnumerable<Query> Walk(Query query)
    {
        yield return query;
        foreach (var nested in query.GetComponents<AbstractCondition>("where").OfType<NestedCondition<Query>>())
            foreach (var child in Walk(nested.Query)) yield return child;
    }

    /// <summary>Validates <paramref name="json"/> against a one-column synthetic source with a row cap of 500.</summary>
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
