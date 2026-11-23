using System.Collections;
using System.Reflection;
using System.Text.Json;
using DataGateway.Core.Catalog;
using SqlKata.JsonQuery;
using Xunit;

namespace DataGateway.Core.Tests;

/// <summary>Pins <see cref="CatalogMapping.Map"/>: how a source's catalog rows become its contract, the public
/// metadata plus the columns a query may use for each clause.</summary>
/// <remarks>
/// <para>A column's five flags (select, filter, sort, group, aggregate) decide which query clauses may name it. A
/// column with every flag off stays in the described metadata but is absent from every query clause.</para>
/// <para>The connection string and object name reach server code only: they never appear in the contract's JSON or
/// its text form.</para>
/// </remarks>
public sealed class CatalogMappingTests
{
    /// <summary>Every supported data type paired with each of the 32 flag combinations.</summary>
    public static IEnumerable<object[]> SupportedMatrix()
    {
        foreach (var type in new[] { "text", "number", "date", "bool" })
            for (var flags = 0; flags < 32; flags++) yield return [type, flags];
    }

    /// <summary>Each type and flag combination maps to the exact metadata and query capabilities, and the generated
    /// query schema offers the column in exactly the clauses its flags allow.</summary>
    [Theory]
    [MemberData(nameof(SupportedMatrix))]
    public void T3_supported_types_and_all_32_flag_combinations_map_exactly(string type, int flags)
    {
        var row = Column(type, flags);
        var source = Source();
        var companion = Column("text", 1) with { ColumnName = "companion", Ordinal = 2 };
        var result = CatalogMapping.Map(new(source, flags == 0 ? [row, companion] : [row]));
        Assert.True(result.IsSuccess, result.Failure?.Message);
        var contract = result.Value;
        Assert.Equal(new SourceMetadata(source.Name, source.Title, source.Description, source.Grain, source.RowCap), contract.Source);
        Assert.Equal(source.ConnectionString, contract.ConnectionString);
        Assert.Equal(source.ObjectName, contract.ObjectName);
        var metadata = flags == 0
            ? Assert.Single(contract.Columns, c => c.Name == row.ColumnName)
            : Assert.Single(contract.Columns);
        Assert.Equal(new ColumnMetadata(row.ColumnName, row.Title, row.Description, row.DataType,
            row.Unit, row.Magnitude, row.DisplayFormat,
            row.CanSelect, row.CanFilter, row.CanSort, row.CanGroup, row.CanAggregate), metadata);

        // The fixture's bit order deliberately differs from the library's Group/Sort order.
        var expected = QueryCapability.None;
        if ((flags & 1) != 0) expected |= QueryCapability.Select;
        if ((flags & 2) != 0) expected |= QueryCapability.Filter;
        if ((flags & 4) != 0) expected |= QueryCapability.Sort;
        if ((flags & 8) != 0) expected |= QueryCapability.Group;
        if ((flags & 16) != 0) expected |= QueryCapability.Aggregate;
        if (flags == 0)
            Assert.DoesNotContain(Inspect(contract.QueryColumns), c => c.Name == "measure");
        else
        {
            var column = Assert.Single(Inspect(contract.QueryColumns));
            Assert.Equal("measure", column.Name);
            Assert.Equal(type, column.Type.ToLowerInvariant());
            Assert.Equal(expected, column.Capabilities);
        }

        // Exercise the public schema as well as inspecting the exact internal type/capability pair.
        var schema = JsonQuerySchema.Create(contract.QueryColumns);
        var properties = schema.GetProperty("properties");
        Assert.Equal(row.CanSelect, ContainsToken(properties.GetProperty("select"), "measure"));
        Assert.Equal(row.CanFilter, ContainsToken(schema.GetProperty("$defs").GetProperty("filter"), "measure"));
        Assert.Equal(row.CanSort, ContainsToken(properties.GetProperty("order_by"), "measure"));
        Assert.Equal(row.CanGroup, ContainsToken(properties.GetProperty("group_by"), "measure"));
        Assert.Equal(row.CanAggregate, ContainsToken(properties.GetProperty("aggregates"), "measure"));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("number")]
    [InlineData("date")]
    [InlineData("bool")]
    public void T2_all_off_column_keeps_order_and_metadata_but_is_absent_from_every_query_clause(string type)
    {
        var middle = Column(type, 0) with { Ordinal = 2 };
        var first = Column("text", 31) with { ColumnName = "first", Ordinal = 1 };
        var last = Column("number", 31) with { ColumnName = "last", Ordinal = 3 };
        var result = CatalogMapping.Map(new(Source(), [last, middle, first]));
        Assert.True(result.IsSuccess, result.Failure?.Message);
        var contract = result.Value;
        Assert.Equal(new[] { "first", "measure", "last" }, contract.Columns.Select(c => c.Name));
        Assert.Equal(new ColumnMetadata(middle.ColumnName, middle.Title, middle.Description, middle.DataType,
            middle.Unit, middle.Magnitude, middle.DisplayFormat, false, false, false, false, false), contract.Columns[1]);
        Assert.Equal(new[] { "first", "last" }, Inspect(contract.QueryColumns).Select(c => c.Name));
        Assert.DoesNotContain(Inspect(contract.QueryColumns), c => c.Capabilities == QueryCapability.None);
        Assert.False(ContainsToken(JsonQuerySchema.Create(contract.QueryColumns), "measure"));
        foreach (var query in new[]
        {
            """{"select":["measure"]}""",
            """{"filters":[{"col":"measure","op":"eq","value":1}]}""",
            """{"order_by":[{"col":"measure","dir":"asc"}]}""",
            """{"group_by":["measure"]}""",
            """{"aggregates":[{"fn":"count","col":"measure"}]}"""
        })
        {
            var error = Assert.Throws<JsonQueryException>(() =>
                new global::SqlKata.Query("synthetic").WithJsonQuery(query, contract.QueryColumns));
            Assert.Equal(JsonQueryErrorCode.UnknownColumn, error.Code);
        }
    }

