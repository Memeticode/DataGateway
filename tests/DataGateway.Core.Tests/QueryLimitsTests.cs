using DataGateway.Core.Catalog;
using SqlKata.JsonQuery;
using Xunit;

namespace DataGateway.Core.Tests;

public sealed class QueryLimitsTests
{
    [Fact]
    public void T7_row_cap_is_the_only_overridden_library_limit()
    {
        var limits = QueryLimits.ForRowCap(500);
        Assert.Equal(500, limits.MaxLimit);
        Assert.Equal(JsonQueryLimits.Default.MaxJsonLength, limits.MaxJsonLength);
        Assert.Equal(JsonQueryLimits.Default.MaxFilterDepth, limits.MaxFilterDepth);
        Assert.Equal(JsonQueryLimits.Default.MaxFilterConditions, limits.MaxFilterConditions);
        Assert.Equal(JsonQueryLimits.Default.MaxInValues, limits.MaxInValues);
        Assert.Equal(QueryLimits.MaxRowCap, QueryLimits.ForRowCap(QueryLimits.MaxRowCap).MaxLimit);
    }

    [Fact]
    public void T9_default_record_equality_and_cap_ceiling()
    {
        Assert.Equal(JsonQueryLimits.Default with { MaxLimit = 500 }, QueryLimits.ForRowCap(500));
        Assert.Equal("rowCap", Assert.Throws<ArgumentOutOfRangeException>(() => QueryLimits.ForRowCap(int.MaxValue)).ParamName);
    }

    [Fact]
    public void T10_create_carries_all_five_limits()
    {
        Assert.Equal(JsonQueryLimits.Default with
        {
            MaxLimit = 7, MaxJsonLength = 2048, MaxFilterDepth = 0, MaxFilterConditions = 3, MaxInValues = 2
        }, QueryLimits.Create(rowCap: 7, maxJsonLength: 2048, maxFilterDepth: 0, maxFilterConditions: 3, maxInValues: 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void T10_create_refuses_bad_caps(int cap) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        QueryLimits.Create(rowCap: cap, maxJsonLength: 2048, maxFilterDepth: 0, maxFilterConditions: 3, maxInValues: 2));

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void T10_create_accepts_depth_edges(int depth) => Assert.Equal(depth,
        QueryLimits.Create(rowCap: 7, maxJsonLength: 1, maxFilterDepth: depth, maxFilterConditions: 1, maxInValues: 1).MaxFilterDepth);

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public void T10_create_refuses_bad_depth(int depth) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        QueryLimits.Create(rowCap: 7, maxJsonLength: 1, maxFilterDepth: depth, maxFilterConditions: 1, maxInValues: 1));

    [Fact]
    public void T10_create_uses_library_guards_for_positive_structural_limits()
    {
        var minimum = QueryLimits.Create(rowCap: 1, maxJsonLength: 1, maxFilterDepth: 0, maxFilterConditions: 1, maxInValues: 1);
        Assert.Equal(1, minimum.MaxJsonLength);
        Assert.Equal(1, minimum.MaxFilterConditions);
        Assert.Equal(1, minimum.MaxInValues);
        Assert.Throws<ArgumentOutOfRangeException>(() => QueryLimits.Create(rowCap: 1, maxJsonLength: 0, maxFilterDepth: 0, maxFilterConditions: 1, maxInValues: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => QueryLimits.Create(rowCap: 1, maxJsonLength: 1, maxFilterDepth: 0, maxFilterConditions: 0, maxInValues: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => QueryLimits.Create(rowCap: 1, maxJsonLength: 1, maxFilterDepth: 0, maxFilterConditions: 1, maxInValues: 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void T7_library_init_guard_refuses_nonpositive_caps(int cap) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => QueryLimits.ForRowCap(cap));
}
