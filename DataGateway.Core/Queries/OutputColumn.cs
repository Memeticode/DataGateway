namespace DataGateway.Core.Queries;

/// <summary>One column of a query's output: its name in the rows and its catalog data type.</summary>
public sealed record OutputColumn(string Name, string DataType);
