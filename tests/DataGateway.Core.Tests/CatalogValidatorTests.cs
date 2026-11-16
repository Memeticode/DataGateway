using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataGateway.Core.Catalog;
using Xunit;

namespace DataGateway.Core.Tests;

public sealed class CatalogValidatorTests
{
    private const string ConnectionSentinel = "validator-connection-sentinel-61b";
    private const string ObjectSentinel = "validator-object-sentinel-61b";
    private const string CapCorrection = "Set rowCap to an integer from 1 through 2147483646.";
    private const string BlankName = "Set name to a non-blank value.";
    private const string TrimName = "Remove surrounding whitespace from name.";
    private const string Duplicate = "Use a source name that is unique ignoring case.";
    private const string MissingColumn = "Replace the null column entry with a column row.";
    private const string MissingColumnName = "Set columnName to a non-null value.";
    private const string Requested = "Return the requested source name, matching it ignoring case.";

    private static SourceListRow ListRow() => new("widgets", "Widgets", "Synthetic measurements", "One measurement", true, 7);
    private static DataSourceRow Source(string? name = "widgets", string? connection = ConnectionSentinel,
        string? objectName = ObjectSentinel, int cap = 7, bool active = true) => new(objectName!, connection!)
        {
            Name = name!, Title = "Widgets", Description = "Synthetic measurements", Grain = "One measurement",
            IsActive = active, RowCap = cap
        };
    private static DataSourceColumnRow Column() => new("amount", 2, "Amount", "Measurement", "number", "units", "one", "0.00",
        true, true, true, true, true);
    private static DataSourceColumnRow Notes() => new("notes", 5, "Notes", "Documentation only", "text", null, null, null,
        false, false, false, false, false);
    private static CatalogDetail Detail(DataSourceRow? source = null, IReadOnlyList<DataSourceColumnRow>? columns = null,
        string? provider = "fixture-target") => new(provider!, new(source ?? Source(), columns ?? [Column()]));
    private static CatalogFailure Expected(string path, string correction, string? name = "widgets") =>
        new("InvalidCatalog", path, name is null ? correction : "Source '" + name + "'. " + correction) { SourceName = name };

