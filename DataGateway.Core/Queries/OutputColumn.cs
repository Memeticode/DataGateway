namespace DataGateway.Core.Queries;

/// <summary>One column of a query's output: its name in the rows and its catalog data type.</summary>
/// <param name="Name">The column's name in the rows: a catalog column, or <c>fn_col</c> for an aggregate.</param>
/// <param name="DataType">One of <c>text</c>, <c>number</c>, <c>date</c> or <c>bool</c>.</param>
public sealed record OutputColumn(string Name, string DataType);
