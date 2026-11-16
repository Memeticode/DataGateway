using SqlKata.JsonQuery;

namespace DataGateway.Core.Queries;

/// <summary>Fix-it text from the library code, vocabulary clause and computed limit detail.
/// The caller's query is never read; its path, rather than the message, identifies the member.</summary>
public static class RefusalMessage
{
    public static string For(JsonQueryErrorCode code, string path, string? detail)
    {
        ArgumentNullException.ThrowIfNull(path);
        var clause = Clause(path);
        if (code == JsonQueryErrorCode.StructuralLimitExceeded)
        {
            var lead = detail is null ? "The query is over a configured limit." : $"The query is over a configured limit: {detail}.";
            return lead + (clause switch
            {
                "limit" => " Ask for no more rows than the maximum, or leave limit out to get the first rows, with more set to true when rows were left out.",
                "filters" => " Use fewer filter conditions, fewer in values or fewer nested groups.",
                _ => " Send a shorter query."
            });
        }
        return code switch
        {
            JsonQueryErrorCode.InvalidJson when clause is null => "The query must be one JSON object whose only members are select, filters, group_by, aggregates, order_by and limit, each used once and with the type querySchema shows. Correct it against querySchema from describe source.",
            JsonQueryErrorCode.InvalidJson => "The member at path is unknown, repeated, missing its col or op, combines and with or, or is not the JSON type querySchema expects. Correct it against querySchema from describe source.",
            JsonQueryErrorCode.InvalidStructure => "The member at path is null, an empty filter group, or not the list or value querySchema expects there. Correct it against querySchema from describe source.",
            JsonQueryErrorCode.UnknownColumn => "The column at path is not a queryable column of this source. Use a column from describe source whose flag for this clause is true.",
            JsonQueryErrorCode.ColumnOperationNotAllowed when clause == "select" => "The column at path cannot be selected. Select only columns whose canSelect is true.",
            JsonQueryErrorCode.ColumnOperationNotAllowed when clause == "filters" => "The column at path cannot be filtered. Filter only on columns whose canFilter is true.",
            JsonQueryErrorCode.ColumnOperationNotAllowed when clause == "group_by" => "The column at path cannot be grouped. Group by a column whose canGroup is true.",
            JsonQueryErrorCode.ColumnOperationNotAllowed when clause == "aggregates" => "The column at path cannot be aggregated. Aggregate only columns whose canAggregate is true.",
            JsonQueryErrorCode.ColumnOperationNotAllowed when clause == "order_by" => "The column at path cannot be sorted. Sort only by columns whose canSort is true.",
            JsonQueryErrorCode.ColumnOperationNotAllowed => "The column at path does not allow this operation. Use a column from describe source whose flag for this clause is true.",
            JsonQueryErrorCode.InvalidOperator => "The filter operator at path is unknown or not allowed for the column's data type. Use an operator that querySchema lists for this column.",
            JsonQueryErrorCode.InvalidAggregateFunction => "The aggregate function at path is unknown or not allowed for the column's data type. Use count, sum, avg, min or max as querySchema allows for this column.",
            JsonQueryErrorCode.InvalidSortDirection => "The sort direction at path must be asc or desc.",
            JsonQueryErrorCode.InvalidValue => "The filter value at path does not fit its operator or the column's data type. Give one value for eq, neq, lt, lte, gt, gte, contains and like; a non-empty list for in and not_in; two values, low then high, for between; and no value for is_null and is_not_null. Text takes a JSON string, number a JSON number, bool true or false, and date a string yyyy-MM-dd.",
            JsonQueryErrorCode.InvalidClauseCombination => "select cannot be combined with group_by or aggregates: leave select out when grouping or aggregating. A query without them needs at least one column whose canSelect is true.",
            JsonQueryErrorCode.InvalidOrderTarget => "With group_by or aggregates, order_by can sort only by a group_by column or an aggregate output, named function_column (for example sum_total). Sort by one of those.",
            JsonQueryErrorCode.DuplicateReference => "The column or aggregate at path is named more than once. Name each column once per clause and each aggregate function and column pair once; names match without regard to case.",
            JsonQueryErrorCode.InvalidLimit => "limit must be 1 or more. Give a positive limit, or leave limit out.",
            _ => "The query was refused at path. Correct it against querySchema from describe source.",
        };
    }

    // Only the vocabulary's six members may choose wording; a path can start with caller text.
    private static string? Clause(string path)
    {
        var end = path.IndexOfAny(['.', '[']);
        var head = end < 0 ? path : path[..end];
        return head is "select" or "filters" or "group_by" or "aggregates" or "order_by" or "limit" ? head : null;
    }
}
