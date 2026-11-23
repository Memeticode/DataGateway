using DataGateway.Core.Queries;
using SqlKata.JsonQuery;
using Xunit;

namespace DataGateway.Core.Tests;

/// <summary>Pins <see cref="RefusalMessage.For"/>: the fix-it text a caller gets for each refused query.</summary>
/// <remarks>The text is built only from the library's error code, the clause the path names and the computed limit
/// detail. The caller's own query text never reaches it, so a refusal cannot echo markup, SQL or oversized input
/// back to the caller.</remarks>
public sealed class RefusalMessageTests
{
    /// <summary>Every library error code, and each clause variant of it, has exactly this wording; an unknown code gets
    /// the generic sentence.</summary>
    /// <remarks>The table is exact on purpose: the wording is the public contract a caller and a model act on, so any
    /// change to it must show up here.</remarks>
    [Theory]
    [InlineData(JsonQueryErrorCode.InvalidJson, "$", null, "The query must be one JSON object whose only members are select, filters, group_by, aggregates, order_by and limit, each used once and with the type querySchema shows. Correct it against querySchema from describe source.")]
    [InlineData(JsonQueryErrorCode.InvalidJson, "", null, "The query must be one JSON object whose only members are select, filters, group_by, aggregates, order_by and limit, each used once and with the type querySchema shows. Correct it against querySchema from describe source.")]
    [InlineData(JsonQueryErrorCode.InvalidJson, "x'; --", null, "The query must be one JSON object whose only members are select, filters, group_by, aggregates, order_by and limit, each used once and with the type querySchema shows. Correct it against querySchema from describe source.")]
    [InlineData(JsonQueryErrorCode.InvalidJson, "limit", null, "The member at path is unknown, repeated, missing its col or op, combines and with or, or is not the JSON type querySchema expects. Correct it against querySchema from describe source.")]
    [InlineData(JsonQueryErrorCode.InvalidJson, "filters[0].or[1].bogus", null, "The member at path is unknown, repeated, missing its col or op, combines and with or, or is not the JSON type querySchema expects. Correct it against querySchema from describe source.")]
    [InlineData(JsonQueryErrorCode.InvalidStructure, "select", null, "The member at path is null, an empty filter group, or not the list or value querySchema expects there. Correct it against querySchema from describe source.")]
    [InlineData(JsonQueryErrorCode.UnknownColumn, "filters[1].or[1].and[0].col", null, "The column at path is not a queryable column of this source. Use a column from describe source whose flag for this clause is true.")]
    [InlineData(JsonQueryErrorCode.ColumnOperationNotAllowed, "select[0]", null, "The column at path cannot be selected. Select only columns whose canSelect is true.")]
    [InlineData(JsonQueryErrorCode.ColumnOperationNotAllowed, "filters[0].col", null, "The column at path cannot be filtered. Filter only on columns whose canFilter is true.")]
    [InlineData(JsonQueryErrorCode.ColumnOperationNotAllowed, "group_by[0]", null, "The column at path cannot be grouped. Group by a column whose canGroup is true.")]
    [InlineData(JsonQueryErrorCode.ColumnOperationNotAllowed, "aggregates[0].col", null, "The column at path cannot be aggregated. Aggregate only columns whose canAggregate is true.")]
    [InlineData(JsonQueryErrorCode.ColumnOperationNotAllowed, "order_by[0].col", null, "The column at path cannot be sorted. Sort only by columns whose canSort is true.")]
    [InlineData(JsonQueryErrorCode.ColumnOperationNotAllowed, "$", null, "The column at path does not allow this operation. Use a column from describe source whose flag for this clause is true.")]
    [InlineData(JsonQueryErrorCode.ColumnOperationNotAllowed, "limit", null, "The column at path does not allow this operation. Use a column from describe source whose flag for this clause is true.")]
    [InlineData(JsonQueryErrorCode.InvalidOperator, "filters[0].op", null, "The filter operator at path is unknown or not allowed for the column's data type. Use an operator that querySchema lists for this column.")]
    [InlineData(JsonQueryErrorCode.InvalidAggregateFunction, "aggregates[0].fn", null, "The aggregate function at path is unknown or not allowed for the column's data type. Use count, sum, avg, min or max as querySchema allows for this column.")]
    [InlineData(JsonQueryErrorCode.InvalidSortDirection, "order_by[0].dir", null, "The sort direction at path must be asc or desc.")]
    [InlineData(JsonQueryErrorCode.InvalidValue, "filters[0].value", null, "The filter value at path does not fit its operator or the column's data type. Give one value for eq, neq, lt, lte, gt, gte, contains and like; a non-empty list for in and not_in; two values, low then high, for between; and no value for is_null and is_not_null. Text takes a JSON string, number a JSON number, bool true or false, and date a string yyyy-MM-dd.")]
    [InlineData(JsonQueryErrorCode.InvalidClauseCombination, "select", null, "select cannot be combined with group_by or aggregates: leave select out when grouping or aggregating. A query without them needs at least one column whose canSelect is true.")]
    [InlineData(JsonQueryErrorCode.InvalidOrderTarget, "order_by[0].col", null, "With group_by or aggregates, order_by can sort only by a group_by column or an aggregate output, named function_column (for example sum_total). Sort by one of those.")]
    [InlineData(JsonQueryErrorCode.DuplicateReference, "select[1]", null, "The column or aggregate at path is named more than once. Name each column once per clause and each aggregate function and column pair once; names match without regard to case.")]
    [InlineData(JsonQueryErrorCode.InvalidLimit, "limit", null, "limit must be 1 or more. Give a positive limit, or leave limit out.")]
    [InlineData((JsonQueryErrorCode)999, "$", null, "The query was refused at path. Correct it against querySchema from describe source.")]
    [InlineData(JsonQueryErrorCode.StructuralLimitExceeded, "limit", "limit 8 exceeds the maximum of 7", "The query is over a configured limit: limit 8 exceeds the maximum of 7. Ask for no more rows than the maximum, or leave limit out to get the first rows, with more set to true when rows were left out.")]
    [InlineData(JsonQueryErrorCode.StructuralLimitExceeded, "filters[0].value", "3 values exceed the in limit of 2", "The query is over a configured limit: 3 values exceed the in limit of 2. Use fewer filter conditions, fewer in values or fewer nested groups.")]
    [InlineData(JsonQueryErrorCode.StructuralLimitExceeded, "$", "41 characters exceed the payload limit of 40", "The query is over a configured limit: 41 characters exceed the payload limit of 40. Send a shorter query.")]
    [InlineData(JsonQueryErrorCode.StructuralLimitExceeded, "filters[2]", null, "The query is over a configured limit. Use fewer filter conditions, fewer in values or fewer nested groups.")]
    public void T7_exact_table_W(JsonQueryErrorCode code, string path, string? detail, string expected) =>
        Assert.Equal(expected, RefusalMessage.For(code, path, detail));

    /// <summary>A hostile or huge path changes nothing in the message beyond the clause it names, for every code.</summary>
    [Fact]
    public void T8_caller_text_never_reaches_wording()
    {
        var longName = new string('a', 4000);
        (string Dirty, string Clean)[] paths = [("filters[0].<img src=x onerror=alert(1)>", "filters[0]"),
            ("x'; DROP TABLE t; --", "$"), ("select[0]<b>\0", "select[0]"), ("filters[0]." + longName, "filters[0]")];
        foreach (var code in Enum.GetValues<JsonQueryErrorCode>().Append((JsonQueryErrorCode)999))
        foreach (var (dirty, clean) in paths)
        {
            var text = RefusalMessage.For(code, dirty, null);
            Assert.Equal(RefusalMessage.For(code, clean, null), text);
            foreach (var forbidden in new[] { "<img", "alert", "DROP", "<b>", "\0", longName, "JSON query failed", dirty })
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
        Assert.Equal("path", Assert.Throws<ArgumentNullException>(() => RefusalMessage.For(JsonQueryErrorCode.InvalidJson, null!, null)).ParamName);
    }
}
