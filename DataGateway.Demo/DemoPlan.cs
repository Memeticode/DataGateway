using System.Text.Json;

namespace DataGateway.Demo;

public sealed record DemoColumn(string Name, string DataType, bool CanSelect, bool CanFilter, bool CanSort, bool CanGroup, bool CanAggregate);
public sealed record DemoPicks(IReadOnlyList<DemoColumn> Select, DemoColumn? Filter, DemoColumn? Group,
    DemoColumn? Aggregate, DemoColumn? NotGroupable, int RowCap);

public static class DemoPlan
{
    public static IReadOnlyList<DemoColumn> Columns(JsonElement describe) => describe.GetProperty("querySchema").GetProperty("columns").EnumerateArray()
        .Select(c => new DemoColumn(c.GetProperty("name").GetString()!, c.GetProperty("dataType").GetString()!,
            c.GetProperty("canSelect").GetBoolean(), c.GetProperty("canFilter").GetBoolean(), c.GetProperty("canSort").GetBoolean(),
            c.GetProperty("canGroup").GetBoolean(), c.GetProperty("canAggregate").GetBoolean())).ToArray();

    public static int RowCap(JsonElement describe) => describe.GetProperty("querySchema")
        .GetProperty("limit").GetProperty("maximum").GetInt32();

    public static DemoPicks Pick(IReadOnlyList<DemoColumn> columns, int cap)
    {
        var filter = columns.FirstOrDefault(c => c.CanSelect && c.CanFilter && c.DataType is "number" or "text" or "bool");
        var select = columns.Where(c => c.CanSelect).Take(3).ToList();
        if (filter is not null && !select.Contains(filter)) select.Add(filter);
        return new(select, filter, columns.FirstOrDefault(c => c.CanGroup), columns.FirstOrDefault(c => c.CanAggregate),
            columns.FirstOrDefault(c => !c.CanGroup && (c.CanSelect || c.CanFilter || c.CanSort || c.CanAggregate)), cap);
    }

    public static JsonElement? FilterValue(JsonElement rows, DemoColumn column)
    {
        foreach (var row in rows.EnumerateArray())
            if (row.TryGetProperty(column.Name, out var value) && value.ValueKind != JsonValueKind.Null &&
                (column.DataType != "number" || value.TryGetDecimal(out _))) return value.Clone();
        return null;
    }

    // Not supported: demo sources whose row_cap is below 5. The group step asks for 5 rows and the detail and filter steps
    // for 3, and the gateway refuses a limit above the source's row_cap; point the demo at a source with a larger row_cap.
    public static string List() => "{}";
    public static string Describe(string source) => JsonSerializer.Serialize(new { source });
    public static string Detail(string source, DemoPicks picks) => Query(source, new { select = picks.Select.Select(c => c.Name), limit = 3 });
    public static string Filter(string source, DemoColumn column, JsonElement value) => Query(source,
        new { select = new[] { column.Name }, filters = new[] { new { col = column.Name, op = "eq", value } }, limit = 3 });
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
    public static string Cap(string source, DemoColumn column) => Query(source, new { select = new[] { column.Name } });
    public static string UnknownColumn(string source, string name) => Query(source, new { select = new[] { name } });
    public static string NotGroupable(string source, DemoColumn column) => Query(source, new { group_by = new[] { column.Name } });
    public static string OverCap(string source, DemoColumn column, int cap) => Query(source, new { select = new[] { column.Name }, limit = (long)cap + 1 });
    private static string Query(string source, object query) => JsonSerializer.Serialize(new { source, query });
}
