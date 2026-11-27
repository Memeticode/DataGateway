using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Data.Queries;
using DataGateway.Data.Sqlite;
using DataGateway.Web.Catalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DataGateway.Web.Tests.CatalogEngineTests;

namespace DataGateway.Web.Tests;

/// <summary>Every stored-catalog reader reads the reserved <c>expression</c> cell, and a filled-in one is refused on
/// every surface without its text reaching an answer or a log line.</summary>
public sealed class ExpressionColumnReaderTests
{
    private const string Marker = "reserved-expression-marker-5c1e";

    private static string Refusal(string column) => $"Column '{column}' has an expression, which this version does not support. " +
        "Remove the expression from the catalog and compute the column in the source's view.";

    [Theory]
    [InlineData(false, Marker)]
    [InlineData(false, "")]
    [InlineData(false, " ")]
    [InlineData(true, Marker)]
    [InlineData(true, "")]
    [InlineData(true, " ")]
    public async Task T1_example_readers_project_expression_by_name_and_the_gateway_refuses_it(bool pg, string expression)
    {
        var readSql = pg ? PostgreSqlCatalog.ReadSql : SqlServerCatalog.ReadSql;
        Assert.Contains(pg ? "c.can_aggregate, c.expression, c.store_type, c.store_type_override\n" : "c.[can_aggregate], c.[expression], c.[store_type], c.[store_type_override]\r\n", readSql);
        using var probe = new SourceProbe { AllowNextResult = true };
        var row = SourceRow();
        row["expression"] = expression;
        Load(probe, row);
        var detail = await CatalogTableReader.DetailAsync(new DbContext<ProbeConnection>("synthetic"), readSql,
            pg ? PostgreSqlCatalog.AvailableSql : SqlServerCatalog.AvailableSql, "synthetic", "sqlserver", !pg, 15, default);
        Assert.Equal(readSql, Assert.Single(probe.Commands).CommandText);
        // Read verbatim: an empty or blank cell is not folded to null, so it is refused like any other text.
        Assert.Equal(expression, Assert.Single(detail!.Rows.Columns).Expression);

        var gateway = new GatewayCatalog(_ => Task.FromResult<IReadOnlyList<SourceListRow>>([]),
            (_, _) => Task.FromResult<CatalogDetail?>(detail), new Dictionary<string, QueryRunner>(), _ => Task.FromResult(McpMetadataFixture.Seed));
        var failure = (await gateway.SourceAsync("synthetic", default)).Failure!;
        Assert.Equal("InvalidCatalog", failure.Code);
        Assert.Equal("$.source", failure.Path);
        Assert.Equal(Refusal("measure"), failure.Message);
        Assert.DoesNotContain(Marker, failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void T2_reader_treats_a_missing_or_non_text_expression_cell_as_a_bad_cell_and_null_as_none(bool integral)
    {
        foreach (var value in new object?[] { null, DBNull.Value })
        {
            var row = SourceRow();
            row["expression"] = value;
            Assert.Null(Assert.Single(CatalogTableReader.MapDetail([row], "sqlserver", integral).Rows.Columns).Expression);
        }
        var missing = SourceRow();
        missing.Remove("expression");
        Assert.Equal(CatalogTableReader.InvalidCell,
            Assert.Throws<InvalidCastException>(() => CatalogTableReader.MapDetail([missing], "sqlserver", integral)).Message);
        foreach (var value in new object[] { 42, true, new object() })
        {
            var row = SourceRow();
            row["expression"] = value;
            Assert.Equal(CatalogTableReader.InvalidCell,
                Assert.Throws<InvalidCastException>(() => CatalogTableReader.MapDetail([row], "sqlserver", integral)).Message);
        }
    }

    [Theory]
    [InlineData(Marker)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task T3_sqlite_catalog_reads_the_stored_cell_end_to_end(string expression)
    {
        using var fixture = new SqliteFixture();
        var before = await fixture.Catalog().QueryCatalogDetailAsync("synthetic_items", default);
        Assert.All(before!.Rows.Columns, column => Assert.Null(column.Expression));
        Assert.True(CatalogMapping.Map(before.Rows).IsSuccess);

        fixture.Change("UPDATE gateway_column SET expression=@v0 WHERE datasource_fk=101 AND column_name='amount'", expression);
        var after = await fixture.Catalog().QueryCatalogDetailAsync("synthetic_items", default);
        Assert.Equal(expression, Assert.Single(after!.Rows.Columns, column => column.ColumnName == "amount").Expression);
        Assert.All(after.Rows.Columns.Where(column => column.ColumnName != "amount"), column => Assert.Null(column.Expression));
        Assert.Equal(Refusal("amount"), CatalogMapping.Map(after.Rows).Failure!.Message);
    }

    [Theory]
    [InlineData("describe_source", "/sources/describe", """{"source":"synthetic_items"}""")]
    [InlineData("query_source", "/query", """{"source":"synthetic_items","query":{"select":["id"],"limit":1}}""")]
    public async Task T4_http_and_mcp_refuse_a_stored_expression_without_showing_it(string tool, string route, string body)
    {
        using var fixture = new SqliteFixture();
        using var settings = fixture.Settings();
        await using var host = fixture.Host(new SqliteEngine());
        await host.Start();
        // Filled in after startup, so the request path (not the startup check) meets it.
        fixture.Change("UPDATE gateway_column SET expression=@v0 WHERE datasource_fk=101 AND column_name='amount'", Marker);

        using var response = await host.Client.PostAsync(route, new StringContent(body, Encoding.UTF8, "application/json"));
        var http = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var mcp = await McpWire.Connect(host.Client);
        var result = await McpWire.Call(mcp, tool, body);
        Assert.True(result.IsError);
        var text = McpWire.Text(result);
        foreach (var answer in new[] { http, text })
            Assert.Equal(WireGolden.Cases["InvalidCatalog/route"].Body, Regex.Replace(answer, "\"incidentId\":\"[^\"]+\"", "\"incidentId\":\"INCIDENT\""));

        var lines = host.Lines();
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.Equal("InvalidCatalog", line.GetProperty("Code").GetString());
            Assert.Equal("$.source", line.GetProperty("Path").GetString());
            Assert.Equal(Refusal("amount"), line.GetProperty("Message").GetString());
            Assert.Equal("synthetic_items", line.GetProperty("SourceName").GetString());
        });
        var everything = http + text + string.Join("\n", host.AllLines().Select(line => line.GetRawText()));
        Assert.DoesNotContain(Marker, everything, StringComparison.Ordinal);

        // List never reads columns, so it still answers, and without the marker.
        using var list = await host.Client.PostAsync("/sources/list", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain(Marker, await list.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("X'7265736572766564'", 503, "CatalogUnavailable")]
    [InlineData("'reserved'", 500, "InternalError")]
    public async Task T6_named_gateway_reader_refuses_a_blob_or_text_expression(string literal, int status, string code)
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        await using var host = new NamedGatewayHost(builder => builder.Services.AddDataGateway("a",
            gateway => gateway.UseCatalog(state.Create).AddEngine(new NamedGatewayEngine())));
        host.App.MapDataGateway("a", "/a");
        await host.Start();
        Assert.Equal(200, (await host.Post("/a", "describe")).Status);
        // The literal is fixed test text, never caller input. A BLOB is not text, so it must not read as a null expression.
        fixture.Change("UPDATE gateway_column SET expression=" + literal + " WHERE datasource_fk=101 AND column_name='amount'");
        foreach (var operation in new[] { "describe", "query" })
        {
            var reply = await host.Post("/a", operation);
            Assert.Equal(status, reply.Status);
            using var json = JsonDocument.Parse(reply.Text);
            Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain("reserved", reply.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task T5_a_null_expression_leaves_describe_unchanged_on_http_and_mcp()
    {
        using var fixture = new SqliteFixture();
        using var settings = fixture.Settings();
        await using var host = fixture.Host(new SqliteEngine());
        await host.Start();
        const string body = """{"source":"synthetic_items"}""";
        using var response = await host.Client.PostAsync("/sources/describe", new StringContent(body, Encoding.UTF8, "application/json"));
        var http = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var mcp = await McpWire.Connect(host.Client);
        var text = McpWire.Text(await McpWire.Call(mcp, "describe_source", body));
        string[] members = ["name", "title", "description", "dataType", "unit", "magnitude", "displayFormat",
            "canSelect", "canFilter", "canSort", "canGroup", "canAggregate"];
        foreach (var answer in new[] { http, text })
        {
            using var json = JsonDocument.Parse(answer);
            var columns = json.RootElement.GetProperty("querySchema").GetProperty("columns").EnumerateArray().ToArray();
            Assert.Equal(new[] { "id", "label", "flag", "amount" }, columns.Select(column => column.GetProperty("name").GetString()));
            Assert.All(columns, column => Assert.Equal(members, column.EnumerateObject().Select(member => member.Name)));
        }
        Assert.Empty(host.Lines());
    }
}