    // Expected records have independent literal corrections and null diagnostic defaults.
    private static void EqualFailures(IReadOnlyList<CatalogFailure> actual, params CatalogFailure[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Code, actual[index].Code);
            Assert.Equal(expected[index].Path, actual[index].Path);
            Assert.Equal(expected[index].Message, actual[index].Message);
            Assert.Equal(expected[index].SourceName, actual[index].SourceName);
            Assert.Null(actual[index].Step);
            Assert.Null(actual[index].SourceId);
            Assert.Null(actual[index].DriverException);
            Assert.Null(actual[index].ExceptionType);
            Assert.Null(actual[index].SqlNumber);
            Assert.Equal(expected[index], actual[index]);
        }
    }
    private static void ListFailure(SourceListRow row, string field, string correction, string? name = "widgets") =>
        EqualFailures(CatalogValidator.ValidateList([row]), Expected("$.sources[0]." + field, correction, name));
    private static void DetailFailure(CatalogDetail detail, string field, string correction, string requested = "widgets") =>
        EqualFailures(CatalogValidator.ValidateDetail(requested, detail), Expected("$.detail." + field, correction, requested));
    private static CatalogResult<SourceContract> ShapeThenMap(params DataSourceColumnRow[] columns)
    {
        var detail = Detail(columns: columns);
        Assert.Empty(CatalogValidator.ValidateDetail("widgets", detail));
        return CatalogMapping.Map(detail.Rows);
    }
    private static CatalogFailure MappingFailure(CatalogResult<SourceContract> result, string correction)
    {
        Assert.False(result.IsSuccess);
        var failure = Assert.IsType<CatalogFailure>(result.Failure);
        EqualFailures([failure], new CatalogFailure("InvalidCatalog", "$.source", correction));
        return failure;
    }

    [Fact]
    public void T1_public_entry_points_accept_existing_and_nullable_callers()
    {
        IReadOnlyList<SourceListRow> existing = [ListRow()];
        IReadOnlyList<SourceListRow?> nullable = [ListRow()];
        IReadOnlyList<CatalogFailure> first = CatalogValidator.ValidateList(existing);
        IReadOnlyList<CatalogFailure> second = CatalogValidator.ValidateList(nullable);
        IReadOnlyList<CatalogFailure> third = CatalogValidator.ValidateDetail("widgets", Detail());
        Assert.Empty(first); Assert.Empty(second); Assert.Empty(third);
        Assert.Equal(typeof(CatalogDetail).Assembly, typeof(CatalogValidator).Assembly);
        Assert.Equal("DataGateway.Core.Catalog", typeof(CatalogValidator).Namespace);
        Assert.True(typeof(CatalogValidator).IsPublic);
        Assert.True(typeof(CatalogValidator).IsAbstract && typeof(CatalogValidator).IsSealed);
        Assert.Equal("requestedName", Assert.Throws<ArgumentNullException>(() => CatalogValidator.ValidateDetail(null!, null)).ParamName);
        Assert.Equal("requestedName", Assert.Throws<ArgumentNullException>(() => CatalogValidator.ValidateDetail(null!, Detail())).ParamName);
    }

    [Fact]
    public void T2_list_must_be_present_but_may_be_empty()
    {
        EqualFailures(CatalogValidator.ValidateList(null), Expected("$.sources", "Return a non-null source list.", null));
        Assert.Empty(CatalogValidator.ValidateList([]));
    }

    [Fact]
    public void T3_null_element_does_not_stop_later_inspection()
    {
        SourceListRow?[] rows = [ListRow(), null, ListRow() with { Name = "other" }];
        var missing = Expected("$.sources[1]", "Replace the null source entry with a source row.", null);
        EqualFailures(CatalogValidator.ValidateList(rows), missing);
        rows[2] = rows[2]! with { RowCap = 0 };
        EqualFailures(CatalogValidator.ValidateList(rows), missing, Expected("$.sources[2].rowCap", CapCorrection, "other"));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("\t\r\n")] [InlineData("\u00a0")]
    public void T4_list_name_must_have_text(string? name) => ListFailure(ListRow() with { Name = name! }, "name", BlankName, null);

    [Theory]
    [InlineData(" widgets")] [InlineData("widgets ")] [InlineData("\twidgets\n")] [InlineData("\u00a0widgets\u00a0")]
    public void T5_list_name_must_be_trimmed_and_retains_original(string name)
    {
        var row = ListRow() with { Name = name };
        ListFailure(row, "name", TrimName, name);
        Assert.Equal(name, row.Name);
    }

    [Theory]
    [InlineData("widgets")] [InlineData("WIDGETS")] [InlineData("_sample1")]
    [InlineData("two words")] [InlineData("name.with.dot")] [InlineData("caf\u00e9")]
    public void T5_trimmed_source_labels_have_no_identifier_restriction(string name) =>
        Assert.Empty(CatalogValidator.ValidateList([ListRow() with { Name = name }]));

    [Fact]
    public void T6_duplicates_compare_original_non_null_names_ignoring_case()
    {
        EqualFailures(CatalogValidator.ValidateList([ListRow(), ListRow() with { Name = "WiDgEtS" }, ListRow() with { IsActive = false }]),
            Expected("$.sources[1].name", Duplicate, "WiDgEtS"), Expected("$.sources[2].name", Duplicate));
        EqualFailures(CatalogValidator.ValidateList([ListRow(), ListRow() with { Name = " widgets " }]),
            Expected("$.sources[1].name", TrimName, " widgets "));
        EqualFailures(CatalogValidator.ValidateList([ListRow() with { Name = " widgets " }, ListRow() with { Name = " WIDGETS " }]),
            Expected("$.sources[0].name", TrimName, " widgets "), Expected("$.sources[1].name", TrimName, " WIDGETS "),
            Expected("$.sources[1].name", Duplicate, " WIDGETS "));
    }

    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData(null)]
    public void T6_blank_duplicates_are_reported_without_a_source_label(string? name)
    {
        var expected = new List<CatalogFailure> { Expected("$.sources[0].name", BlankName, null), Expected("$.sources[1].name", BlankName, null) };
        if (name is not null) expected.Add(Expected("$.sources[1].name", Duplicate, null));
        EqualFailures(CatalogValidator.ValidateList([ListRow() with { Name = name! }, ListRow() with { Name = name! }]), expected.ToArray());
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(int.MinValue)] [InlineData(int.MaxValue)]
    public void T7_list_cap_obeys_the_row_cap_rule_including_inactive_rows(int cap)
    {
        ListFailure(ListRow() with { RowCap = cap }, "rowCap", CapCorrection);
        ListFailure(ListRow() with { RowCap = cap, IsActive = false }, "rowCap", CapCorrection);
    }

    [Theory]
    [InlineData(1)] [InlineData(7)] [InlineData(QueryLimits.MaxRowCap)]
    public void T7_list_cap_accepts_inclusive_bounds(int cap) => Assert.Empty(CatalogValidator.ValidateList([ListRow() with { RowCap = cap }]));

    [Fact]
    public void T8_title_requires_presence_only()
    {
        ListFailure(ListRow() with { Title = null! }, "title", "Set title to a non-null value.");
        foreach (var value in new[] { "", " " }) Assert.Empty(CatalogValidator.ValidateList([ListRow() with { Title = value }]));
    }

    [Fact]
    public void T9_description_requires_presence_only()
    {
        ListFailure(ListRow() with { Description = null! }, "description", "Set description to a non-null value.");
        foreach (var value in new[] { "", " " }) Assert.Empty(CatalogValidator.ValidateList([ListRow() with { Description = value }]));
    }

    [Fact]
    public void T10_grain_requires_presence_only()
    {
        ListFailure(ListRow() with { Grain = null! }, "grain", "Set grain to a non-null value.");
        foreach (var value in new[] { "", " " }) Assert.Empty(CatalogValidator.ValidateList([ListRow() with { Grain = value }]));
    }

    [Fact]
    public void T11_absent_lookup_is_not_malformed() => Assert.Empty(CatalogValidator.ValidateDetail("widgets", null));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("\t\r\n")]
    public void T12_provider_requires_text(string? provider) => DetailFailure(Detail(provider: provider), "provider", "Set provider to a non-blank value.");

    [Theory]
    [InlineData("fixture-target")] [InlineData(" unregistered synthetic provider ")]
    public void T12_provider_membership_and_normalization_are_outside_shape(string provider)
    {
        var detail = Detail(provider: provider);
        Assert.Empty(CatalogValidator.ValidateDetail("widgets", detail));
        Assert.Equal(provider, detail.Provider);
    }

    [Fact]
    public void T13_missing_rows_stop_descendants_after_provider()
    {
        var missing = Expected("$.detail.rows", "Return non-null rows for this detail.");
        EqualFailures(CatalogValidator.ValidateDetail("widgets", new("fixture-target", null!)), missing);
        EqualFailures(CatalogValidator.ValidateDetail("widgets", new(null!, null!)),
            Expected("$.detail.provider", "Set provider to a non-blank value."), missing);
    }

    [Fact]
    public void T14_missing_source_keeps_column_siblings_inspectable()
    {
        var missing = Expected("$.detail.rows.source", "Return a non-null source row for this detail.");
        EqualFailures(CatalogValidator.ValidateDetail("widgets", new("fixture-target", new(null!, [Column()]))), missing);
        EqualFailures(CatalogValidator.ValidateDetail("widgets", new("fixture-target", new(null!, [null!, Column(), null!]))),
            missing, Expected("$.detail.rows.columns[0]", MissingColumn), Expected("$.detail.rows.columns[2]", MissingColumn));
    }

    [Theory]
    [InlineData("other")] [InlineData(null)] [InlineData("")] [InlineData(" widgets")] [InlineData("widgets ")]
    public void T15_returned_source_must_equal_requested_label(string? returned) =>
        DetailFailure(Detail(Source(name: returned)), "rows.source.name", Requested);

    [Fact]
    public void T15_requested_name_is_used_verbatim_and_ignores_case_only()
    {
        Assert.Empty(CatalogValidator.ValidateDetail("WiDgEtS", Detail()));
        DetailFailure(Detail(), "rows.source.name", Requested, " widgets ");
        DetailFailure(Detail(), "rows.source.name", Requested, "OTHER");
        foreach (var name in new[] { "", " widgets " })
            Assert.Empty(CatalogValidator.ValidateDetail(name, Detail(Source(name: name))));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("\t\r\n")]
    public void T16_connection_requires_text(string? value) =>
        DetailFailure(Detail(Source(connection: value)), "rows.source.connectionString", "Set connectionString to a non-blank value.");

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("\t\r\n")]
    public void T17_object_requires_text(string? value) =>
        DetailFailure(Detail(Source(objectName: value)), "rows.source.objectName", "Set objectName to a non-blank value.");

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(int.MinValue)] [InlineData(int.MaxValue)]
    public void T18_detail_cap_obeys_the_row_cap_rule(int cap) => DetailFailure(Detail(Source(cap: cap)), "rows.source.rowCap", CapCorrection);

    [Theory]
    [InlineData(1)] [InlineData(7)] [InlineData(QueryLimits.MaxRowCap)]
    public void T18_detail_cap_accepts_inclusive_bounds(int cap) => Assert.Empty(CatalogValidator.ValidateDetail("widgets", Detail(Source(cap: cap))));

    [Fact]
    public void T19_null_column_paths_use_supplied_indices_instead_of_ordinals() =>
        EqualFailures(CatalogValidator.ValidateDetail("widgets", Detail(columns: [Column() with { Ordinal = 99 }, null!, Column() with { Ordinal = -2 }, null!])),
            Expected("$.detail.rows.columns[1]", MissingColumn), Expected("$.detail.rows.columns[3]", MissingColumn));

    [Fact]
    public void T20_null_column_name_is_a_shape_fault_even_without_source()
    {
        var columns = new[] { Column(), Column() with { ColumnName = null! } };
        DetailFailure(Detail(columns: columns), "rows.columns[1].columnName", MissingColumnName);
        EqualFailures(CatalogValidator.ValidateDetail("widgets", new("fixture-target", new(null!, columns))),
            Expected("$.detail.rows.source", "Return a non-null source row for this detail."),
            Expected("$.detail.rows.columns[1].columnName", MissingColumnName));
    }

    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("bad.name")] [InlineData("9lead")] [InlineData("na\u00efve")]
    public void T21_identifier_semantics_belong_to_mapping(string name) => MappingFailure(ShapeThenMap(Column() with { ColumnName = name }),
        "Correct the catalog column name: use a bare ASCII SQL identifier.");

    [Theory]
    [InlineData("amount")] [InlineData("_sample1")]
    public void T21_valid_identifiers_pass_both_stages(string name) => Assert.True(ShapeThenMap(Column() with { ColumnName = name }).IsSuccess);

    [Fact]
    public void T22_column_uniqueness_belongs_to_mapping() => MappingFailure(
        ShapeThenMap(Column() with { Ordinal = 1 }, Column() with { ColumnName = "AMOUNT", Ordinal = 2 }),
        "Column 'AMOUNT' is duplicated ignoring case. Correct the catalog.");

    [Theory]
    [InlineData("text")] [InlineData("number")] [InlineData("date")] [InlineData("bool")]
    public void T23_supported_types_pass_both_stages(string type) => Assert.True(ShapeThenMap(Column() with { DataType = type }).IsSuccess);

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("Text")] [InlineData("private-type-sentinel")]
    public void T23_type_semantics_belong_to_mapping_without_echoing_values(string? type)
    {
        var failure = MappingFailure(ShapeThenMap(Column() with { DataType = type! }),
            "Column 'amount' has an unsupported data_type. Set it to text, number, date or bool in the catalog.");
        if (type == "private-type-sentinel")
            foreach (var text in new[] { failure.Message, failure.ToString(), JsonSerializer.Serialize(failure) }) Assert.DoesNotContain(type, text);
    }

    [Fact]
    public void T24_source_wide_capability_requirement_belongs_to_mapping() => MappingFailure(ShapeThenMap(
        Notes() with { ColumnName = "amount" }, Notes()), "Switch on at least one flag in the catalog before using this source.");

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void T24_any_single_capability_allows_documentation_only_metadata(int flag)
    {
        var enabled = Notes() with { ColumnName = "amount", Ordinal = 1, CanSelect = flag == 0, CanFilter = flag == 1,
            CanSort = flag == 2, CanGroup = flag == 3, CanAggregate = flag == 4 };
        var result = ShapeThenMap(enabled, Notes());
        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(new ColumnMetadata("notes", "Notes", "Documentation only", "text", null, null, null,
            false, false, false, false, false), result.Value.Columns[1]);
    }

    [Fact]
    public void T25_column_presence_belongs_to_mapping()
    {
        MappingFailure(ShapeThenMap(), "Catalog an accessible column before using this source.");
        Assert.True(ShapeThenMap(Column()).IsSuccess);
    }

    private static SourceListRow?[] MultiList() =>
    [
        null, new(" widgets ", null!, "", null!, true, 0), new("widgets", "", "", "", true, 7),
        new("WIDGETS", "", null!, "", false, -1)
    ];
    private static CatalogFailure[] MultiListExpected() =>
    [
        Expected("$.sources[0]", "Replace the null source entry with a source row.", null),
        Expected("$.sources[1].name", TrimName, " widgets "), Expected("$.sources[1].rowCap", CapCorrection, " widgets "),
        Expected("$.sources[1].title", "Set title to a non-null value.", " widgets "),
        Expected("$.sources[1].grain", "Set grain to a non-null value.", " widgets "),
        Expected("$.sources[3].name", Duplicate, "WIDGETS"), Expected("$.sources[3].rowCap", CapCorrection, "WIDGETS"),
        Expected("$.sources[3].description", "Set description to a non-null value.", "WIDGETS")
    ];
    private static CatalogDetail MultiDetail() => Detail(Source("other", "", "", 0), [null!, Column() with { ColumnName = null! }], null);
    private static CatalogFailure[] MultiDetailExpected() =>
    [
        Expected("$.detail.provider", "Set provider to a non-blank value."), Expected("$.detail.rows.source.name", Requested),
        Expected("$.detail.rows.source.connectionString", "Set connectionString to a non-blank value."),
        Expected("$.detail.rows.source.objectName", "Set objectName to a non-blank value."),
        Expected("$.detail.rows.source.rowCap", CapCorrection), Expected("$.detail.rows.columns[0]", MissingColumn),
        Expected("$.detail.rows.columns[1].columnName", MissingColumnName)
    ];

    [Fact]
    public void T26_all_list_faults_have_fixed_order_and_repeat_exactly()
    {
        var rows = MultiList();
        EqualFailures(CatalogValidator.ValidateList(rows), MultiListExpected());
        EqualFailures(CatalogValidator.ValidateList(rows), MultiListExpected());
    }

    [Fact]
    public void T27_all_detail_faults_have_fixed_order_and_repeat_exactly()
    {
        var detail = MultiDetail();
        EqualFailures(CatalogValidator.ValidateDetail("widgets", detail), MultiDetailExpected());
        EqualFailures(CatalogValidator.ValidateDetail("widgets", detail), MultiDetailExpected());
    }

    [Fact]
    public void T28_example_catalog_and_inactive_shapes_pass()
    {
        DataSourceColumnRow[] columns =
        [
            Column(), new("label", 1, "Label", "Kind", "text", null, null, null, true, true, true, false, false),
            new("observed", 3, "Observed", "Observation date", "date", null, null, null, true, false, true, false, false),
            new("enabled", 4, "Enabled", "Availability", "bool", null, null, null, true, true, false, true, false), Notes()
        ];
        Assert.Empty(CatalogValidator.ValidateList([ListRow()]));
        var detail = Detail(columns: columns);
        Assert.Empty(CatalogValidator.ValidateDetail("WiDgEtS", detail));
        var mapped = CatalogMapping.Map(detail.Rows);
        Assert.True(mapped.IsSuccess, mapped.Failure?.Message);
        Assert.Equal(new[] { "label", "amount", "observed", "enabled", "notes" }, mapped.Value.Columns.Select(c => c.Name));
        Assert.Equal("Documentation only", mapped.Value.Columns[4].Description);
        Assert.Empty(CatalogValidator.ValidateList([ListRow() with { IsActive = false }]));
        Assert.Empty(CatalogValidator.ValidateDetail("widgets", Detail(Source(active: false), columns)));
    }

    [Fact]
    public void T29_constructor_owns_snapshot_and_rejects_null_collection()
    {
        Assert.Throws<ArgumentNullException>(() => new CatalogRows(Source(), null!));
        var original = Column();
        DataSourceColumnRow[] columns = [original, null!];
        var rows = new CatalogRows(Source(), columns);
        columns[0] = Notes();
        Assert.Same(original, rows.Columns[0]);
        EqualFailures(CatalogValidator.ValidateDetail("widgets", new("fixture-target", rows)), Expected("$.detail.rows.columns[1]", MissingColumn));
    }

    private static void AssertProjection(CatalogFailure failure, JsonElement json, bool web)
    {
        Assert.Equal(web ? new[] { "code", "path", "message" } : new[] { "Code", "Path", "Message" }, json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(failure.Code, json.GetProperty(web ? "code" : "Code").GetString());
        Assert.Equal(failure.Path, json.GetProperty(web ? "path" : "Path").GetString());
        Assert.Equal(failure.Message, json.GetProperty(web ? "message" : "Message").GetString());
    }
    private static void AssertNoSecrets(string text)
    {
        Assert.DoesNotContain(ConnectionSentinel, text);
        Assert.DoesNotContain(ObjectSentinel, text);
    }
    private static void CheckSerialization(IReadOnlyList<CatalogFailure> failures)
    {
        Assert.NotEmpty(failures);
        JsonSerializerOptions?[] options = [null, JsonSerializerOptions.Web, new() { IncludeFields = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never }];
        foreach (var settings in options)
        {
            var serialized = JsonSerializer.Serialize(failures, settings);
            AssertNoSecrets(serialized);
            using var collection = JsonDocument.Parse(serialized);
            Assert.Equal(failures.Count, collection.RootElement.GetArrayLength());
            for (var index = 0; index < failures.Count; index++)
            {
                var failure = failures[index];
                AssertProjection(failure, collection.RootElement[index], ReferenceEquals(settings, JsonSerializerOptions.Web));
                var single = JsonSerializer.Serialize(failure, settings);
                AssertNoSecrets(single);
                using var parsed = JsonDocument.Parse(single);
                AssertProjection(failure, parsed.RootElement, ReferenceEquals(settings, JsonSerializerOptions.Web));
                foreach (var text in new[] { failure.Message, failure.Path, failure.ToString(), $"{failure}" }) AssertNoSecrets(text);
            }
        }
    }

    [Fact]
    public void T30_diagnostics_and_all_JSON_modes_exclude_connection_and_object_secrets()
    {
        var columns = new[] { null!, Column() with { ColumnName = null! } };
        var failures = CatalogValidator.ValidateDetail("widgets", Detail(Source("other", cap: 0), columns, null));
        EqualFailures(failures, Expected("$.detail.provider", "Set provider to a non-blank value."),
            Expected("$.detail.rows.source.name", Requested), Expected("$.detail.rows.source.rowCap", CapCorrection),
            Expected("$.detail.rows.columns[0]", MissingColumn), Expected("$.detail.rows.columns[1].columnName", MissingColumnName));
        CheckSerialization(failures);
        foreach (var connectionBlank in new[] { true, false })
        {
            var detail = Detail(Source(connection: connectionBlank ? " " : ConnectionSentinel, objectName: connectionBlank ? ObjectSentinel : " "));
            var actual = CatalogValidator.ValidateDetail("widgets", detail);
            EqualFailures(actual, connectionBlank
                ? Expected("$.detail.rows.source.connectionString", "Set connectionString to a non-blank value.")
                : Expected("$.detail.rows.source.objectName", "Set objectName to a non-blank value."));
            CheckSerialization(actual);
        }
        var list = CatalogValidator.ValidateList([ListRow() with { RowCap = 0 }]);
        EqualFailures(list, Expected("$.sources[0].rowCap", CapCorrection));
        CheckSerialization(list);
    }

    [Fact]
    public void T31_five_thousand_rows_report_all_22500_faults_in_order()
    {
        var rows = new SourceListRow[5000];
        var expected = new List<CatalogFailure>();
        for (var pair = 0; pair < 2500; pair++)
        {
            var suffix = pair.ToString(CultureInfo.InvariantCulture);
            rows[pair * 2] = new("widgets_" + suffix, null!, null!, null!, true, 0);
            rows[pair * 2 + 1] = new("WIDGETS_" + suffix, null!, null!, null!, true, 0);
        }
        // Derive expectations from the pair pattern, without a duplicate set or reading the fixture rows.
        for (var index = 0; index < 5000; index++)
        {
            var name = (index % 2 == 0 ? "widgets_" : "WIDGETS_") + (index / 2).ToString(CultureInfo.InvariantCulture);
            var path = "$.sources[" + index.ToString(CultureInfo.InvariantCulture) + "].";
            if (index % 2 == 1) expected.Add(Expected(path + "name", Duplicate, name));
            expected.Add(Expected(path + "rowCap", CapCorrection, name));
            expected.Add(Expected(path + "title", "Set title to a non-null value.", name));
            expected.Add(Expected(path + "description", "Set description to a non-null value.", name));
            expected.Add(Expected(path + "grain", "Set grain to a non-null value.", name));
        }
        Assert.Equal(22500, expected.Count);
        EqualFailures(CatalogValidator.ValidateList(rows), expected.ToArray());
        EqualFailures(CatalogValidator.ValidateList(rows), expected.ToArray());
    }

    [Fact]
    public async Task T32_cultures_and_concurrent_calls_preserve_order_and_independence()
    {
        SourceListRow?[] rows = [.. MultiList(), .. Enumerable.Range(4, 8).Select(i => ListRow() with { Name = "item" + i.ToString(CultureInfo.InvariantCulture) }), null];
        var detail = MultiDetail();
        var expected = MultiListExpected().Append(Expected("$.sources[12]", "Replace the null source entry with a source row.", null)).ToArray();
        var oldCulture = CultureInfo.CurrentCulture;
        var oldUI = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("tr-TR"), CultureInfo.GetCultureInfo("ar-SA") })
            {
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                var result = CatalogValidator.ValidateList(rows);
                EqualFailures(result, expected);
                Assert.All(result, failure => Assert.All(failure.Path, c => Assert.True(c <= 127)));
                EqualFailures(CatalogValidator.ValidateDetail("widgets", detail), MultiDetailExpected());
                Assert.Empty(CatalogValidator.ValidateList([ListRow()]));
                Assert.Empty(CatalogValidator.ValidateDetail("widgets", Detail()));
            }
        }
        finally { CultureInfo.CurrentCulture = oldCulture; CultureInfo.CurrentUICulture = oldUI; }
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
        {
            EqualFailures(CatalogValidator.ValidateList(rows), expected);
            Assert.Empty(CatalogValidator.ValidateList([]));
            EqualFailures(CatalogValidator.ValidateDetail("widgets", detail), MultiDetailExpected());
            Assert.Empty(CatalogValidator.ValidateDetail("widgets", null));
        })));
    }

    [Fact]
    public void T33_inputs_and_completed_results_own_their_contents()
    {
        var rows = MultiList();
        var references = rows.ToArray();
        var values = rows.Select(r => r is null ? null : r with { }).ToArray();
        var columns = new[] { Column() with { Ordinal = 99 }, Column() with { ColumnName = null!, Ordinal = 1 } };
        var source = Source();
        var detail = Detail(source, columns);
        var listResult = CatalogValidator.ValidateList(rows);
        var detailResult = CatalogValidator.ValidateDetail("widgets", detail);
        EqualFailures(listResult, MultiListExpected());
        EqualFailures(detailResult, Expected("$.detail.rows.columns[1].columnName", MissingColumnName));
        for (var i = 0; i < rows.Length; i++) { Assert.Same(references[i], rows[i]); Assert.Equal(values[i], rows[i]); }
        Assert.Same(source, detail.Rows.Source);
        Assert.Equal("widgets", source.Name); Assert.Equal("Widgets", source.Title);
        Assert.Equal("Synthetic measurements", source.Description); Assert.Equal("One measurement", source.Grain);
        Assert.Equal(ConnectionSentinel, source.ConnectionString); Assert.Equal(ObjectSentinel, source.ObjectName);
        Assert.True(source.IsActive); Assert.Equal(7, source.RowCap);
        Assert.Same(columns[0], detail.Rows.Columns[0]); Assert.Same(columns[1], detail.Rows.Columns[1]);
        Assert.Equal(new[] { 99, 1 }, detail.Rows.Columns.Select(c => c.Ordinal));
        foreach (var result in new[] { listResult, detailResult })
        {
            var mutable = Assert.IsAssignableFrom<IList<CatalogFailure>>(result);
            Assert.True(mutable.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => mutable[0] = new("changed", "$", "changed"));
        }
        rows[1] = ListRow(); columns[1] = Notes();
        for (var repeat = 0; repeat < 2; repeat++)
        {
            EqualFailures(listResult.ToArray(), MultiListExpected());
            EqualFailures(detailResult.ToArray(), Expected("$.detail.rows.columns[1].columnName", MissingColumnName));
        }
        var next = CatalogValidator.ValidateList([ListRow()]);
        Assert.Empty(next); Assert.NotSame(next, listResult);
        Assert.NotSame(listResult, CatalogValidator.ValidateList(MultiList()));
        Assert.NotSame(detailResult, CatalogValidator.ValidateDetail("widgets", detail));
    }
}
