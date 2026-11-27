using DataGateway.Data;
using DataGateway.Web.Catalog;
using DataGateway.Failures;
using DataGateway.Data.SqlServer;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataGateway.Web.Configuration;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Core.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlKata;
using SqlKata.JsonQuery;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class QueryRouteTests
{
    private const string SourceConnection = "Server=tcp:127.0.0.1,1;Database=Synthetic;User ID=fake-query-reader;Password=fake-query-password;Encrypt=False;Pooling=False;Connect Timeout=1;ConnectRetryCount=0";
    private const string Connection = "Server=tcp:127.0.0.1,1;Database=Catalog;User ID=fake-query-reader;Password=fake-query-password;Encrypt=False;Pooling=False";
    private const string M1 = "The request body is not valid JSON. Send {\"source\": \"<name>\", \"query\": {...}} with a name from list sources.";
    private const string M2 = "Send a JSON object with exactly two properties, source and query: {\"source\": \"<name>\", \"query\": {...}}. Take the name from list sources and the query vocabulary from describe source.";
    private const string M3 = "source must be a JSON string: the name of a source from list sources.";
    private const string M4 = "DataGateway could not complete the request because of a fault on its side, not in the request. Contact the DataGateway administrator. Tell the user, give the incidentId and stop; do not retry.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T10_composition_exact_json_sql_parameters_and_case(bool grouped)
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        var query = grouped ? """{"group_by":["region"],"aggregates":[{"fn":"sum","col":"line_total"}],"limit":5}""" :
            """{ "select" : ["region","OrderDay","line_total","is_return","note"], "limit": 5 }""";
        CompiledQuery? seen = null;
        var runner = Engine((q, _) => { seen = q; return Data(grouped ? new Dictionary<string,object?>() { ["region"] = "N\u00f6rth <&>", ["sum_line_total"] = 523.0600m } :
            new Dictionary<string,object?>() { ["region"] = "N\u00f6rth <&>", ["OrderDay"] = new DateTime(2024,12,31), ["line_total"] = 523.0600m, ["is_return"] = false, ["note"] = null }); });
        await using var app = Host(logs, Catalog(logs), runner);
        await app.StartAsync();
        app.Services.GetRequiredService<HostCatalogState>().Arm();
        using var client = Client(app);
        var response = await Post(client, query, "SYNTHETIC_SALES");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(grouped ? """{"source":"synthetic_sales","columns":[{"name":"region","dataType":"text"},{"name":"sum_line_total","dataType":"number"}],"rows":[{"region":"Nörth <&>","sum_line_total":523.0600}],"rowCount":1,"more":false,"hint":"Complete result set."}""" :
            """{"source":"synthetic_sales","columns":[{"name":"region","dataType":"text"},{"name":"OrderDay","dataType":"date"},{"name":"line_total","dataType":"number"},{"name":"is_return","dataType":"bool"},{"name":"note","dataType":"text"}],"rows":[{"region":"Nörth <&>","OrderDay":"2024-12-31","line_total":523.0600,"is_return":false,"note":null}],"rowCount":1,"more":false,"hint":"Complete result set."}""",
            await response.Content.ReadAsStringAsync());
        var expected = new SqlServerEngine().Compile(QueryBuilding.Validate(Contract(), query, app.Services.GetRequiredService<JsonQueryLimits>() with { MaxLimit = Contract().Source.RowCap }).Value);
        Assert.NotNull(seen);
        Assert.Equal(expected.Sql, seen.Sql);
        Assert.Equal(expected.Parameters, seen.Parameters);
        Assert.Empty(logs.Errors);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/query")).StatusCode);
        Assert.Contains(((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>(),
            e => e.RoutePattern.RawText == "/query" && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));
        AssertLogSafe(logs);
    }

    [Theory]
    [InlineData(1, 2, 1, true, 2)]
    [InlineData(1, 1, 1, false, 2)]
    [InlineData(null, 8, 7, true, 8)]
    [InlineData(null, 3, 3, false, 8)]
    [InlineData(5, 6, 5, true, 6)]
    [InlineData(5, 5, 5, false, 6)]
    [InlineData(7, 8, 7, true, 8)]
    [InlineData(null, 0, 0, false, 8)]
    public async Task T11_cap(int? limit, int fetched, int kept, bool more, int fetch)
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        await using var app = Host(logs, Catalog(logs), Engine((q, _) =>
        {
            Assert.Equal(fetch, Assert.Single(q.Parameters).Value);
            return Task.FromResult<IReadOnlyList<dynamic>>(Enumerable.Range(0,fetched).Select(i => (dynamic)new Dictionary<string,object?> { ["region"] = "north" }).ToArray());
        }));
        await app.StartAsync();
        app.Services.GetRequiredService<HostCatalogState>().Arm();
        using var client = Client(app);
        var query = "{\"select\":[\"region\"]" + (limit is null ? "}" : ",\"limit\":" + limit + "}");
        var response = await Post(client, query);
        await using var mcp = await McpWire.Connect(client);
        var tool = await McpWire.Call(mcp, "query_source", Body(query));
        Assert.False(tool.IsError == true);
        Assert.Equal(await response.Content.ReadAsStringAsync(), McpWire.Text(tool));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(kept, json.RootElement.GetProperty("rowCount").GetInt32());
        Assert.Equal(kept, json.RootElement.GetProperty("rows").GetArrayLength());
        Assert.EndsWith(more
            ? ",\"more\":true,\"hint\":\"The result set was capped at " + kept + (kept == 1 ? " row." : " rows.") + " Rows are in no particular order.\"}"
            : kept == 0 ? ",\"more\":false,\"hint\":\"Nothing found that matched your query.\"}"
            : ",\"more\":false,\"hint\":\"Complete result set.\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(more, json.RootElement.GetProperty("more").GetBoolean());
        Assert.Equal("region", json.RootElement.GetProperty("columns")[0].GetProperty("name").GetString());
        Assert.Empty(logs.Errors);
    }

    [Fact]
    public async Task T11_like_refusal_does_not_echo_bracketed_value()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(ErrorLogTests.Catalog());
        await host.Start();
        const string body = """{"source":"synthetic","query":{"filters":[{"col":"text","op":"like","value":"a_b"},{"col":"absent","op":"eq","value":"x"}]}}""";
        using var response = await host.Client.PostAsync("/query", new StringContent(body));
        var route = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var mcp = await McpWire.Connect(host.Client);
        var tool = await McpWire.Call(mcp, "query_source", body);
        Assert.True(tool.IsError);
        McpWire.SameDocument(route, McpWire.Text(tool));
        using var answer = JsonDocument.Parse(route);
        Assert.Equal("UnknownColumn", answer.RootElement.GetProperty("code").GetString());
        Assert.Equal("$.query.filters[1].col", answer.RootElement.GetProperty("path").GetString());
        Assert.DoesNotContain("[_]", route);
        Assert.DoesNotContain("[_]", McpWire.Text(tool));
    }

    [Theory]
    [InlineData("", "InvalidJson", "$")]
    [InlineData("/*x*/{}", "InvalidJson", "$")]
    [InlineData("{\"source\":\"a\",\"query\":{},}", "InvalidJson", "$")]
    [InlineData("{}{}", "InvalidJson", "$")]
    [InlineData("[]", "InvalidStructure", "$")]
    [InlineData("null", "InvalidStructure", "$")]
    [InlineData("1", "InvalidStructure", "$")]
    [InlineData("\"x\"", "InvalidStructure", "$")]
    [InlineData("{}", "InvalidStructure", "$")]
    [InlineData("{\"source\":\"a\"}", "InvalidStructure", "$")]
    [InlineData("{\"query\":{}}", "InvalidStructure", "$")]
    [InlineData("{\"source\":\"a\",\"query\":{},\"limit\":1}", "InvalidStructure", "$")]
    [InlineData("{\"source\":\"a\",\"source\":\"b\"}", "InvalidStructure", "$")]
    [InlineData("{\"source\":\"a\",\"query\":{},\"query\":{}}", "InvalidStructure", "$")]
    [InlineData("{\"Source\":\"a\",\"query\":{}}", "InvalidStructure", "$")]
    [InlineData("{\"source\":\"a\",\"Query\":{}}", "InvalidStructure", "$")]
    [InlineData("{\"source\":null,\"query\":{}}", "InvalidStructure", "$.source")]
    [InlineData("{\"source\":1,\"query\":{}}", "InvalidStructure", "$.source")]
    [InlineData("{\"source\":[],\"query\":{}}", "InvalidStructure", "$.source")]
    [InlineData("{\"source\":{},\"query\":{}}", "InvalidStructure", "$.source")]
    public Task T12_malformed(string text, string code, string path) => BadBody(Encoding.UTF8.GetBytes(text), code, path);

    [Fact]
    public async Task T12_invalid_utf8()
    {
        foreach (var (prefix, suffix, code) in new[] { ("{\"source\":\"", "\",\"query\":{}}", "InvalidJson"),
                     ("{\"source\":\"a\",\"query\":{\"x\":\"", "\"}}", "InvalidJson"), ("{\"source\":\"a\",\"", "\":{}}", "InvalidStructure") })
            await BadBody([.. Encoding.UTF8.GetBytes(prefix), 255,254, .. Encoding.UTF8.GetBytes(suffix)],code,"$");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"{}\"")]
    [InlineData("5")]
    public Task T12_library_judges_root(string query) => Refused(query, "InvalidJson", "$.query", "The query must be one JSON object whose only members are select, filters, group_by, aggregates, order_by and limit, each used once and with the type querySchema shows. Correct it against querySchema from describe source.");

    [Fact]
    public async Task T13_raw_length_duplicates_and_depth()
    {
        using (var settings = new SettingsScope())
        {
            var logs = new RecordedLogs();
            await using var app = Host(logs, Catalog(logs), Engine((_,_) => Data()), new Dictionary<string,string?> { ["DataGateway:Limits:MaxJsonLength"] = "40" });
            await app.StartAsync();
            app.Services.GetRequiredService<HostCatalogState>().Arm();
            using var client = Client(app);
            var exact = "{\"select\":[\"region\"]".PadRight(39) + "}";
            Assert.Equal(HttpStatusCode.OK, (await Post(client,exact)).StatusCode);
            var response = await Post(client,exact.Insert(exact.Length-1," "));
            await Error(response,400,"StructuralLimitExceeded","$.query","The query is over a configured limit: 41 characters exceed the payload limit of 40. Send a shorter query.");
            Assert.Equal("query", Assert.Single(logs.ErrorFields)["Step"]);
        }
        await Refused("{\"limit\":2,\"limit\":3}","InvalidJson","$.query.limit","The member at path is unknown, repeated, missing its col or op, combines and with or, or is not the JSON type querySchema expects. Correct it against querySchema from describe source.");
        var deep64 = "{\"x\":" + new string('[',63) + "0" + new string(']',63) + "}";
        await Refused(deep64, "InvalidJson", "$.query", "The query must be one JSON object whose only members are select, filters, group_by, aggregates, order_by and limit, each used once and with the type querySchema shows. Correct it against querySchema from describe source.");
        var deep65 = "{\"x\":" + new string('[',64) + "0" + new string(']',64) + "}";
        await BadBody(Encoding.UTF8.GetBytes(Body(deep65)),"InvalidJson","$");
    }

    public static IEnumerable<object[]> Refusals()
    {
        yield return ["{\"select\":[\"missing\"]}", "UnknownColumn", "$.query.select[0]", "The column at path is not a queryable column of this source. Use a column from describe source whose flag for this clause is true.", -1];
        yield return ["{\"select\":[\"internal_code\"]}", "UnknownColumn", "$.query.select[0]", "The column at path is not a queryable column of this source. Use a column from describe source whose flag for this clause is true.", -1];
        yield return ["{\"group_by\":[\"line_total\"]}", "ColumnOperationNotAllowed", "$.query.group_by[0]", "The column at path cannot be grouped. Group by a column whose canGroup is true.", -1];
        yield return ["{\"filters\":[{\"col\":\"region\",\"op\":\"eq\",\"value\":\"north\"},{\"or\":[{\"col\":\"region\",\"op\":\"eq\",\"value\":\"west\"},{\"and\":[{\"col\":\"note\",\"op\":\"eq\",\"value\":\"x\"}]}]}]}", "ColumnOperationNotAllowed", "$.query.filters[1].or[1].and[0].col", "The column at path cannot be filtered. Filter only on columns whose canFilter is true.", -1];
        yield return ["{\"aggregates\":[{\"fn\":\"count\",\"col\":\"note\"}]}", "ColumnOperationNotAllowed", "$.query.aggregates[0].col", "The column at path cannot be aggregated. Aggregate only columns whose canAggregate is true.", -1];
        yield return ["{\"filters\":[{\"col\":\"region\",\"op\":\"bogus\",\"value\":\"x\"}]}", "InvalidOperator", "$.query.filters[0].op", "The filter operator at path is unknown or not allowed for the column's data type. Use an operator that querySchema lists for this column.", -1];
        yield return ["{\"aggregates\":[{\"fn\":\"bogus\",\"col\":\"line_total\"}]}", "InvalidAggregateFunction", "$.query.aggregates[0].fn", "The aggregate function at path is unknown or not allowed for the column's data type. Use count, sum, avg, min or max as querySchema allows for this column.", -1];
        yield return ["{\"order_by\":[{\"col\":\"region\",\"dir\":\"bogus\"}]}", "InvalidSortDirection", "$.query.order_by[0].dir", "The sort direction at path must be asc or desc.", -1];
        yield return ["{\"filters\":[{\"col\":\"OrderDay\",\"op\":\"eq\",\"value\":\"31/12/2024\"}]}", "InvalidValue", "$.query.filters[0].value", "The filter value at path does not fit its operator or the column's data type. Give one value for eq, neq, lt, lte, gt, gte, contains and like; a non-empty list for in and not_in; two values, low then high, for between; and no value for is_null and is_not_null. Text takes a JSON string, number a JSON number, bool true or false, and date a string yyyy-MM-dd.", -1];
        yield return ["{\"select\":[\"region\"],\"group_by\":[\"region\"]}", "InvalidClauseCombination", "$.query.select", "select cannot be combined with group_by or aggregates: leave select out when grouping or aggregating. A query without them needs at least one column whose canSelect is true.", -1];
        yield return ["{\"group_by\":[\"region\"],\"order_by\":[{\"col\":\"OrderDay\",\"dir\":\"asc\"}]}", "InvalidOrderTarget", "$.query.order_by[0].col", "With group_by or aggregates, order_by can sort only by a group_by column or an aggregate output, named function_column (for example sum_total). Sort by one of those.", -1];
        yield return ["{\"select\":[\"region\",\"region\"]}", "DuplicateReference", "$.query.select[1]", "The column or aggregate at path is named more than once. Name each column once per clause and each aggregate function and column pair once; names match without regard to case.", -1];
        yield return ["{\"limit\":0}", "InvalidLimit", "$.query.limit", "limit must be 1 or more. Give a positive limit, or leave limit out.", -1];
        yield return ["{\"limit\":8}", "StructuralLimitExceeded", "$.query.limit", "The query is over a configured limit: limit 8 exceeds the maximum of 7. Ask for no more rows than the maximum, or leave limit out to get the first rows, with more set to true when rows were left out.", -1];
        yield return ["{\"extra\":1}", "InvalidJson", "$.query", "The query must be one JSON object whose only members are select, filters, group_by, aggregates, order_by and limit, each used once and with the type querySchema shows. Correct it against querySchema from describe source.", -1];
        yield return ["{\"filters\":[{\"col\":\"region\",\"op\":\"in\",\"value\":[\"a\",\"b\",\"c\"]}]}", "StructuralLimitExceeded", "$.query.filters[0].value", "The query is over a configured limit: 3 values exceed the in limit of 2. Use fewer filter conditions, fewer in values or fewer nested groups.", 0];
        yield return ["{\"filters\":[{\"col\":\"region\",\"op\":\"eq\",\"value\":\"a\"},{\"col\":\"region\",\"op\":\"eq\",\"value\":\"b\"},{\"col\":\"region\",\"op\":\"eq\",\"value\":\"c\"}]}", "StructuralLimitExceeded", "$.query.filters[2]", "The query is over a configured limit: 3 filter conditions exceed the limit of 2. Use fewer filter conditions, fewer in values or fewer nested groups.", 1];
        yield return ["{\"filters\":[{\"or\":[{\"and\":[{\"col\":\"region\",\"op\":\"eq\",\"value\":\"a\"}]}]}]}", "StructuralLimitExceeded", "$.query.filters[0].or[0].and", "The query is over a configured limit: filter group nesting depth 2 exceeds the limit of 1. Use fewer filter conditions, fewer in values or fewer nested groups.", 2];
        yield return ["{\"select\":null}", "InvalidStructure", "$.query.select", "The member at path is null, an empty filter group, or not the list or value querySchema expects there. Correct it against querySchema from describe source.", -1];
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public Task T14_all_library_refusals(string query, string code, string path, string message, int structural) =>
        Refused(query, code, path, message, structural < 0 ? null : new Dictionary<string,string?>
        { [structural switch { 0 => "DataGateway:Limits:MaxInValues", 1 => "DataGateway:Limits:MaxFilterConditions", _ => "DataGateway:Limits:MaxFilterDepth" }] = structural == 2 ? "1" : "2" });

    [Theory]
    [InlineData("unknown",404,"UnknownSource","$.source")]
    [InlineData("inactive",404,"UnknownSource","$.source")]
    [InlineData("driver",503,"CatalogUnavailable","$.source")]
    [InlineData("timeout",503,"CatalogUnavailable","$.source")]
    [InlineData("all-off",500,"InternalError","$")]
    [InlineData("argument",503,"CatalogUnavailable","$.source")]
    [InlineData("cancelled",500,"InternalError","$")]
    public async Task T15_catalog_single_log(string kind, int status, string code, string path)
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        var calls = 0;
        var reader = kind == "driver" ? null : Catalog(logs, (_,_) =>
        {
            if (kind is "unknown" or "inactive") return Task.FromResult<(IReadOnlyList<dynamic>,IReadOnlyList<dynamic>)>(([],[
                new Dictionary<string,object?> { ["name"] = "alpha" }, new Dictionary<string,object?> { ["name"] = "beta" }]));
            if (kind == "all-off")
            {
                var rows = Rows();
                foreach (var row in rows)
                foreach (var flag in new[] { "can_select","can_filter","can_sort","can_group","can_aggregate" }) row[flag] = false;
                return Batch(rows);
            }
            throw DriverFailure(kind);
        });
        if (reader is not null && kind is "unknown" or "inactive") reader.List = _ => Task.FromResult<IReadOnlyList<SourceListRow>>(
            [new("alpha", "Alpha", "Synthetic", "One row", true, 7), new("beta", "Beta", "Synthetic", "One row", true, 7)]);
        await using var app = Host(logs,reader,Engine((_,_) => { calls++; return Data(); }));
        await app.StartAsync();
        app.Services.GetRequiredService<HostCatalogState>().Arm();
        using var client = Client(app);
        var response = await Post(client,"{}");
        var body = await Error(response,status,code,path,status==500 ? M4 : null);
        if (status==500) Assert.DoesNotContain(kind switch { "all-off" => "InvalidCatalog", "argument" => "CatalogConfiguration", _ => "CatalogCancelled" },body);
        if (code=="UnknownSource") Assert.Contains("alpha, beta",body);
        Assert.Equal(0,calls);
        Assert.Single(logs.Errors);
        var fields = Assert.Single(logs.ErrorFields);
        Assert.Equal(kind switch { "all-off" => "InvalidCatalog", "argument" => "CatalogUnavailable", "cancelled" => "CatalogCancelled", _ => code },fields["Code"]);
        if (kind=="driver") Assert.Equal("SqlException",fields["ExceptionType"]);
        AssertSafe(body); AssertLogSafe(logs);
    }

    [Theory]
    [InlineData("driver",503,"SourceUnavailable","$.source","connect")]
    [InlineData("timeout",504,"QueryTimeout","$.query","command")]
    [InlineData("io",503,"SourceUnavailable","$.source","command")]
    [InlineData("stored",500,"InternalError","$","connect")]
    [InlineData("seed",500,"InternalError","$","seed")]
    public async Task T16_runner_single_log(string kind, int status, string code, string path, string step)
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        var reader = Catalog(logs,(_,_) => Batch(Rows(kind=="seed" ? "bad name; DROP" : "hidden_schema.order_lines",kind=="stored" ? "malformed-query-connection-marker" : SourceConnection)));
        IDatabaseEngine? runner = kind=="driver" ? null : Engine((_,_) => throw DriverFailure(kind=="timeout" ? "timeout" : "io"));
        await using var app = Host(logs,reader,runner);
        await app.StartAsync();
        app.Services.GetRequiredService<HostCatalogState>().Arm();
        using var client = Client(app);
        var response = await Post(client,"{\"select\":[\"region\"]}");
        var body = await Error(response,status,code,path,status==500 ? M4 : null);
        var fields = Assert.Single(logs.ErrorFields);
        Assert.Single(logs.Errors);
        Assert.Equal(step,fields["Step"]);
        Assert.Equal(status==500 ? "InvalidCatalog" : code,fields["Code"]);
        if (status==500) Assert.DoesNotContain("InvalidCatalog",body);
        if (kind=="driver") Assert.Equal("SqlException",fields["ExceptionType"]);
        if (kind=="timeout") Assert.Contains("1-second command timeout",body);
        AssertSafe(body); AssertLogSafe(logs);
        Assert.DoesNotContain("bad name; DROP",body+logs.Text);
    }

    [Theory]
    [InlineData("pre")]
    [InlineData("io")]
    [InlineData("json")]
    [InlineData("cancelled")]
    public async Task T17_cancelled_body(string kind)
    {
        using var abort = new CancellationTokenSource();
        var logs = new RecordedLogs();
        var calls = 0;
        var reader = Catalog(logs, (_, _) =>
        {
            calls++;
            return Batch(Rows());
        });
        using var response = new MemoryStream();
        var context = Context(abort.Token,response);
        if (kind=="pre") abort.Cancel(); else context.Request.Body = new CancellingStream(abort,kind);
        await PublicGatewayProbe.Invoke(context, "query", reader, logs, Engine((_, _) => throw new Exception()));
        Assert.Equal(0,response.Length);
        Assert.False(context.Response.HasStarted);
        Assert.Equal(0,calls);
        Assert.Empty(logs.Errors);
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("run")]
    [InlineData("returned")]
    [InlineData("after-catalog")]
    [InlineData("write")]
    public async Task T17_cancelled_stages(string kind)
    {
        using var abort = new CancellationTokenSource();
        var logs = new RecordedLogs();
        var runCalls = 0;
        var reader = Catalog(logs,(_,_) =>
        {
            if (kind=="catalog") { abort.Cancel(); throw new IOException("fake-driver-secret"); }
            return Batch(Rows());
        });
        var runner = Engine((_,_) =>
        {
            runCalls++;
            if (kind is "run" or "returned") abort.Cancel();
            if (kind=="run") throw new IOException("fake-driver-secret");
            return Data(new Dictionary<string,object?>() { ["region"] = "north" });
        });
        Stream response = kind=="write" ? new AbortedWriteStream(abort) : new MemoryStream();
        using (response)
        {
            var context = Context(abort.Token,response);
            if (kind=="after-catalog") context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Body("{\"select\":[\"region\"]}")));
            // Cancel after the public catalog callback, before source execution.
            if (kind == "after-catalog")
            {
                runner = FakeRows.Engine(new NamedGatewayEngine("sqlserver")
                {
                    CompileFault = () =>
                    {
                        abort.Cancel();
                        return null;
                    }
                }, FakeRows.From((_, _, _, _) =>
                {
                    runCalls++;
                    return Data();
                }));
            }
            await PublicGatewayProbe.Invoke(context, "query", reader, logs, runner);
            Assert.Equal(0,response.Length);
            Assert.False(context.Response.HasStarted);
        }
        if (kind=="write") Assert.Empty(logs.Errors);
        else
        {
            var fields = Assert.Single(logs.ErrorFields);
            Assert.Single(logs.Errors);
            Assert.Equal(kind=="catalog" ? "CatalogCancelled" : "QueryCancelled",fields["Code"]);
            Assert.Equal(LogLevel.Warning, Assert.Single(logs.Levels));
            if (kind is "run" or "returned") Assert.Equal("command",fields["Step"]);
        }
        if (kind is "catalog" or "after-catalog") Assert.Equal(0,runCalls);
        AssertLogSafe(logs);
    }

    [Fact]
    public async Task T18_http_abort_reaches_runner()
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = Host(logs,Catalog(logs),Engine(async (_,token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite,token); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            return [];
        }));
        await app.StartAsync();
        app.Services.GetRequiredService<HostCatalogState>().Arm();
        using var client = Client(app);
        using var abort = new CancellationTokenSource();
        var pending = client.PostAsync("/query",new StringContent(Body("{}")),abort.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await app.StopAsync();
        Assert.Contains("QueryCancelled",Assert.Single(logs.Errors));
        Assert.Equal(LogLevel.Warning, Assert.Single(logs.Levels));
    }

    [Fact]
    public async Task T19_concurrent_queries_are_independent()
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        var reads = 0;
        var arrived = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = new ConcurrentBag<CompiledQuery>();
        await using var app = Host(logs,Catalog(logs,(_,_) => { Interlocked.Increment(ref reads); return Batch(Rows()); }),Engine(async (q,token) =>
        {
            queries.Add(q);
            if (Interlocked.Increment(ref arrived)==2) barrier.SetResult();
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5),token);
            var region = Assert.Single(q.Parameters.Values.OfType<string>());
            return [new Dictionary<string,object?> { ["region"] = region }];
        }));
        await app.StartAsync();
        app.Services.GetRequiredService<HostCatalogState>().Arm();
        using var client = Client(app);
        var results = await Task.WhenAll(new[] { "north","south" }.Select(async region =>
        {
            var response = await Post(client,"{\"select\":[\"region\"],\"filters\":[{\"col\":\"region\",\"op\":\"eq\",\"value\":\"" + region + "\"}]}");
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("rows")[0].GetProperty("region").GetString();
        }));
        Assert.Equal(new[] { "north","south" },results);
        Assert.Equal(2,reads);
        Assert.Equal(2,queries.Count);
        Assert.NotSame(queries.First(),queries.Last());
        Assert.Empty(logs.Errors);
    }

    [Fact]
    public Task T11_explicit_limit_above_cap_is_refused() => Refused(
        "{\"select\":[\"region\"],\"limit\":8}", "StructuralLimitExceeded", "$.query.limit",
        "The query is over a configured limit: limit 8 exceeds the maximum of 7. Ask for no more rows than the maximum, or leave limit out to get the first rows, with more set to true when rows were left out.");

    [Theory]
    [InlineData("{\"filters\":[{\"col\":\"region\",\"op\":\"eq\",\"value\":\"x\",\"<img src=x onerror=alert(1)>\":1}]}", "InvalidJson", "$.query.filters[0].<img src=x onerror=alert(1)>", "The member at path is unknown, repeated, missing its col or op, combines and with or, or is not the JSON type querySchema expects. Correct it against querySchema from describe source.")]
    [InlineData("{\"x'; DROP TABLE t; --\":1,\"x'; DROP TABLE t; --\":2}", "InvalidJson", "$.query.x'; DROP TABLE t; --", "The query must be one JSON object whose only members are select, filters, group_by, aggregates, order_by and limit, each used once and with the type querySchema shows. Correct it against querySchema from describe source.")]
    [InlineData("{\"filters\":[{\"col\":\"region\",\"op\":\"eq\",\"value\":{\"<script>\":\"1'; DROP TABLE x; --\"}}]}", "InvalidStructure", "$.query.filters[0].value", "The member at path is null, an empty filter group, or not the list or value querySchema expects there. Correct it against querySchema from describe source.")]
    public Task T16_hostile_query_text_stays_only_in_the_path(string query, string code, string path, string message) =>
        Refused(query, code, path, message);

    private static async Task BadBody(byte[] bytes,string code,string path)
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        var reads = 0;
        var runs = 0;
        await using var app = Host(logs,Catalog(logs,(_,_) => { reads++; return Batch(Rows()); }),Engine((_,_) => { runs++; return Data(); }));
        await app.StartAsync();
        app.Services.GetRequiredService<HostCatalogState>().Arm();
        using var client = Client(app);
        var response = await client.PostAsync("/query",new ByteArrayContent(bytes));
        var body = await Error(response,400,code,path,code=="InvalidJson" ? M1 : path=="$" ? M2 : M3);
        Assert.Equal(0,reads);
        Assert.Equal(0,runs);
        Assert.Single(logs.Errors);
        Assert.Equal("request",Assert.Single(logs.ErrorFields)["Step"]);
        AssertSafe(body); AssertLogSafe(logs);
    }

    private static async Task Refused(string query,string code,string path,string message,IDictionary<string,string?>? limits=null)
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        var reads = 0;
        var runs = 0;
        await using var app = Host(logs,Catalog(logs,(_,_) => { reads++; return Batch(Rows()); }),Engine((_,_) => { runs++; return Data(); }),limits);
        await app.StartAsync();
        app.Services.GetRequiredService<HostCatalogState>().Arm();
        using var client = Client(app);
        var expected = Assert.Throws<JsonQueryException>(() => new Query(Contract().ObjectName).WithJsonQuery(query,Contract().QueryColumns,app.Services.GetRequiredService<JsonQueryLimits>() with { MaxLimit = Contract().Source.RowCap }));
        var rooted = expected.Path is "$" or "" ? "$.query" : expected.Path.StartsWith('[') ? "$.query"+expected.Path : "$.query."+expected.Path;
        Assert.Equal(code,expected.Code.ToString());
        Assert.Equal(path,rooted);
        var response = await Post(client,query);
        var body = await Error(response,400,code,path,message);
        Assert.DoesNotContain("JSON query failed",body);
        using var parsed = JsonDocument.Parse(body);
        var wording = parsed.RootElement.GetProperty("message").GetString()!;
        foreach (var forbidden in new[] { "<img", "alert", "DROP", "<script>" }) Assert.DoesNotContain(forbidden,wording);
        Assert.Equal(1,reads);
        Assert.Equal(0,runs);
        Assert.Single(logs.Errors);
        var fields = Assert.Single(logs.ErrorFields);
        Assert.Equal("query",fields["Step"]);
        Assert.Equal(rooted,fields["Path"]);
        AssertSafe(body); AssertLogSafe(logs);
    }

    private static async Task<string> Error(HttpResponseMessage response,int status,string code,string path,string? message=null)
    {
        Assert.Equal(status,(int)response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        AssertError(response,text,code,path);
        using var body = JsonDocument.Parse(text);
        Assert.Equal(new[] { "code","path","message","incidentId" },body.RootElement.EnumerateObject().Select(p => p.Name));
        if (message is not null) Assert.Equal(message,body.RootElement.GetProperty("message").GetString());
        return text;
    }

    private static string Body(string query,string source="synthetic_sales") => "{\"source\":"+JsonSerializer.Serialize(source)+",\"query\":"+query+"}";
    private static Task<HttpResponseMessage> Post(HttpClient client,string query,string source="synthetic_sales") => client.PostAsync("/query",new StringContent(Body(query,source)));
    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(Assert.Single(app.Urls)) };
    private static IDatabaseEngine Engine(Func<CompiledQuery,CancellationToken,Task<IReadOnlyList<dynamic>>> run) =>
        FakeRows.Engine(new SqlServerEngine(), FakeRows.From((_,query,opened,token) => { opened(); return run(query,token); }));
    private static HostCatalogState Catalog(RecordedLogs logs, Func<string,CancellationToken,Task<(IReadOnlyList<dynamic> Rows,IReadOnlyList<dynamic> Available)>>? read = null)
    {
        var selected = read ?? ((string _, CancellationToken _) => Batch(Rows()));
        var catalog = HostCatalogState.Raw(_ => Task.FromResult<IReadOnlyList<dynamic>>([Rows()[0]]), selected);
        catalog.StartupList = _ => Task.FromResult(CatalogTableReader.MapList([Rows()[0]]));
        catalog.StartupDetail = (name, _) => Task.FromResult<CatalogDetail?>(CatalogTableReader.MapDetail(Rows(), "sqlserver", true));
        return catalog;
    }
    private static Task<IReadOnlyList<dynamic>> Data(params Dictionary<string,object?>[] rows) => Task.FromResult<IReadOnlyList<dynamic>>(rows);
    private static Task<(IReadOnlyList<dynamic> Rows,IReadOnlyList<dynamic> Available)> Batch(IReadOnlyList<dynamic> rows) => Task.FromResult<(IReadOnlyList<dynamic>,IReadOnlyList<dynamic>)>((rows,[]));
    private static Dictionary<string,object?>[] Rows(string objectName="hidden_schema.order_lines",string connection=SourceConnection) => Columns().Select(c => new Dictionary<string,object?>
    {
        ["id"] = 7319, ["object_name"] = objectName, ["connection_string"] = connection, ["name"] = "synthetic_sales", ["title"] = "Synthetic sales", ["description"] = "Invented lines", ["grain"] = "One line", ["row_cap"] = 7, ["query_timeout_seconds"] = 1,
        ["column_name"] = c.ColumnName, ["ordinal"] = c.Ordinal, ["column_title"] = c.Title, ["column_description"] = c.Description, ["data_type"] = c.DataType,
        ["unit"] = null, ["magnitude"] = null, ["display_format"] = null,
        ["can_select"] = c.CanSelect, ["can_filter"] = c.CanFilter, ["can_sort"] = c.CanSort, ["can_group"] = c.CanGroup, ["can_aggregate"] = c.CanAggregate, ["expression"] = c.Expression, ["store_type"] = c.StoreType, ["store_type_override"] = c.StoreTypeOverride
    }).ToArray();
    private static DataSourceColumnRow[] Columns() => [
        new("region",1,"Region","Invented region","text",null,null,null,true,true,true,true,true),
        new("OrderDay",2,"Day","Invented day","date",null,null,null,true,true,true,true,true),
        new("line_total",3,"Total","Invented total","number",null,null,null,true,true,true,false,true),
        new("is_return",4,"Return","Invented return","bool",null,null,null,true,true,false,true,false),
        new("note",5,"Note","Invented note","text",null,null,null,true,false,false,false,false),
        new("internal_code",6,"Internal","Invented internal","text",null,null,null,false,false,false,false,false) ];
    private static SourceContract Contract() => CatalogMapping.Map(new(new DataSourceRow("hidden_schema.order_lines",SourceConnection)
        { QueryTimeoutSeconds = 30, Id = 7319, Name="synthetic_sales",Title="Synthetic sales",Description="Invented lines",Grain="One line",RowCap = 7, IsActive =true },Columns())).Value;

    private static WebApplication Host(RecordedLogs logs, HostCatalogState? catalog, IDatabaseEngine? engine, IDictionary<string,string?>? settings = null)
    {
        if (catalog is null)
        {
            catalog = Catalog(logs);
            catalog.Detail = (name, token) => new SqlServerCatalog(new Dapper.Data.DbContext<Microsoft.Data.SqlClient.SqlConnection>(Connection), 1).QueryCatalogDetailAsync(name, token);
        }
        var app = Program.CreateApplication(new WebApplicationOptions
        {
            EnvironmentName="Production",ContentRootPath=TestFiles.InRepo("DataGateway.Web"),ApplicationName=typeof(Program).Assembly.GetName().Name,Args=[]
        }, builder =>
        {
            SyntheticSignIn.Use(builder);
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.WebHost.UseSetting("urls","http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?> { });
            if (settings is not null) builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddSingleton(catalog);
            catalog.Use(builder.Services);
            if (engine is not null) builder.Services.AddSingleton(engine);
        });
        return app;
    }

    private static DefaultHttpContext Context(CancellationToken token,Stream response)
    {
        var context = new DefaultHttpContext { RequestAborted=token };
        context.Request.Body=new MemoryStream(Encoding.UTF8.GetBytes(Body("{\"select\":[\"region\"]}")));
        context.Response.Body=response;
        return context;
    }
    private sealed class CancellingStream(CancellationTokenSource abort,string kind) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
        { abort.Cancel(); throw kind switch { "json" => new JsonException("fake-input-secret"), "cancelled" => new OperationCanceledException("fake-input-secret"), _ => new IOException("fake-input-secret") }; }
    }
    private sealed class AbortedWriteStream(CancellationTokenSource abort) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default)
        { abort.Cancel(); throw new IOException("fake-driver-secret"); }
    }
    private static Exception DriverFailure(string kind) => kind switch
    {
        "pool" => new InvalidOperationException("fake-driver-secret"),
        "io" => new IOException("fake-driver-secret"),
        "disposed" => new ObjectDisposedException("fake-driver-secret"),
        "timeout" => new TimeoutException("fake-driver-secret"),
        "argument" => new ArgumentException("fake-driver-secret"),
        "format" => new FormatException("fake-driver-secret"),
        "cast" => new InvalidCastException("fake-driver-secret"),
        "cancelled" => new OperationCanceledException("fake-driver-secret"),
        "sql" => (Microsoft.Data.SqlClient.SqlException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(Microsoft.Data.SqlClient.SqlException)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void AssertError(HttpResponseMessage response, string text, string code, string path)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(new[] { "code", "incidentId", "message", "path" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(path, json.RootElement.GetProperty("path").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("message").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssertLogSafe_catches_foreign_text_in_non_failure_entries(bool diagnostic)
    {
        using var logs = new RecordedLogs();
        var planted = diagnostic ? "PWD=unrecognized-foreign-value" : new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(Connection).Password;
        logs.CreateLogger("foreign fixture").LogInformation("{Detail}", planted);
        Assert.Empty(logs.ErrorFields);
        var failure = Assert.IsType<Xunit.Sdk.XunitException>(Record.Exception(() => AssertLogSafe(logs)));
        Assert.False(failure.Message.Contains(planted, StringComparison.Ordinal), "Log scan printed a planted value.");
    }

    [Fact]
    public void AssertLogSafe_catches_diagnostic_marker_in_non_failure_entries()
    {
        using var logs = new RecordedLogs();
        const string planted = "fake-driver-secret";
        logs.CreateLogger("foreign fixture").LogInformation("{Detail}", planted);
        Assert.Empty(logs.ErrorFields);
        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertLogSafe(logs));
        Assert.False(failure.Message.Contains(planted, StringComparison.OrdinalIgnoreCase), "Log scan printed a planted value.");
    }

    private static void AssertLogSafe(RecordedLogs logs)
    {
        var scan = LeakScan.Synthetic(("route catalog", Connection), ("route source", SourceConnection));
        foreach (var entry in logs.Entries.Where(entry => entry.Category != FailureLog.Category))
            AssertSafe(entry.Text);
        foreach (var fields in logs.ErrorFields)
            scan.LogLine("query line", JsonSerializer.SerializeToElement(new Dictionary<string, object?>(fields) { ["SourceContext"] = FailureLog.Category }));
    }

    private static void AssertSafe(string text)
    {
        LeakScan.Synthetic(("route catalog", Connection), ("route source", SourceConnection)).Text("route body and logs", text, Rule.Foreign);
        foreach (var marker in new[] { "127.0.0.1,1", "fake-driver-secret", "fake-input-secret", "hidden_schema", "order_lines", "malformed-query-connection-marker" })
            Assert.True(!text.Contains(marker, StringComparison.OrdinalIgnoreCase), "Diagnostic marker found in route output.");
    }

    private sealed class SettingsScope : IDisposable
    {
        private readonly Dictionary<string, string?> original = new();
        private readonly string scratch = TestFiles.InRepo("tests/DataGateway.Web.Tests/obj/query-settings/" + Guid.NewGuid());

        public SettingsScope()
        {
            Directory.CreateDirectory(scratch);
            foreach (var key in new[] { "APPDATA", "HOME", "DOTNET_USER_SECRETS_FALLBACK_DIR" }) Set(key, scratch);
            Set(DataGatewayConfiguration.ConnectionSetting, Connection);
            foreach (var name in new[] { "ConnectTimeoutSeconds", "CommandTimeoutSeconds", "RowCap", "MaxJsonLength", "MaxFilterDepth", "MaxFilterConditions", "MaxInValues" })
                Set("DataGateway__Limits__" + name, null);
        }

        private void Set(string key, string? value)
        {
            original.TryAdd(key, Environment.GetEnvironmentVariable(key));
            Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            foreach (var pair in original) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            Directory.Delete(scratch, recursive: true);
        }
    }

    private sealed class RecordedLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Text)> entries = new();
        private readonly ConcurrentQueue<Dictionary<string, object?>> errorFields = new();
        public IEnumerable<(string Category, LogLevel Level, string Text)> Entries => entries;
        public IEnumerable<Dictionary<string, object?>> ErrorFields => errorFields;
        public IEnumerable<string> Errors => entries.Where(entry => entry.Level >= LogLevel.Warning).Select(entry => entry.Text);
        public IEnumerable<LogLevel> Levels => entries.Where(entry => entry.Level >= LogLevel.Warning).Select(entry => entry.Level);
        public string Text => string.Join("\n", entries.Select(entry => entry.Text));
        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, entries, errorFields);
        public void Dispose() { }

        private sealed class Recorder(string category, ConcurrentQueue<(string Category, LogLevel Level, string Text)> entries,
            ConcurrentQueue<Dictionary<string, object?>> errorFields) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Assert.Null(exception);
                entries.Enqueue((category, logLevel, formatter(state, exception)));
                if (logLevel >= LogLevel.Warning)
                    errorFields.Enqueue(Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state)
                        .ToDictionary(pair => pair.Key, pair => pair.Value));
            }
        }
    }
}
