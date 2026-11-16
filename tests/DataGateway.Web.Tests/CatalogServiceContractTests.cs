using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Core.Tests;
using DataGateway.Data;
using DataGateway.Data.Catalog;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class CatalogServiceContractTests
{
    [Fact]
    public void Public_surface_and_context_identity_are_exact()
    {
        var service = typeof(CatalogService<>);
        Assert.Equal(typeof(IDatabaseEngine).Assembly, service.Assembly);
        Assert.Equal("DataGateway.Data.Catalog", service.Namespace);
        Assert.True(service.IsPublic && service.IsAbstract && service.IsClass);
        var connection = Assert.Single(service.GetGenericArguments());
        Assert.Equal(typeof(DbConnection), Assert.Single(connection.GetGenericParameterConstraints()));
        Assert.Equal(GenericParameterAttributes.DefaultConstructorConstraint,
            connection.GenericParameterAttributes);
        var context = typeof(DbContext<>).MakeGenericType(connection);
        var constructor = Assert.Single(service.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
        Assert.True(constructor.IsFamily);
        Assert.Equal(context, Assert.Single(constructor.GetParameters()).ParameterType);
        var property = Assert.Single(service.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Equal("Db", property.Name);
        Assert.Equal(context, property.PropertyType);
        Assert.True(property.GetMethod!.IsFamily);
        Assert.Null(property.SetMethod);
        Assert.Empty(service.GetProperties());
        Assert.False(typeof(IDisposable).IsAssignableFrom(service));
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(service));
        var methods = service.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.Equal(new[] { "QueryCatalogAsync", "QueryCatalogDetailAsync" }, methods.Select(m => m.Name).Order());
        CheckMethod("QueryCatalogAsync", typeof(Task<IReadOnlyList<SourceListRow>>), [typeof(CancellationToken)]);
        CheckMethod("QueryCatalogDetailAsync", typeof(Task<CatalogDetail>), [typeof(string), typeof(CancellationToken)]);
        var nullable = new NullabilityInfoContext();
        Assert.Equal(NullabilityState.Nullable, Assert.Single(nullable.Create(
            methods.Single(m => m.Name == "QueryCatalogDetailAsync").ReturnParameter).GenericTypeArguments).ReadState);
        Assert.Equal(NullabilityState.NotNull, Assert.Single(nullable.Create(
            methods.Single(m => m.Name == "QueryCatalogAsync").ReturnParameter).GenericTypeArguments).ReadState);
        var db = NewDb();
        Assert.Same(db, new ExampleCatalog(db).SuppliedDb);
        Assert.Equal("db", Assert.Throws<ArgumentNullException>(() => new ExampleCatalog(null!)).ParamName);

        var detail = typeof(CatalogDetail);
        Assert.Equal(typeof(CatalogRows).Assembly, detail.Assembly);
        Assert.Equal("DataGateway.Core.Catalog", detail.Namespace);
        Assert.True(detail.IsPublic && detail.IsSealed && detail.IsClass);
        Assert.Equal(new[] { "Provider", "Rows" }, detail.GetProperties().Select(p => p.Name).Order());
        Assert.Equal(typeof(string), detail.GetProperty("Provider")!.PropertyType);
        Assert.Equal(typeof(CatalogRows), detail.GetProperty("Rows")!.PropertyType);
        Assert.All(detail.GetProperties(), p => { Assert.True(p.GetMethod!.IsPublic); Assert.Null(p.SetMethod); });
        Assert.Equal(new[] { typeof(string), typeof(CatalogRows) }, Assert.Single(detail.GetConstructors()).GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(object), detail.GetMethod("ToString", Type.EmptyTypes)!.DeclaringType);
        foreach (var type in new[] { typeof(CatalogRows), typeof(DataSourceRow), typeof(DataSourceColumnRow), typeof(SourceListRow) })
        {
            Assert.True(type.IsPublic);
            Assert.Contains(type.GetConstructors(), c => c.IsPublic && !c.GetParameters().Any(p => p.ParameterType == type));
        }

        Assert.NotNull(typeof(CatalogRows).GetConstructor([typeof(DataSourceRow), typeof(IReadOnlyList<DataSourceColumnRow>)]));
        Assert.NotNull(typeof(DataSourceRow).GetConstructor([typeof(string), typeof(string)]));
        Assert.NotNull(typeof(SourceListRow).GetConstructor([typeof(string), typeof(string), typeof(string), typeof(string), typeof(bool), typeof(int)]));
        Assert.NotNull(typeof(DataSourceColumnRow).GetConstructor([typeof(string), typeof(int), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool)]));

        void CheckMethod(string name, Type result, Type[] parameters)
        {
            var method = Assert.Single(methods, m => m.Name == name);
            Assert.True(method.IsPublic && method.IsAbstract && !method.IsStatic && !method.IsGenericMethod);
            Assert.Equal(result, method.ReturnType);
            Assert.Equal(parameters, method.GetParameters().Select(p => p.ParameterType));
            Assert.All(method.GetParameters(), p => { Assert.False(p.IsOptional); Assert.False(p.HasDefaultValue); });
        }
    }

    [Fact]
    public async Task Application_queries_feed_list_describe_and_query_projections()
    {
        var example = new ExampleCatalog(NewDb());
        CatalogService<SqliteConnection> service = example;
        var list = await service.QueryCatalogAsync(CancellationToken.None);
        var detail = Assert.IsType<CatalogDetail>(await service.QueryCatalogDetailAsync("widgets", CancellationToken.None));
        Assert.Equal("fixture-target", detail.Provider);
        var source = detail.Rows.Source;
        Assert.Equal(7319, source.Id);
        Assert.Equal("main.measurements", source.ObjectName);
        Assert.Equal(ConnectionSentinel, source.ConnectionString);
        Assert.Equal("widgets", source.Name);
        Assert.Equal("Widgets", source.Title);
        Assert.Equal("Synthetic measurements", source.Description);
        Assert.Equal("One measurement", source.Grain);
        Assert.True(source.IsActive);
        Assert.Equal(7, source.RowCap);
        Assert.Equal(new SourceListRow("widgets", "Widgets", "Synthetic measurements", "One measurement", true, 7), Assert.Single(list));
        // These independent expectations pin every nullable field and each independent capability bit.
        Assert.Equal(new DataSourceColumnRow[]
        {
            new("amount", 2, "Amount", "Measurement", "number", "units", "one", "0.00", true, true, true, true, true),
            new("label", 1, "Label", "Kind", "text", null, null, null, true, true, true, false, false),
            new("observed", 3, "Observed", "Observation date", "date", null, null, null, true, false, true, false, false),
            new("enabled", 4, "Enabled", "Availability", "bool", null, null, null, true, true, false, true, false),
            new("notes", 5, "Notes", "Documentation only", "text", null, null, null, false, false, false, false, false)
        }, detail.Rows.Columns);
        var mapped = CatalogMapping.Map(detail.Rows);
        Assert.True(mapped.IsSuccess);
        var contract = mapped.Value;
        Assert.Equal(new[] { "label", "amount", "observed", "enabled", "notes" }, contract.Columns.Select(c => c.Name));
        Assert.Equal(ConnectionSentinel, contract.ConnectionString);
        var metadata = new SourceMetadata("widgets", "Widgets", "Synthetic measurements", "One measurement", 7);
        Assert.Equal(metadata, Assert.Single(SourceListing.Map(list).Sources));
        var inactive = new SourceListRow("sleeping", "Sleeping", "Inactive fixture", "One row", false, 3);
        var raw = new RawCatalog(list.Concat([inactive]).ToArray(), detail);
        var rawList = await raw.QueryCatalogAsync(CancellationToken.None);
        Assert.Contains(inactive, rawList);
        Assert.Equal(metadata, Assert.Single(SourceListing.Map(rawList).Sources));
        var limits = QueryLimits.ForRowCap(7);
        var description = SourceDescription.Map(contract, limits);
        Assert.Equal(metadata, description.Source);
        Assert.Equal(contract.Columns, description.QuerySchema.Columns);
        Assert.Equal(7, description.QuerySchema.Limit.Maximum);
        var notes = Assert.Single(description.QuerySchema.Columns, c => c.Name == "notes");
        Assert.False(notes.CanSelect || notes.CanFilter || notes.CanSort || notes.CanGroup || notes.CanAggregate);
        var validated = QueryBuilding.Validate(contract, "{\"select\":[\"label\",\"amount\"],\"limit\":2}", limits);
        Assert.True(validated.IsSuccess);
        Assert.Equal(new[] { new OutputColumn("label", "text"), new OutputColumn("amount", "number") }, validated.Value.Columns);
        Assert.Equal(2, validated.Value.RowLimit.Keep);
        Assert.Equal(3, validated.Value.RowLimit.Fetch);
        foreach (var forbidden in new[]
        {
            """{"select":["notes"]}""",
            """{"filters":[{"col":"notes","op":"eq","value":"example"}]}""",
            """{"order_by":[{"col":"notes","dir":"asc"}]}""",
            """{"group_by":["notes"]}""",
            """{"aggregates":[{"fn":"count","col":"notes"}]}"""
        }) Assert.False(QueryBuilding.Validate(contract, forbidden, limits).IsSuccess);
        var answer = QueryAnswer.Map(contract, validated.Value, new QueryRows(
            [new Dictionary<string, object?> { ["label"] = "example", ["amount"] = 12m }], false));
        Assert.Equal("widgets", answer.Source);
        Assert.Equal(new[] { new OutputColumn("label", "text"), new OutputColumn("amount", "number") }, answer.Columns);
        var row = Assert.Single(answer.Rows);
        Assert.Equal(new[] { "label", "amount" }, row.Keys);
        Assert.Equal("example", row["label"]);
        Assert.Equal(12m, row["amount"]);
        Assert.Equal(1, answer.RowCount);
        Assert.False(answer.More);
        Assert.Equal(QueryResponse.CompleteSet, answer.Hint);
    }

    [Fact]
    public async Task Lookup_preserves_input_and_absence_without_normalization()
    {
        var example = new ExampleCatalog(NewDb());
        CatalogService<SqliteConnection> service = example;
        foreach (var name in new[] { "widgets", "WiDgEtS", " widgets", "widgets ", " widgets ", "\twidgets\n", "", " ", "missing" })
        {
            var describe = SourceDescription.ParseRequest(JsonSerializer.SerializeToElement(new { source = name }));
            var query = QueryAnswer.ParseRequest(JsonSerializer.SerializeToElement(new { source = name, query = new { } }));
            Assert.True(describe.IsSuccess);
            Assert.True(query.IsSuccess);
            Assert.Equal(name, describe.Value);
            Assert.Equal(name, query.Value.Source);
            foreach (var parsed in new[] { describe.Value, query.Value.Source })
            {
                var detail = await service.QueryCatalogDetailAsync(parsed, CancellationToken.None);
                Assert.Equal(name, example.LastSourceName);
                if (name is "widgets" or "WiDgEtS") Assert.Equal("widgets", Assert.IsType<CatalogDetail>(detail).Rows.Source.Name);
                else Assert.Null(detail);
            }
        }
        Assert.Equal(" fixture-target ", new CatalogDetail(" fixture-target ", FixtureRows("widgets")).Provider);
        Assert.Empty(await new RawCatalog([], null).QueryCatalogAsync(CancellationToken.None));
        Assert.Null(await new RawCatalog(null!, null).QueryCatalogAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_results_remain_visible_to_the_future_validator()
    {
        Assert.Null(await new RawCatalog(null!, null).QueryCatalogAsync(CancellationToken.None));
        Assert.Null(await new RawCatalog([], null).QueryCatalogDetailAsync("anything", CancellationToken.None));
        Assert.Empty(await new RawCatalog([], null).QueryCatalogAsync(CancellationToken.None));
        IReadOnlyList<SourceListRow> nullElement = new SourceListRow[] { null! };
        Assert.Null(Assert.Single(await new RawCatalog(nullElement, null).QueryCatalogAsync(CancellationToken.None)));
        var lists = new[]
        {
            new SourceListRow("widgets", "", "", "", true, 7),
            new SourceListRow("widgets", "", "", "", true, 7),
            new SourceListRow("WiDgEtS", "", "", "", true, 7),
            new SourceListRow("", "", "", "", true, 0),
            new SourceListRow(" ", "", "", "", false, -1)
        };
        Assert.Same(lists, await new RawCatalog(lists, null).QueryCatalogAsync(CancellationToken.None));
        Assert.Equal(new[] { "widgets", "widgets", "WiDgEtS", "", " " }, lists.Select(r => r.Name));
        Assert.False(lists[4].IsActive);
        Assert.Equal(new[] { 7, 7, 7, 0, -1 }, lists.Select(r => r.RowCap));
        var nullProvider = await Detail(new CatalogDetail(null!, FixtureRows("widgets")));
        Assert.Null(nullProvider.Provider);
        Assert.Null((await Detail(new CatalogDetail("fixture-target", null!))).Rows);
        Assert.Null((await Detail(new CatalogDetail("fixture-target", new CatalogRows(null!, [])))).Rows.Source);
        foreach (var invalid in new[] { "", " " })
        {
            Assert.Equal(invalid, (await Detail(new CatalogDetail(invalid, FixtureRows("widgets")))).Provider);
            var badName = new CatalogRows(InvalidSource(invalid, "fixture", 7), FixtureRows("widgets").Columns);
            Assert.Equal(invalid, (await Detail(new CatalogDetail("fixture-target", badName))).Rows.Source.Name);
            var badConnection = new CatalogRows(InvalidSource("widgets", invalid, 7), FixtureRows("widgets").Columns);
            Assert.Equal(invalid, (await Detail(new CatalogDetail("fixture-target", badConnection))).Rows.Source.ConnectionString);
            var column = FixtureRows("widgets").Columns[0] with { ColumnName = invalid };
            var badColumn = new CatalogRows(FixtureRows("widgets").Source, [column]);
            Assert.Equal(invalid, Assert.Single((await Detail(new CatalogDetail("fixture-target", badColumn))).Rows.Columns).ColumnName);
        }
        foreach (var cap in new[] { 0, -1 })
            Assert.Equal(cap, (await Detail(new CatalogDetail("fixture-target", new CatalogRows(InvalidSource("widgets", "fixture", cap), [])))).Rows.Source.RowCap);
        Assert.Empty((await Detail(new CatalogDetail("fixture-target", new CatalogRows(FixtureRows("widgets").Source, [])))).Rows.Columns);
        Assert.Null(Assert.Single((await Detail(new CatalogDetail("fixture-target", new CatalogRows(FixtureRows("widgets").Source, [null!])))).Rows.Columns));
        var first = FixtureRows("widgets").Columns[0];
        var duplicates = new[] { first, first with { Ordinal = 4 }, first with { ColumnName = "AMOUNT" } };
        Assert.Equal(duplicates, (await Detail(new CatalogDetail("fixture-target", new CatalogRows(FixtureRows("widgets").Source, duplicates)))).Rows.Columns);
        var retained = new[] { first };
        var snapshot = new CatalogRows(FixtureRows("widgets").Source, retained);
        retained[0] = first with { ColumnName = "replacement" };
        Assert.Same(first, Assert.Single(snapshot.Columns));
        var mutableView = Assert.IsAssignableFrom<IList<DataSourceColumnRow>>(snapshot.Columns);
        Assert.Throws<NotSupportedException>(() => mutableView[0] = retained[0]);

        static async Task<CatalogDetail> Detail(CatalogDetail value) =>
            Assert.IsType<CatalogDetail>(await new RawCatalog([], value).QueryCatalogDetailAsync("widgets", CancellationToken.None));
    }

    [Fact]
    public async Task Secret_bearing_shapes_never_format_or_serialize_the_connection()
    {
        var example = new ExampleCatalog(NewDb());
        var list = await example.QueryCatalogAsync(CancellationToken.None);
        var detail = Assert.IsType<CatalogDetail>(await example.QueryCatalogDetailAsync("widgets", CancellationToken.None));
        var contract = CatalogMapping.Map(detail.Rows).Value;
        var limits = QueryLimits.ForRowCap(7);
        var listing = SourceListing.Map(list);
        var description = SourceDescription.Map(contract, limits);
        var query = QueryBuilding.Validate(contract, "{\"select\":[\"label\",\"amount\"],\"limit\":2}", limits).Value;
        var answer = QueryAnswer.Map(contract, query, new QueryRows([new Dictionary<string, object?> { ["label"] = "example", ["amount"] = 12m }], false));
        object[] values = [detail, detail.Rows, detail.Rows.Source, .. detail.Rows.Columns, .. list, contract, listing, description, answer];
        JsonSerializerOptions[] options = [new(), JsonSerializerOptions.Web,
            new() { IncludeFields = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never }];
        foreach (var value in values)
        {
            Assert.DoesNotContain(ConnectionSentinel, value.ToString()!);
            Assert.DoesNotContain(ConnectionSentinel, $"{value}");
            foreach (var option in options)
            {
                var json = JsonSerializer.Serialize(value, value.GetType(), option);
                Assert.DoesNotContain(ConnectionSentinel, json);
                Assert.DoesNotContain("ConnectionString", json, StringComparison.OrdinalIgnoreCase);
            }
        }
        Assert.DoesNotContain(ConnectionSentinel, new InvalidOperationException($"Invalid detail: {detail}").ToString());
        foreach (var option in options)
        {
            var json = JsonSerializer.SerializeToElement(detail, option);
            var rows = json.GetProperty(option.PropertyNamingPolicy is null ? "Rows" : "rows");
            Assert.Contains("widgets", json.GetRawText());
            var columns = rows.GetProperty(option.PropertyNamingPolicy is null ? "Columns" : "columns");
            Assert.Equal(5, columns.GetArrayLength());
            Assert.Equal(new[] { "amount", "label", "observed", "enabled", "notes" }, columns.EnumerateArray()
                .Select(c => c.GetProperty(option.PropertyNamingPolicy is null ? "ColumnName" : "columnName").GetString()));
            var source = JsonSerializer.SerializeToElement(detail.Rows.Source, option);
            Assert.DoesNotContain(source.EnumerateObject(), p => p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("ObjectName", StringComparison.OrdinalIgnoreCase));
        }
        Members(detail.Rows.Source, "name", "title", "description", "grain", "isActive", "rowCap");
        Members(contract, "source", "columns");
        Members(listing, "sources");
        Members(Assert.Single(listing.Sources), "name", "title", "description", "grain", "rowCap");
        Members(description, "source", "querySchema");
        Members(answer, "source", "columns", "rows", "rowCount", "more", "hint");
        Assert.Equal(ConnectionSentinel, detail.Rows.Source.ConnectionString);
        Assert.Equal(ConnectionSentinel, contract.ConnectionString);
        Assert.Equal("{}", JsonSerializer.Serialize<CatalogService<SqliteConnection>>(example));
        foreach (var type in new[] { typeof(CatalogDetail), typeof(CatalogService<>) })
        {
            Assert.Empty(type.GetCustomAttributes<DebuggerDisplayAttribute>());
            Assert.Empty(type.GetCustomAttributes<DebuggerTypeProxyAttribute>());
        }

        static void Members(object value, params string[] expected) => Assert.Equal(expected.Order(),
            JsonSerializer.SerializeToElement(value, value.GetType(), JsonSerializerOptions.Web).EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Both_queries_observe_precancelled_tokens()
    {
        var example = new ExampleCatalog(NewDb());
        CatalogService<SqliteConnection> service = example;
        using var listCancellation = new CancellationTokenSource();
        listCancellation.Cancel();
        var listError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.QueryCatalogAsync(listCancellation.Token));
        Assert.Equal(listCancellation.Token, listError.CancellationToken);
        Assert.Equal(listCancellation.Token, example.LastToken);
        using var detailCancellation = new CancellationTokenSource();
        detailCancellation.Cancel();
        var detailError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.QueryCatalogDetailAsync("widgets", detailCancellation.Token));
        Assert.Equal(detailCancellation.Token, detailError.CancellationToken);
        Assert.Equal(detailCancellation.Token, example.LastToken);
    }

    [Fact]
    public async Task Both_queries_propagate_cancellation_after_entry()
    {
        foreach (var isDetail in new[] { false, true })
        {
            var gated = new GatedCatalog();
            CatalogService<SqliteConnection> service = gated;
            using var cancellation = new CancellationTokenSource();
            Task operation = isDetail ? service.QueryCatalogDetailAsync("widgets", cancellation.Token) : service.QueryCatalogAsync(cancellation.Token);
            await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(operation.IsCompleted);
            Assert.Equal(cancellation.Token, gated.LastToken);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
    }

    [Fact]
    public void Core_and_data_remain_driver_free_in_all_dependency_views()
    {
        foreach (var name in new[] { "Microsoft.Data.SqlClient", "System.Data.SqlClient", "Npgsql", "Microsoft.Data.Sqlite", "SQLitePCLRaw.core", "DataGateway.Data.SqlServer" })
            Assert.True(Driver(name));
        foreach (var name in new[] { "Dapper.Data", "System.Data.Common", "SqlKata.JsonQuery" }) Assert.False(Driver(name));
        foreach (var assembly in new[] { typeof(CatalogDetail).Assembly, typeof(CatalogService<>).Assembly, typeof(DataGateway.Failures.FailureLog).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => Driver(reference.Name!));
        foreach (var layer in new[] { "DataGateway.Core", "DataGateway.Data", "DataGateway" })
        {
            using var assets = JsonDocument.Parse(File.ReadAllBytes(TestFiles.InRepo(layer + "/obj/project.assets.json")));
            var targets = assets.RootElement.GetProperty("targets").EnumerateObject().ToArray();
            Assert.NotEmpty(targets);
            foreach (var target in targets) CheckEntries(target.Value.EnumerateObject().Select(p => p.Name));
            CheckEntries(assets.RootElement.GetProperty("libraries").EnumerateObject().Select(p => p.Name));
            void CheckEntries(IEnumerable<string> entries)
            {
                var names = entries.ToArray();
                Assert.DoesNotContain(names, name => Driver(name.Split('/')[0]));
                Assert.Equal(layer is "DataGateway.Data" or "DataGateway" ? new[] { "Dapper.Data/3.1.2", "Dapper/2.1.89" } : Array.Empty<string>(),
                    names.Where(n => n.StartsWith("Dapper", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal));
            }
        }
    }

    private static bool Driver(string name) => name.Contains("SqlClient", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) || name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("DataGateway.Data.", StringComparison.OrdinalIgnoreCase);

    private static DbContext<SqliteConnection> NewDb() => new("Data Source=:memory:");

    private static DataSourceRow InvalidSource(string name, string connection, int cap) => new("main.measurements", connection)
    {
        Id = 7319, Name = name, Title = "Widgets", Description = "Synthetic measurements",
        Grain = "One measurement", IsActive = true, RowCap = cap
    };

    private sealed class RawCatalog(IReadOnlyList<SourceListRow> list, CatalogDetail? detail) : CatalogService<SqliteConnection>(NewDb())
    {
        public override Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(CancellationToken cancellationToken) => Task.FromResult(list);
        public override Task<CatalogDetail?> QueryCatalogDetailAsync(string sourceName, CancellationToken cancellationToken) => Task.FromResult(detail);
    }

    private sealed class GatedCatalog() : CatalogService<SqliteConnection>(NewDb())
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken LastToken { get; private set; }
        public override async Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            Entered.SetResult(true);
            await Pending.Task.WaitAsync(cancellationToken);
            return [];
        }
        public override async Task<CatalogDetail?> QueryCatalogDetailAsync(string sourceName, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            Entered.SetResult(true);
            await Pending.Task.WaitAsync(cancellationToken);
            return null;
        }
    }

    private const string ConnectionSentinel = "catalog-contract-connection-sentinel-6a";

    private static CatalogRows FixtureRows(string name) => new(
        new DataSourceRow("main.measurements", ConnectionSentinel)
        {
            Id = 7319, Name = name, Title = "Widgets", Description = "Synthetic measurements",
            Grain = "One measurement", IsActive = true, RowCap = 7
        },
        [
            new("amount", 2, "Amount", "Measurement", "number", "units", "one", "0.00",
                true, true, true, true, true),
            new("label", 1, "Label", "Kind", "text", null, null, null,
                true, true, true, false, false),
            new("observed", 3, "Observed", "Observation date", "date", null, null, null,
                true, false, true, false, false),
            new("enabled", 4, "Enabled", "Availability", "bool", null, null, null,
                true, true, false, true, false),
            new("notes", 5, "Notes", "Documentation only", "text", null, null, null,
                false, false, false, false, false)
        ]);

    private sealed class ExampleCatalog(DbContext<SqliteConnection> db) : CatalogService<SqliteConnection>(db)
    {
        public const string TargetProvider = "fixture-target";
        public DbContext<SqliteConnection> SuppliedDb => Db;
        public string? LastSourceName { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public override async Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            var rows = await Db.QueryAsync("SELECT @name AS name;", new { name = "widgets" },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return Array.AsReadOnly(rows.Select(row =>
            {
                var name = (string)((IDictionary<string, object?>)row)["name"]!;
                var source = FixtureRows(name).Source;
                return new SourceListRow(source.Name, source.Title, source.Description, source.Grain,
                    source.IsActive, source.RowCap);
            }).ToArray());
        }

        public override async Task<CatalogDetail?> QueryCatalogDetailAsync(string sourceName, CancellationToken cancellationToken)
        {
            LastSourceName = sourceName;
            LastToken = cancellationToken;
            var rows = await Db.QueryAsync("SELECT @name AS name WHERE @source = @name COLLATE NOCASE;",
                new { name = "widgets", source = sourceName }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (rows.Count == 0) return null;
            var name = (string)((IDictionary<string, object?>)rows[0])["name"]!;
            return new CatalogDetail(TargetProvider, FixtureRows(name));
        }
    }
}
