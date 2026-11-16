using System.Text.Json;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using SqlKata.JsonQuery;
using Xunit;

namespace DataGateway.Core.Tests;

public sealed class RowCapTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(500, 501)]
    [InlineData(QueryLimits.MaxRowCap, int.MaxValue)]
    public void T3_absent_limit_probes_one_more_without_overflow(int cap, int fetch)
    {
        var plan = RowCap.Plan(null, QueryLimits.ForRowCap(cap));
        Assert.Equal(fetch, plan.Fetch);
        Assert.Equal(cap, plan.Keep);
    }

    [Theory]
    [InlineData(500, 1)]
    [InlineData(500, 499)]
    [InlineData(500, 500)]
    [InlineData(1, 1)]
    [InlineData(QueryLimits.MaxRowCap, QueryLimits.MaxRowCap)]
    public void T4_explicit_limit_fetches_one_more(int cap, int requested)
    {
        var plan = RowCap.Plan(requested, QueryLimits.ForRowCap(cap));
        Assert.Equal(requested + 1, plan.Fetch);
        Assert.Equal(requested, plan.Keep);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(501)]
    [InlineData(int.MaxValue)]
    public void T5_bad_requested_limit_is_a_datagateway_bug(int requested) =>
        Assert.Equal("requestedLimit", Assert.Throws<ArgumentOutOfRangeException>(() =>
            RowCap.Plan(requested, QueryLimits.ForRowCap(500))).ParamName);

    [Fact]
    public void T5_missing_unbounded_and_overflowing_limits_are_datagateway_bugs()
    {
        Assert.Equal("limits", Assert.Throws<ArgumentNullException>(() => RowCap.Plan(null, null!)).ParamName);
        foreach (var limits in new[] { JsonQueryLimits.Default, JsonQueryLimits.None })
        {
            var failure = Assert.Throws<ArgumentException>(() => RowCap.Plan(null, limits));
            Assert.Equal("limits", failure.ParamName);
            Assert.StartsWith("build the limits with QueryLimits", failure.Message);
        }
        Assert.Equal("limits", Assert.Throws<ArgumentOutOfRangeException>(() =>
            RowCap.Plan(null, JsonQueryLimits.Default with { MaxLimit = int.MaxValue })).ParamName);
    }

    [Fact]
    public void T8_exact_web_shape_and_lossless_rows_without_copy()
    {
        IDictionary<string, object?> row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Int"] = 7, ["LONG"] = 9007199254740993L, ["Decimal"] = 12.3400m,
            ["Text"] = "Synthetic", ["bOOL"] = true,
            ["DateTime"] = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), ["Null"] = null
        };
        var contents = row.ToArray();
        var rows = new List<IDictionary<string, object?>> { row };
        var result = new QueryRows(rows, false);
        Assert.Same(rows, result.Rows);
        Assert.Same(row, result.Rows[0]);
        Assert.Single(rows);
        Assert.Same(row, rows[0]);
        Assert.Equal(contents, row.ToArray());
        Assert.True(result.Rows[0].ContainsKey("int"));
        foreach (var pair in row) Assert.Same(pair.Value, result.Rows[0][pair.Key]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, JsonSerializerOptions.Web));
        var body = json.RootElement;
        Assert.Equal(new[] { "rows", "rowCount", "more" }, body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(body.GetProperty("rows").GetArrayLength(), body.GetProperty("rowCount").GetInt32());
        Assert.False(body.GetProperty("more").GetBoolean());
        Assert.Equal(JsonSerializer.Serialize(row, JsonSerializerOptions.Web), body.GetProperty("rows")[0].GetRawText());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("rows")[0].GetProperty("Null").ValueKind);
        // No dictionary or values were copied.
        row["Text"] = "Changed after construction";
        Assert.Equal("Changed after construction", result.Rows[0]["Text"]);
    }
}