    /// <summary>A source whose columns all have every flag off is an invalid catalog: nothing could be queried.</summary>
    [Fact]
    public void T4_source_with_only_all_off_columns_requires_switching_on_a_flag()
    {
        var result = CatalogMapping.Map(new(Source(), [Column("number", 0),
            Column("text", 0) with { ColumnName = "other" }]));
        Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Equal("InvalidCatalog", result.Failure!.Code);
        Assert.Equal("Switch on at least one flag in the catalog before using this source.", result.Failure.Message);
    }

    /// <summary>A column with every flag off is still checked for a bare identifier name, a unique name and a supported
    /// type, so a bad row cannot hide behind switched-off flags.</summary>
    [Theory]
    [InlineData("bad.name", "number", "bare ASCII SQL identifier")]
    [InlineData("COMPANION", "number", "duplicated ignoring case")]
    [InlineData("measure", "fake-private-type", "unsupported data_type")]
    public void T5_all_off_columns_still_validate_names_duplicates_and_types(string name, string type, string message)
    {
        var companion = Column("text", 1) with { ColumnName = "companion", Ordinal = 0 };
        var result = CatalogMapping.Map(new(Source(), [companion, Column(type, 0) with { ColumnName = name }]));
        Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Equal("InvalidCatalog", result.Failure!.Code);
        Assert.Contains(message, result.Failure.Message);
        Assert.DoesNotContain("fake-private-type", result.Failure.ToString());
    }

    [Fact]
    public void T2_unknown_data_type_returns_a_failure_naming_the_column_without_echoing_the_value()
    {
        var result = CatalogMapping.Map(new(Source(), [Column("fake-private-type", 31)]));
        Assert.False(result.IsSuccess);
        Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Equal("InvalidCatalog", result.Failure!.Code);
        Assert.Contains("measure", result.Failure.Message);
        Assert.DoesNotContain("fake-private-type", result.Failure.ToString());
        Assert.DoesNotContain("fake-password", result.Failure.ToString());
    }

    /// <summary>Columns come out in ordinal order, the catalog rows keep their own copy of the input, null unit,
    /// magnitude and format stay null, and mapping twice gives the same result.</summary>
    [Fact]
    public void Mapping_orders_columns_without_mutating_input_and_preserves_nullable_metadata()
    {
        var later = Column("date", 31) with { ColumnName = "later", Ordinal = 9, Unit = null,
            Magnitude = null, DisplayFormat = null };
        var earlier = Column("text", 1) with { ColumnName = "earlier", Ordinal = 2 };
        var input = new[] { later, earlier };
        var rows = new CatalogRows(Source(), input);
        input[0] = earlier;
        var mapped = CatalogMapping.Map(rows);
        Assert.True(mapped.IsSuccess, mapped.Failure?.Message);
        var contract = mapped.Value;
        Assert.Equal(new[] { "earlier", "later" }, contract.Columns.Select(c => c.Name));
        Assert.Equal("later", rows.Columns[0].ColumnName);
        Assert.Null(contract.Columns[1].Unit);
        Assert.Null(contract.Columns[1].Magnitude);
        Assert.Null(contract.Columns[1].DisplayFormat);
        var again = CatalogMapping.Map(rows);
        Assert.True(again.IsSuccess, again.Failure?.Message);
        Assert.Equal(contract.Columns.ToArray(), again.Value.Columns.ToArray());
    }

