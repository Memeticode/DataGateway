using System.Text.Json;
using DataGateway.Core.Catalog;
using Xunit;

namespace DataGateway.Core.Tests;

public sealed class SourceListingTests
{
    [Fact]
    public void T2_heterogeneous_caps_are_copied_independently()
    {
        var result = SourceListing.Map([new("small", "Small", "Synthetic", "One row", true, 3), new("large", "Large", "Synthetic", "One row", true, 7)]);
        var wire = JsonSerializer.SerializeToElement(result, JsonSerializerOptions.Web);
        Assert.Equal(new[] { 7, 3 }, wire.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("rowCap").GetInt32()));
    }

    [Fact]
    public void T2_active_rows_are_ordered_and_have_exactly_the_five_reasoning_properties()
    {
        SourceListRow[] rows =
        [
            new("zulu", "Last", "Synthetic last source", "One observation", true, 500),
            new("hidden", "Hidden", "Synthetic inactive source", "One hidden row", false, 500),
            new("alpha", "First", "Synthetic first source", "One item", true, 500)
        ];
        var response = SourceListing.Map(rows);
        Assert.Equal(new[] { "alpha", "zulu" }, response.Sources.Select(source => source.Name));
        Assert.Equal(new[] { "zulu", "hidden", "alpha" }, rows.Select(row => row.Name));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, JsonSerializerOptions.Web));
        Assert.Equal("sources", Assert.Single(json.RootElement.EnumerateObject()).Name);
        foreach (var entry in json.RootElement.GetProperty("sources").EnumerateArray())
        {
            Assert.Equal(new[] { "description", "grain", "name", "rowCap", "title" },
                entry.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            var original = Assert.Single(rows, row => row.Name == entry.GetProperty("name").GetString());
            Assert.True(original.IsActive);
            Assert.Equal(original.Title, entry.GetProperty("title").GetString());
            Assert.Equal(original.Description, entry.GetProperty("description").GetString());
            Assert.Equal(original.Grain, entry.GetProperty("grain").GetString());
            Assert.Equal(original.RowCap, entry.GetProperty("rowCap").GetInt32());
        }
    }

    [Fact]
    public void T2_mixed_case_underscore_and_digit_names_use_case_insensitive_order_with_ordinal_ties()
    {
        SourceListRow[] rows =
        [
            new("Banana", "Banana", "Synthetic source", "One row", true, 500),
            new("a1", "Lowercase", "Synthetic source", "One row", true, 500),
            new("apple", "Apple", "Synthetic source", "One row", true, 500),
            new("_x", "Underscore", "Synthetic source", "One row", true, 500),
            new("A1", "Uppercase", "Synthetic source", "One row", true, 500)
        ];
        string[] expected = ["A1", "a1", "apple", "Banana", "_x"];

        Assert.Equal(expected, SourceListing.Map(rows).Sources.Select(source => source.Name));
        Assert.Equal(expected, SourceListing.Map(rows.Reverse()).Sources.Select(source => source.Name));
        Assert.Equal(new[] { "Banana", "a1", "apple", "_x", "A1" }, rows.Select(row => row.Name));
    }

    [Fact]
    public void T2_unknown_source_uses_the_list_order_without_mutating_input()
    {
        string[] names = ["Banana", "a1", "apple", "_x", "A1"];
        var original = names.ToArray();
        foreach (var input in new[] { names, names.Reverse().ToArray(), new[] { "_x", "A1", "Banana", "apple", "a1" } })
        {
            var snapshot = input.ToArray();
            var failure = SourceListing.UnknownSource(input);
            Assert.Equal("UnknownSource", failure.Code);
            Assert.Equal("$.source", failure.Path);
            Assert.Equal("Choose an active source. Available sources: A1, a1, apple, Banana, _x.", failure.Message);
            Assert.Null(failure.Step);
            Assert.Null(failure.ExceptionType);
            Assert.Null(failure.SqlNumber);
            var listed = SourceListing.Map(input.Select(name => new SourceListRow(name, name, "Synthetic", "One row", true, 500)));
            Assert.Equal(new[] { "A1", "a1", "apple", "Banana", "_x" }, listed.Sources.Select(source => source.Name));
            Assert.Equal("Choose an active source. Available sources: " + string.Join(", ", listed.Sources.Select(source => source.Name)) + ".", failure.Message);
            Assert.Equal(snapshot, input);
        }
        Assert.Equal(original, names);
        var empty = SourceListing.UnknownSource([]);
        Assert.Equal("No source is active in the catalog. Ask the data steward to activate a source, then call list sources.", empty.Message);
        Assert.Null(empty.Step);
        Assert.Null(empty.ExceptionType);
        Assert.Null(empty.SqlNumber);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData(" { } ")]
    [InlineData("{\r\n}")]
    public void T10_empty_object_is_the_only_valid_request(string text)
    {
        using var body = JsonDocument.Parse(text);
        Assert.Null(SourceListing.CheckRequest(body.RootElement));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("\"x\"")]
    [InlineData("true")]
    [InlineData("{\"source\":\"x\"}")]
    [InlineData("{\"\":1}")]
    [InlineData("{\"a\":{}}")]
    [InlineData("{\"a\":null}")]
    public void T10_nonempty_request_has_exact_refusal(string text)
    {
        using var body = JsonDocument.Parse(text);
        var failure = Assert.IsType<CatalogFailure>(SourceListing.CheckRequest(body.RootElement));
        Assert.Equal(("InvalidStructure", "$", "Send an empty JSON object, {}: list sources takes no parameters.", "request"),
            (failure.Code, failure.Path, failure.Message, failure.Step));
        Assert.Null(failure.ExceptionType);
        Assert.Null(failure.SqlNumber);
    }

    [Fact]
    public void T10_unparseable_request_has_exact_refusal()
    {
        var failure = SourceListing.InvalidJson;
        Assert.Equal(("InvalidJson", "$", "The request body is not valid JSON. Send an empty JSON object, {}: list sources takes no parameters.", "request"),
            (failure.Code, failure.Path, failure.Message, failure.Step));
        Assert.Null(failure.ExceptionType);
        Assert.Null(failure.SqlNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_or_all_inactive_input_returns_an_empty_array(bool inactive)
    {
        var result = SourceListing.Map(inactive
            ? [new SourceListRow("hidden", "Hidden", "Synthetic", "One row", false, 500)] : []);
        Assert.Empty(result.Sources);
        Assert.Equal("{\"sources\":[]}", JsonSerializer.Serialize(result, JsonSerializerOptions.Web));
    }
}
