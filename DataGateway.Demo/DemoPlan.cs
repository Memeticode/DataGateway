using System.Text.Json;

namespace DataGateway.Demo;

/// <summary>One column as describe reported it: its name, data type and capability flags.</summary>
/// <param name="Name">The column name.</param>
/// <param name="DataType">text, number, date or bool.</param>
/// <param name="CanSelect">Whether the column can be selected.</param>
/// <param name="CanFilter">Whether the column can be filtered on.</param>
/// <param name="CanSort">Whether the column can be sorted on.</param>
/// <param name="CanGroup">Whether the column can be grouped by.</param>
/// <param name="CanAggregate">Whether the column can be aggregated.</param>
public sealed record DemoColumn(string Name, string DataType, bool CanSelect, bool CanFilter, bool CanSort, bool CanGroup, bool CanAggregate);
/// <summary>The columns the run uses on the chosen source, picked from its flags; a null pick skips its step.</summary>
/// <param name="Select">Up to three selectable columns, plus the filter column when it is not among them.</param>
/// <param name="Filter">A selectable, filterable number, text or bool column, for the equality filter.</param>
/// <param name="Group">A groupable column, for the group step.</param>
/// <param name="Aggregate">An aggregatable column, counted in the group step.</param>
/// <param name="NotGroupable">A queryable column that cannot be grouped, for the refusal step.</param>
/// <param name="RowCap">The source's row cap, from describe.</param>
public sealed record DemoPicks(IReadOnlyList<DemoColumn> Select, DemoColumn? Filter, DemoColumn? Group,
    DemoColumn? Aggregate, DemoColumn? NotGroupable, int RowCap);

/// <summary>Builds the run's request bodies from what list and describe returned.</summary>
/// <remarks>Not supported: demo sources whose row_cap is below 5. The group step asks for 5 rows and the detail and filter steps
/// for 3, and the gateway refuses a limit above the source's row_cap; point the demo at a source with a larger row_cap.</remarks>
public static class DemoPlan
{
    /// <summary>The columns of a describe answer, in its order.</summary>
    public static IReadOnlyList<DemoColumn> Columns(JsonElement describe) => describe.GetProperty("querySchema").GetProperty("columns").EnumerateArray()
        .Select(c => new DemoColumn(c.GetProperty("name").GetString()!, c.GetProperty("dataType").GetString()!,
            c.GetProperty("canSelect").GetBoolean(), c.GetProperty("canFilter").GetBoolean(), c.GetProperty("canSort").GetBoolean(),
            c.GetProperty("canGroup").GetBoolean(), c.GetProperty("canAggregate").GetBoolean())).ToArray();

    /// <summary>The largest limit a describe answer allows, <c>querySchema.limit.maximum</c>.</summary>
    public static int RowCap(JsonElement describe) => describe.GetProperty("querySchema")
        .GetProperty("limit").GetProperty("maximum").GetInt32();

    /// <summary>Picks the columns for each step: the first that fits, in describe order.</summary>
    /// <param name="columns">The chosen source's columns.</param>
    /// <param name="cap">The source's row cap.</param>
    public static DemoPicks Pick(IReadOnlyList<DemoColumn> columns, int cap)
    {
        var filter = columns.FirstOrDefault(c => c.CanSelect && c.CanFilter && c.DataType is "number" or "text" or "bool");
        var select = columns.Where(c => c.CanSelect).Take(3).ToList();
        if (filter is not null && !select.Contains(filter)) select.Add(filter);
        return new(select, filter, columns.FirstOrDefault(c => c.CanGroup), columns.FirstOrDefault(c => c.CanAggregate),
            columns.FirstOrDefault(c => !c.CanGroup && (c.CanSelect || c.CanFilter || c.CanSort || c.CanAggregate)), cap);
    }

    /// <summary>The first non-null value of <paramref name="column"/> in the detail rows, for the equality filter;
    /// for a number column, only one that reads as a decimal.</summary>
    /// <returns>A copy that outlives the rows' document, or null when no row has one.</returns>
    public static JsonElement? FilterValue(JsonElement rows, DemoColumn column)
    {
        foreach (var row in rows.EnumerateArray())
            if (row.TryGetProperty(column.Name, out var value) && value.ValueKind != JsonValueKind.Null &&
                (column.DataType != "number" || value.TryGetDecimal(out _))) return value.Clone();
        return null;
    }

    /// <summary>The list request body.</summary>
    public static string List() => "{}";
    /// <summary>The describe request body for one source.</summary>
    public static string Describe(string source) => JsonSerializer.Serialize(new { source });
    /// <summary>A query for three rows of the selected columns.</summary>
    public static string Detail(string source, DemoPicks picks) => Query(source, new { select = picks.Select.Select(c => c.Name), limit = 3 });
    /// <summary>A query for three rows where the column equals the value.</summary>
    public static string Filter(string source, DemoColumn column, JsonElement value) => Query(source,
        new { select = new[] { column.Name }, filters = new[] { new { col = column.Name, op = "eq", value } }, limit = 3 });
    /// <summary>A query for five groups with a count each, largest first when the counted column can be sorted.</summary>
    public static string Group(string source, DemoColumn group, DemoColumn aggregate)
    {
        var query = new Dictionary<string, object>
        {
            ["group_by"] = new[] { group.Name }, ["aggregates"] = new[] { new { fn = "count", col = aggregate.Name } }
        };
        if (aggregate.CanSort) query["order_by"] = new[] { new { col = "count_" + aggregate.Name, dir = "desc" } };
        query["limit"] = 5;
        return Query(source, query);
    }
    /// <summary>A query with no limit, whose answer must stop at the row cap.</summary>
    public static string Cap(string source, DemoColumn column) => Query(source, new { select = new[] { column.Name } });
    /// <summary>A query that selects a column the source does not have.</summary>
    public static string UnknownColumn(string source, string name) => Query(source, new { select = new[] { name } });
    /// <summary>A query that groups by a column whose <c>canGroup</c> is false.</summary>
    public static string NotGroupable(string source, DemoColumn column) => Query(source, new { group_by = new[] { column.Name } });
    /// <summary>A query whose limit is one over the row cap.</summary>
    public static string OverCap(string source, DemoColumn column, int cap) => Query(source, new { select = new[] { column.Name }, limit = (long)cap + 1 });
    /// <summary>The query request body: the source name and the query object.</summary>
    private static string Query(string source, object query) => JsonSerializer.Serialize(new { source, query });
}