    [Fact]
    public void Invalid_names_duplicates_empty_and_inactive_rows_are_failures()
    {
        var first = Column("text", 1);
        foreach (var columns in new DataSourceColumnRow[][]
                 { [], [first with { ColumnName = "bad.name" }], [first, first with { ColumnName = "MEASURE" }] })
            Assert.Equal("InvalidCatalog", CatalogMapping.Map(new(Source(), columns)).Failure!.Code);
        Assert.Equal("UnknownSource", CatalogMapping.Map(new(Source(false), [first])).Failure!.Code);
    }

    /// <summary>The connection string and object name are readable by server code but never serialized or printed.</summary>
    [Fact]
    public void Internals_are_available_to_server_code_and_absent_from_default_json_and_ToString()
    {
        var source = Source();
        var rows = new CatalogRows(source, [Column("number", 31)]);
        var mapped = CatalogMapping.Map(rows);
        Assert.True(mapped.IsSuccess, mapped.Failure?.Message);
        var contract = mapped.Value;
        var output = JsonSerializer.Serialize(contract) + JsonSerializer.Serialize(rows) + source + contract;
        Assert.Equal(source.ConnectionString, contract.ConnectionString);
        foreach (var hidden in new[] { source.ConnectionString, source.ObjectName, "fake-password",
                     "ConnectionString", "ObjectName", "IsActive", "Ordinal", "QueryColumns" })
            Assert.DoesNotContain(hidden, JsonSerializer.Serialize(contract) + contract);
        Assert.DoesNotContain("fake-password", output);
    }

    /// <summary>A <see cref="CatalogResult{T}"/> is either a value or a failure; reading the value of a failure throws.</summary>
    [Fact]
    public void result_has_exactly_one_branch_and_rejected_value_throws()
    {
        var failure = new CatalogFailure("InvalidCatalog", "$.source", "Synthetic failure");
        var rejected = CatalogResult<string>.Reject(failure);
        Assert.False(rejected.IsSuccess);
        Assert.Same(failure, rejected.Failure);
        Assert.Throws<InvalidOperationException>(() => rejected.Value);
        var success = CatalogResult<string>.Success("synthetic");
        Assert.True(success.IsSuccess);
        Assert.Equal("synthetic", success.Value);
        Assert.Null(success.Failure);
    }

    /// <summary>A synthetic source row; its connection string is a marker the tests search output for.</summary>
    /// <param name="active">Whether the source is active in the catalog.</param>
    private static DataSourceRow Source(bool active = true) => new("dbo.synthetic_observations", "fake-password")
    {
        Id = 7319, Name = "synthetic_observations", Title = "Synthetic observations", Description = "Test measurements.",
        QueryTimeoutSeconds = 30, Grain = "One synthetic measurement per day.", RowCap = 500, IsActive = active
    };

    /// <summary>A synthetic column named <c>measure</c> with the given type and flags.</summary>
    /// <param name="type">The catalog data type.</param>
    /// <param name="flags">Bits 1, 2, 4, 8 and 16 switch on select, filter, sort, group and aggregate.</param>
    private static DataSourceColumnRow Column(string type, int flags) => new(
        "measure", 1, "Synthetic measure", "Meaning and aggregation guidance.", type,
        "widgets", "thousands", "N2", (flags & 1) != 0, (flags & 2) != 0,
        (flags & 4) != 0, (flags & 8) != 0, (flags & 16) != 0);

    /// <summary>Reads the name, type and capabilities of each query column through reflection.</summary>
    /// <remarks>The library keeps its ordered columns internal; reading them pins the exact type and capability pair,
    /// which the public schema alone does not show.</remarks>
    private static IEnumerable<(string Name, string Type, QueryCapability Capabilities)> Inspect(QueryColumns columns)
    {
        var ordered = (IEnumerable)typeof(QueryColumns).GetProperty("Ordered", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(columns)!;
        foreach (var column in ordered)
        {
            var type = column.GetType();
            yield return ((string)type.GetProperty("Name")!.GetValue(column)!,
                type.GetProperty("Type")!.GetValue(column)!.ToString()!,
                (QueryCapability)type.GetProperty("Capabilities")!.GetValue(column)!);
        }
    }

    /// <summary>Whether <paramref name="token"/> appears as a string anywhere in <paramref name="element"/>.</summary>
    private static bool ContainsToken(JsonElement element, string token) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() == token,
        JsonValueKind.Array => element.EnumerateArray().Any(item => ContainsToken(item, token)),
        JsonValueKind.Object => element.EnumerateObject().Any(item => ContainsToken(item.Value, token)),
        _ => false
    };
}
