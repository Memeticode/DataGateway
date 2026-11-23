using System.Net;
using System.Text;
using System.Text.Json;
using DataGateway.Core.Catalog;
using DataGateway.Core.Tests;
using DataGateway.Data;
using DataGateway.Data.SqlServer;
using DataGateway.Web.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>Pins how the web host composes the library: its registration, routes, API page and MCP server info, the
/// startup check of the whole catalog before it listens, and the safe answers when the catalog fails after start.</summary>
/// <remarks><para>The host reads and validates the whole catalog once at startup; after start, each request reads the
/// catalog fresh, so a catalog change shows on the next request.</para>
/// <para>A missing or malformed catalog should stop the host before it listens, loudly, rather than show up later as
/// failures on individual requests.</para></remarks>
public sealed class HostCompositionTests
{
    /// <summary>With no MCP title or description configured, the API page's document and HTML equal the frozen copies
    /// byte for byte, on two separately started hosts, and a host's own extra route is unaffected.</summary>
    [Fact]
    public async Task T8_default_document_and_html_match_frozen_transforms_on_two_ports()
    {
        using var settings = new ErrorLogSettingsScope();
        foreach (var ignored in new[] { 0, 1 })
        {
            await using var host = Host(new HostCatalogState(), "Development", new Dictionary<string, string?> { ["DataGateway:Mcp:Title"] = "", ["DataGateway:Mcp:About"] = "" });
            host.App.MapGet("/extra", () => "extra");
            await host.Start();
            Assert.Equal("extra", await host.Client.GetStringAsync("/extra"));
            Assert.Equal(WebApiPageGolden.Document(), await host.Client.GetByteArrayAsync("/openapi/v1.json"));
            Assert.Equal(WebApiPageGolden.Html(), await host.Client.GetByteArrayAsync("/scalar/"));
            Assert.Empty(host.Lines());
        }
    }

    /// <summary>A realistic three-source catalog with optional fields passes the production list and detail mapping and
    /// validation, and the host starts on it after one list read and one detail read per source.</summary>
    [Fact]
    public async Task T2_synthetic_multi_source_catalog_uses_actual_mapping_and_startup_validation()
    {
        using var settings = new ErrorLogSettingsScope();
        using var sample = JsonDocument.Parse("""
            [
              {
                "name": "measurements",
                "object_name": "example.measurements",
                "title": "Measurements",
                "description": "Invented measurements for catalog mapping checks.",
                "grain": "One row per measurement (measure_01).",
                "is_active": true,
                "row_cap": 500,
                "columns": [
                  {"column_name": "measure_01", "ordinal": 1, "title": "Example field 1", "description": "Invented measurements field 1.", "data_type": "text", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_02", "ordinal": 2, "title": "Example field 2", "description": "Invented measurements field 2.", "data_type": "number", "unit": "USD", "magnitude": "thousands", "display_format": "N2", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": true},
                  {"column_name": "measure_03", "ordinal": 3, "title": "Example field 3", "description": "Invented measurements field 3.", "data_type": "date", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_04", "ordinal": 4, "title": "Example field 4", "description": "Invented measurements field 4.", "data_type": "bool", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": false},
                  {"column_name": "measure_05", "ordinal": 5, "title": "Example field 5", "description": "Invented measurements field 5.", "data_type": "text", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_06", "ordinal": 6, "title": "Example field 6", "description": "Invented measurements field 6.", "data_type": "number", "unit": "percent", "magnitude": "none", "display_format": "N1", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": true},
                  {"column_name": "measure_07", "ordinal": 7, "title": "Example field 7", "description": "Invented measurements field 7.", "data_type": "date", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_08", "ordinal": 8, "title": "Example field 8", "description": "Invented measurements field 8.", "data_type": "bool", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": false},
                  {"column_name": "measure_09", "ordinal": 9, "title": "Example field 9", "description": "Invented measurements field 9.", "data_type": "text", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_10", "ordinal": 10, "title": "Example field 10", "description": "Invented measurements field 10.", "data_type": "number", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": true},
                  {"column_name": "measure_11", "ordinal": 11, "title": "Example field 11", "description": "Invented measurements field 11.", "data_type": "date", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_12", "ordinal": 12, "title": "Example field 12", "description": "Invented measurements field 12.", "data_type": "bool", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": false},
                  {"column_name": "measure_13", "ordinal": 13, "title": "Example field 13", "description": "Invented measurements field 13.", "data_type": "text", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_14", "ordinal": 14, "title": "Example field 14", "description": "Invented measurements field 14.", "data_type": "number", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": true},
                  {"column_name": "measure_15", "ordinal": 15, "title": "Example field 15", "description": "Invented measurements field 15.", "data_type": "date", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_16", "ordinal": 16, "title": "Example field 16", "description": "Invented measurements field 16.", "data_type": "bool", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": false},
                  {"column_name": "measure_17", "ordinal": 17, "title": "Example field 17", "description": "Invented measurements field 17.", "data_type": "text", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_18", "ordinal": 18, "title": "Example field 18", "description": "Invented measurements field 18.", "data_type": "number", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": true},
                  {"column_name": "measure_19", "ordinal": 19, "title": "Example field 19", "description": "Invented measurements field 19.", "data_type": "date", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_20", "ordinal": 20, "title": "Example field 20", "description": "Invented measurements field 20.", "data_type": "bool", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": false},
                  {"column_name": "measure_21", "ordinal": 21, "title": "Example field 21", "description": "Invented measurements field 21.", "data_type": "text", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_22", "ordinal": 22, "title": "Example field 22", "description": "Invented measurements field 22.", "data_type": "number", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": true},
                  {"column_name": "measure_23", "ordinal": 23, "title": "Example field 23", "description": "Invented measurements field 23.", "data_type": "date", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_24", "ordinal": 24, "title": "Example field 24", "description": "Invented measurements field 24.", "data_type": "bool", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": false},
                  {"column_name": "measure_25", "ordinal": 25, "title": "Example field 25", "description": "Invented measurements field 25.", "data_type": "text", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_26", "ordinal": 26, "title": "Example field 26", "description": "Invented measurements field 26.", "data_type": "number", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": true},
                  {"column_name": "measure_27", "ordinal": 27, "title": "Example field 27", "description": "Invented measurements field 27.", "data_type": "date", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_28", "ordinal": 28, "title": "Example field 28", "description": "Invented measurements field 28.", "data_type": "bool", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": false},
                  {"column_name": "measure_29", "ordinal": 29, "title": "Example field 29", "description": "Invented measurements field 29.", "data_type": "text", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_30", "ordinal": 30, "title": "Example field 30", "description": "Invented measurements field 30.", "data_type": "number", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": true},
                  {"column_name": "measure_31", "ordinal": 31, "title": "Example field 31", "description": "Invented measurements field 31.", "data_type": "date", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_32", "ordinal": 32, "title": "Example field 32", "description": "Invented measurements field 32.", "data_type": "bool", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": false},
                  {"column_name": "measure_33", "ordinal": 33, "title": "Example field 33", "description": "Invented measurements field 33.", "data_type": "text", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "measure_34", "ordinal": 34, "title": "Example field 34", "description": "Invented measurements field 34.", "data_type": "number", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": true}
                ]
              },
              {
                "name": "parcels",
                "object_name": "example.parcels",
                "title": "Parcels",
                "description": "Invented parcels for catalog mapping checks.",
                "grain": "One row per parcel and package (parcel_key, package_index).",
                "is_active": true,
                "row_cap": 500,
                "columns": [
                  {"column_name": "parcel_key", "ordinal": 1, "title": "Example field 1", "description": "Invented parcels field 1.", "data_type": "text", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "package_index", "ordinal": 2, "title": "Example field 2", "description": "Invented parcels field 2.", "data_type": "number", "unit": "count", "magnitude": "thousands", "display_format": "N0", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": true}
                ]
              },
              {
                "name": "batches",
                "object_name": "example.batches",
                "title": "Batches",
                "description": "Invented batches for catalog mapping checks.",
                "grain": "One row per batch, stage and entry (batch_key, stage_key, entry_key).",
                "is_active": true,
                "row_cap": 500,
                "columns": [
                  {"column_name": "batch_key", "ordinal": 1, "title": "Example field 1", "description": "Invented batches field 1.", "data_type": "text", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false},
                  {"column_name": "stage_key", "ordinal": 2, "title": "Example field 2", "description": "Invented batches field 2.", "data_type": "number", "can_select": true, "can_filter": true, "can_sort": true, "can_group": false, "can_aggregate": true},
                  {"column_name": "entry_key", "ordinal": 3, "title": "Example field 3", "description": "Invented batches field 3.", "data_type": "date", "can_select": true, "can_filter": true, "can_sort": true, "can_group": true, "can_aggregate": false}
                ]
              }
            ]
            """);
        var sources = sample.RootElement.EnumerateArray().Select(row => row.Clone()).ToArray();
        Assert.Equal(3, sources.Length);
        Assert.Equal(39, sources.Sum(row => row.GetProperty("columns").GetArrayLength()));
        var list = DataGateway.Web.Catalog.CatalogTableReader.MapList(sources.Select(row => (dynamic)Cells(row)).ToArray());
        Assert.Empty(CatalogValidator.ValidateList(list));
        CatalogDetail Detail(string name)
        {
            var source = sources.Single(row => row.GetProperty("name").GetString() == name);
            Assert.True(source.GetProperty("is_active").GetBoolean());
            var rows = source.GetProperty("columns").EnumerateArray().Select(column =>
            {
                var cells = Cells(source);
                cells["id"] = 1;
                cells["query_timeout_seconds"] = 30;
                foreach (var field in new[] { "unit", "magnitude", "display_format" }) cells[field] = null;
                cells["connection_string"] = "Server=synthetic-sample;Database=synthetic-sample;Integrated Security=true";
                foreach (var (key, value) in Cells(column))
                    cells[key switch { "title" => "column_title", "description" => "column_description", _ => key }] = value;
                return (dynamic)cells;
            }).ToArray();
            var detail = DataGateway.Web.Catalog.CatalogTableReader.MapDetail(rows, "sqlserver", true);
            Assert.Empty(CatalogValidator.ValidateDetail(name, detail));
            var mapped = CatalogMapping.Map(detail.Rows);
            Assert.True(mapped.IsSuccess, mapped.Failure?.Message);
            Assert.Equal(source.GetProperty("columns").GetArrayLength(), detail.Rows.Columns.Count);
            Assert.Equal(source.GetProperty("object_name").GetString(), detail.Rows.Source.ObjectName);
            Assert.Equal(source.GetProperty("title").GetString(), detail.Rows.Source.Title);
            Assert.Equal(source.GetProperty("description").GetString(), detail.Rows.Source.Description);
            Assert.Equal(source.GetProperty("grain").GetString(), detail.Rows.Source.Grain);
            foreach (var column in source.GetProperty("columns").EnumerateArray())
            {
                var actual = Assert.Single(detail.Rows.Columns,
                    row => row.ColumnName == column.GetProperty("column_name").GetString());
                Assert.Equal(column.TryGetProperty("unit", out var unit) ? unit.GetString() : null, actual.Unit);
                Assert.Equal(column.TryGetProperty("magnitude", out var magnitude) ? magnitude.GetString() : null, actual.Magnitude);
                Assert.Equal(column.TryGetProperty("display_format", out var displayFormat) ? displayFormat.GetString() : null, actual.DisplayFormat);
            }
            return detail;
        }
        foreach (var source in list) _ = Detail(source.Name);
        var state = new HostCatalogState
        {
            StartupList = _ => Task.FromResult(list),
            StartupDetail = (name, _) => Task.FromResult<CatalogDetail?>(Detail(name))
        };
        await using var host = Host(state);
        await host.Start();
        Assert.Equal((1, 1, 3), state.Counts);
    }

    /// <summary>The scalar members of a sample JSON row as catalog cells.</summary>
    private static Dictionary<string, object?> Cells(JsonElement row) => row.EnumerateObject()
        .Where(property => property.Value.ValueKind != JsonValueKind.Array)
        .ToDictionary(property => property.Name, property => property.Value.ValueKind switch
        {
            JsonValueKind.String => (object?)property.Value.GetString(), JsonValueKind.Number => property.Value.GetInt32(),
            JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null,
            _ => throw new InvalidOperationException("The supplied sample contains an unsupported catalog cell.")
        });


    /// <summary>A logged web host on <paramref name="state"/>'s catalog.</summary>
    /// <param name="state">The catalog the host reads.</param>
    /// <param name="environment">The host environment name.</param>
    /// <param name="settings">Extra configuration.</param>
    /// <param name="engine">An engine to register, or <see langword="null"/> for the default.</param>
    private static LoggedHost Host(HostCatalogState state, string environment = "Production",
        IDictionary<string, string?>? settings = null, IDatabaseEngine? engine = null) =>
        new(environment: environment, settings: settings, configure: builder =>
        {
            state.Use(builder.Services);
            if (engine is not null) builder.Services.AddSingleton(engine);
        });

    /// <summary>The host registers one gateway with its configured limits and sign-in policy, maps its four named
    /// endpoints, reads nothing before start, and reads the list fresh on every request after start; the API page and
    /// MCP server carry the gateway's name and the library version.</summary>
    [Fact]
    public async Task T1_public_composition_root_metadata_limits_and_lazy_fresh_reads()
    {
        using var settings = new ErrorLogSettingsScope();
        var state = new HostCatalogState();
        var calls = 0;
        state.List = _ => Task.FromResult<IReadOnlyList<SourceListRow>>([HostCatalogState.HealthyList(++calls == 1 ? "first" : "second")]);
        await using var host = Host(state, "Development", new Dictionary<string, string?>
        {
            ["DataGateway:Limits:MaxJsonLength"] = "4000",
            ["DataGateway:Limits:MaxFilterDepth"] = "0", ["DataGateway:Limits:MaxFilterConditions"] = "3",
            ["DataGateway:Limits:MaxInValues"] = "2", ["DataGateway:Mcp:Title"] = " Display ", ["DataGateway:Mcp:About"] = " About "
        });
        Assert.Equal((0, 0, 0), state.Counts);
        var registration = Assert.Single(host.App.Services.GetServices<GatewayRegistration>());
        Assert.Equal("DataGateway.Web", registration.Name);
        Assert.Equal(4000, registration.Limits.MaxJsonLength);
        Assert.Equal(0, registration.Limits.MaxFilterDepth);
        Assert.Equal(3, registration.Limits.MaxFilterConditions);
        Assert.Equal(2, registration.Limits.MaxInValues);
        Assert.Equal("sign-in", registration.HostPolicy?.Failure.Step);
        await host.Start();
        Assert.Equal((1, 1, 1), state.Counts);
        state.Arm();
        var endpoints = ((IEndpointRouteBuilder)host.App).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToArray();
        foreach (var (path, operation) in new[] { ("/sources/list", "list"), ("/sources/describe", "describe"), ("/query", "query"), ("/mcp", "mcp") })
        {
            var endpoint = endpoints.Single(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "DataGateway:DataGateway.Web:" + operation);
            Assert.Equal(path, "/" + endpoint.RoutePattern.RawText?.Trim('/'));
            Assert.Equal("DataGateway:DataGateway.Web:" + operation, endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
            if (operation != "mcp") Assert.Equal(new[] { "POST" }, endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods);
        }
        foreach (var name in new[] { "first", "second" })
        {
            using var response = await host.Client.PostAsync("/sources/list", new StringContent("{}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(name, body.RootElement.GetProperty("sources")[0].GetProperty("name").GetString());
        }
        Assert.Equal((3, 3, 1), state.Counts);
        using var document = JsonDocument.Parse(await host.Client.GetByteArrayAsync("/openapi/v1.json"));
        Assert.Equal("DataGateway.Web", document.RootElement.GetProperty("info").GetProperty("title").GetString());
        var operations = document.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Value.GetProperty("post")).ToArray();
        Assert.Equal(new[] { "describe", "list", "query" }, operations.Select(p => p.GetProperty("operationId").GetString()).Order());
        foreach (var operation in operations) Assert.Equal("DataGateway.Web", Assert.Single(operation.GetProperty("tags").EnumerateArray()).GetString());
        Assert.Equal((3, 3, 1), state.Counts);
        await using var mcp = await McpWire.Connect(host.Client, "2025-11-25");
        Assert.Equal("DataGateway.Web", mcp.ServerInfo.Name);
        Assert.Equal("1.0.0", mcp.ServerInfo.Version);
        Assert.Equal(typeof(DataGatewayExtensions).Assembly.GetName().Version?.ToString(3), mcp.ServerInfo.Version);
        Assert.NotNull(await HostCatalogState.NamedRunner(host.App.Services));
    }

    /// <summary>The host does not listen until the list and every listed source's detail have been read.</summary>
    [Fact]
    public async Task T2_startup_awaits_list_and_every_active_detail_before_listening()
    {
        using var settings = new ErrorLogSettingsScope();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new HostCatalogState
        {
            StartupList = _ => Task.FromResult<IReadOnlyList<SourceListRow>>([HostCatalogState.HealthyList("one"), HostCatalogState.HealthyList("two")]),
            StartupDetail = async (name, token) =>
            {
                if (name == "two") { entered.SetResult(); await release.Task.WaitAsync(token); }
                return HostCatalogState.HealthyDetail(name);
            }
        };
        await using var host = Host(state);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var start = host.App.StartAsync(deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            Assert.False(start.IsCompleted);
            Assert.Empty(host.App.Urls);
            Assert.Equal((1, 1, 2), state.Counts);
        }
        finally { release.TrySetResult(); }
        await start;
        Assert.Single(host.App.Urls);
    }

    /// <summary>Each kind of bad catalog metadata stops startup with a message that names the gateway, has no inner
    /// exception and leaks nothing.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("provider")]
    [InlineData("columns")]
    [InlineData("all-off")]
    [InlineData("duplicate")]
    [InlineData("padded")]
    [InlineData("blank")]
    [InlineData("mismatch")]
    [InlineData("cap")]
    [InlineData("object")]
    [InlineData("connection")]
    public async Task T2_invalid_metadata_refuses_startup_safely(string kind)
    {
        using var settings = new ErrorLogSettingsScope();
        var state = new HostCatalogState();
        state.StartupList = _ => Task.FromResult<IReadOnlyList<SourceListRow>>(kind switch
        {
            "duplicate" => [HostCatalogState.HealthyList(), HostCatalogState.HealthyList("SYNTHETIC")],
            "padded" => [HostCatalogState.HealthyList(" synthetic ")],
            "blank" => [HostCatalogState.HealthyList("")],
            _ => [HostCatalogState.HealthyList()]
        });
        state.StartupDetail = (name, _) => Task.FromResult(Change(HostCatalogState.HealthyDetail(name), kind));
        await using var host = Host(state);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.App.StartAsync(deadline.Token));
        Assert.StartsWith("Gateway 'DataGateway.Web'.", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Empty(host.App.Urls);
        LoggedHost.Scan.Text("safe metadata refusal", failure.Message, Rule.DataGateway);
    }

    /// <summary>An empty catalog, or a source whose connection or object would only fail when queried, does not stop
    /// startup: startup checks catalog metadata and never connects to a source.</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("malformed-source-connection")]
    [InlineData("missing-source-object")]
    public async Task T2_empty_or_unexecuted_source_metadata_can_start(string kind)
    {
        using var settings = new ErrorLogSettingsScope();
        var state = new HostCatalogState();
        if (kind == "empty") state.StartupList = _ => Task.FromResult<IReadOnlyList<SourceListRow>>([]);
        else state.StartupDetail = (name, _) =>
        {
            var detail = HostCatalogState.HealthyDetail(name);
            return Task.FromResult<CatalogDetail?>(new(detail.Provider, new(Copy(detail.Rows.Source, connection: kind == "malformed-source-connection" ? "unparseable" : detail.Rows.Source.ConnectionString, objectName: "dbo.absent_object"), detail.Rows.Columns)));
        };
        await using var host = Host(state);
        await host.Start();
        Assert.Equal(kind == "empty" ? 0 : 1, state.Details);
    }

    /// <summary>A catalog fault after start answers with the exact CatalogUnavailable error (or InternalError for a
    /// cancellation) on both the route and the MCP tool, each with its own incident id and record, and no fault message.</summary>
    [Theory]
    [InlineData("list", "io")]
    [InlineData("detail", "io")]
    [InlineData("list", "timeout")]
    [InlineData("detail", "format")]
    [InlineData("list", "argument")]
    [InlineData("detail", "unexpected")]
    [InlineData("list", "cast")]
    [InlineData("detail", "cancel")]
    [InlineData("factory", "unexpected")]
    public async Task T5_post_start_faults_preserve_exact_safe_envelopes_and_diagnostics(string origin, string kind)
    {
        using var settings = new ErrorLogSettingsScope();
        var state = new HostCatalogState();
        state.List = _ => throw Fault(kind);
        state.Detail = (_, _) => throw Fault(kind);
        
        await using var host = Host(state);
        await host.Start();
        await using var mcp = await McpWire.Connect(host.Client);
        if (origin == "factory") state.FactoryFailure = () => Fault(kind);
        state.Arm();
        var operation = origin == "detail" ? "describe" : "list";
        using var response = await host.Client.PostAsync(ErrorLogTests.Route(operation), new StringContent(ErrorLogTests.Body(operation)));
        Assert.Equal(kind == "cancel" ? 500 : 503, (int)response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        var code = kind == "cancel" ? "InternalError" : "CatalogUnavailable";
        Assert.Equal(new[] { "code", "path", "message", "incidentId" }, body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(kind == "cancel" ? "$" : operation == "list" ? "$" : "$.source", body.RootElement.GetProperty("path").GetString());
        if (kind != "cancel") Assert.Equal("Cannot read the catalog. Check catalog availability and the configured catalog connection. Tell the user, give the incidentId and stop; do not retry.", body.RootElement.GetProperty("message").GetString());
        var line = Assert.Single(host.Lines());
        var incident = body.RootElement.GetProperty("incidentId").GetString();
        void AssertDiagnostics(JsonElement actualLine, string? expectedIncident, string actualText)
        {
            Assert.Equal(expectedIncident, actualLine.GetProperty("IncidentId").GetString());
            Assert.Equal("DataGateway.Web", actualLine.GetProperty("Gateway").GetString());
            Assert.Equal(kind == "cancel" ? "CatalogCancelled" : "CatalogUnavailable", actualLine.GetProperty("Code").GetString());
            Assert.Equal(Fault(kind).GetType().Name, actualLine.GetProperty("ExceptionType").GetString());
            foreach (var field in new[] { "SqlNumber", "DriverMessage", "ExceptionText", "SourceId" })
                Assert.Equal(JsonValueKind.Null, actualLine.GetProperty(field).ValueKind);
            if (operation == "describe")
                Assert.Equal("synthetic", actualLine.GetProperty("SourceName").GetString());
            else
                Assert.Equal(JsonValueKind.Null, actualLine.GetProperty("SourceName").ValueKind);
            Assert.DoesNotContain("planted-private-marker", actualText + actualLine.GetRawText());
        }
        AssertDiagnostics(line, incident, text);
        var result = await McpWire.Call(mcp, operation == "list" ? "list_sources" : "describe_source", ErrorLogTests.Body(operation));
        Assert.True(result.IsError);
        Assert.Single(result.Content);
        McpWire.SameDocument(text, McpWire.Text(result));
        var lines = host.Lines();
        Assert.Equal(2, lines.Length);
        var toolText = McpWire.Text(result);
        using var toolBody = JsonDocument.Parse(toolText);
        var toolIncident = toolBody.RootElement.GetProperty("incidentId").GetString();
        Assert.NotEqual(incident, toolIncident);
        var toolLine = Assert.Single(lines, candidate => candidate.GetProperty("IncidentId").GetString() == toolIncident);
        AssertDiagnostics(toolLine, toolIncident, toolText);
        Assert.NotEqual(lines[0].GetProperty("IncidentId").GetString(), lines[1].GetProperty("IncidentId").GetString());
    }

    /// <summary>When a source is not found and the list read used to name the available sources then fails, the
    /// answer is the catalog failure at the root path, not an unknown source.</summary>
    [Fact]
    public async Task T5_failed_full_list_after_missing_detail_wins_with_root_path_and_null_source()
    {
        using var settings = new ErrorLogSettingsScope();
        var state = new HostCatalogState
        {
            Detail = (_, _) => Task.FromResult<CatalogDetail?>(null),
            List = _ => throw new IOException("planted-private-marker")
        };
        await using var host = Host(state);
        await host.Start();
        var baseline = state.Counts;
        state.Arm();
        using var response = await host.Client.PostAsync("/sources/describe", new StringContent("{\"source\":\"absent\"}"));
        Assert.Equal(503, (int)response.StatusCode);
        var route = await response.Content.ReadAsStringAsync();
        string? AssertEnvelope(string actualText, string expectedPath)
        {
            using var answer = JsonDocument.Parse(actualText);
            Assert.Equal(new[] { "code", "path", "message", "incidentId" }, answer.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("CatalogUnavailable", answer.RootElement.GetProperty("code").GetString());
            Assert.Equal(expectedPath, answer.RootElement.GetProperty("path").GetString());
            Assert.Equal("Cannot read the catalog. Check catalog availability and the configured catalog connection. Tell the user, give the incidentId and stop; do not retry.", answer.RootElement.GetProperty("message").GetString());
            Assert.DoesNotContain("planted-private-marker", actualText);
            return answer.RootElement.GetProperty("incidentId").GetString();
        }
        void AssertDiagnostics(JsonElement actualLine, string? expectedIncident, string expectedOperation)
        {
            Assert.Equal(expectedIncident, actualLine.GetProperty("IncidentId").GetString());
            Assert.Equal("DataGateway.Web", actualLine.GetProperty("Gateway").GetString());
            Assert.Equal("CatalogUnavailable", actualLine.GetProperty("Code").GetString());
            Assert.Equal("IOException", actualLine.GetProperty("ExceptionType").GetString());
            Assert.Equal(expectedOperation, actualLine.GetProperty("Operation").GetString());
            Assert.Equal(JsonValueKind.Null, actualLine.GetProperty("SourceName").ValueKind);
            foreach (var field in new[] { "SqlNumber", "DriverMessage", "ExceptionText", "SourceId" })
                Assert.Equal(JsonValueKind.Null, actualLine.GetProperty(field).ValueKind);
            Assert.DoesNotContain("planted-private-marker", actualLine.GetRawText());
        }
        var incident = AssertEnvelope(route, "$");
        Assert.Equal((baseline.Factories + 1, baseline.Lists + 1, baseline.Details + 1), state.Counts);
        var line = Assert.Single(host.Lines());
        Assert.Single(host.Lines(), candidate => candidate.GetProperty("IncidentId").GetString() == incident);
        AssertDiagnostics(line, incident, "describe");
        await using var mcp = await McpWire.Connect(host.Client);
        var beforeTool = state.Counts;
        var result = await McpWire.Call(mcp, "describe_source", "{\"source\":\"absent\"}");
        Assert.Equal((beforeTool.Factories + 1, beforeTool.Lists + 1, beforeTool.Details + 1), state.Counts);
        Assert.True(result.IsError);
        Assert.Single(result.Content);
        var tool = McpWire.Text(result);
        McpWire.SameDocument(route, tool);
        var toolIncident = AssertEnvelope(tool, "$");
        Assert.NotEqual(incident, toolIncident);
        var lines = host.Lines();
        Assert.Equal(2, lines.Length);
        var toolLine = Assert.Single(lines, candidate => candidate.GetProperty("IncidentId").GetString() == toolIncident);
        AssertDiagnostics(toolLine, toolIncident, "describe_source");
    }

    /// <summary>The web host's source uses only the library's public composition methods, never its internal
    /// namespaces or types.</summary>
    /// <remarks>The web host is the library's reference application, so it is built only from what any other
    /// application can use. A dependency on internals would hide gaps in the public API and give other hosts code they
    /// cannot copy.</remarks>
    [Fact]
    public void T1_every_production_host_source_uses_the_public_library_surface()
    {
        var files = Directory.GetFiles(TestFiles.InRepo("DataGateway.Web"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"));
        foreach (var path in files)
        {
            var source = File.ReadAllText(path);
            foreach (var name in new[] { "DataGateway.Failures", "DataGateway.Routes", "DataGateway.Mcp", "DataGateway.Page", "GatewayRegistration", "HostOperationDocument" })
                Assert.DoesNotContain(name, source, StringComparison.Ordinal);
        }
        var program = File.ReadAllText(TestFiles.InRepo("DataGateway.Web/Program.cs"));
        Assert.DoesNotContain("Catalog" + "ReaderFactory", program, StringComparison.Ordinal);
        foreach (var member in new[] { "AddDataGateway(", "HostCatalogs.Use(", "AddEngine(", "UseLimits(", "UseHostPolicy(", "UseDataGatewayErrors(", "MapDataGateway(", "MapDataGatewayApiPage(" })
            Assert.Contains(member, program, StringComparison.Ordinal);
    }

    /// <summary>A catalog read that fails or is cancelled at startup stops the host with a fixed message and no inner
    /// exception, so the fault's message and inner details never reach the startup error.</summary>
    [Theory]
    [InlineData("list")]
    [InlineData("detail")]
    [InlineData("cancellation")]
    public async Task T2_host_runs_startup_validation_and_suppresses_exception_details(string origin)
    {
        using var settings = new ErrorLogSettingsScope();
        var state = new HostCatalogState();
        if (origin == "list") state.StartupList = _ => throw new IOException("Password=planted-private-marker", new Exception("private inner"));
        else state.StartupDetail = (_, _) => throw (origin == "cancellation"
            ? new OperationCanceledException("Password=planted-private-marker") : new IOException("Password=planted-private-marker", new Exception("private inner")));
        await using var host = Host(state);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.App.StartAsync());
        Assert.Empty(host.App.Urls);
        Assert.Null(failure.InnerException);
        Assert.StartsWith("Gateway 'DataGateway.Web'.", failure.Message);
        Assert.Contains(origin == "cancellation" ? "The catalog read was cancelled." : "Cannot read the catalog.", failure.Message);
        Assert.DoesNotContain("planted-private-marker", failure.ToString());
        Assert.DoesNotContain("private inner", failure.ToString());
        LoggedHost.Scan.Text("startup exception refusal", failure.ToString(), Rule.DataGateway);
    }

    /// <summary>The host's forwarding engine refuses calls before it is bound and a second bind, and then passes every
    /// argument, result and exception through to the selected engine unchanged.</summary>
    [Fact]
    public async Task T3_forwarding_engine_preserves_every_argument_result_and_exception()
    {
        var selected = new ForwardingRecorder();
        IDatabaseEngine engine = new HostDatabaseEngine("configured-alias");
        var forwarding = (HostDatabaseEngine)engine;
        Assert.Throws<InvalidOperationException>(() => engine.ValidateSourceConnection("before bind"));
        forwarding.Bind(selected);
        Assert.Equal("configured-alias", engine.ProviderName);
        Assert.Throws<InvalidOperationException>(() => forwarding.Bind(selected));
        var query = DataGateway.Core.Queries.QueryBuilding.Validate(ErrorLogTests.Contract(), "{}", QueryLimits.ForRowCap(500)).Value;
        Assert.Same(selected.Compiled, engine.Compile(query));
        Assert.Same(query, Assert.Single(selected.Arguments));
        engine.ValidateSourceConnection("source original");
        Assert.Equal(new object?[] { "source original" }, selected.Arguments);
        var original = new IOException("original");
        Assert.Same(selected.ProviderFailure, engine.RecognizeFailure(original));
        Assert.Same(original, Assert.Single(selected.Arguments));
        using var caller = new CancellationTokenSource();
        Action opened = () => { };
        Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<DataGateway.Core.Queries.QueryRows>> consume = _ => selected.Rows;
        Assert.Same(selected.Rows, engine.ReadSourceAsync("source original", selected.Compiled, 23, opened, consume, caller.Token));
        Assert.Equal(new object?[] { "source original", selected.Compiled, 23, opened, consume, caller.Token }, selected.Arguments);
        selected.Error = original;
        Assert.Same(original, Record.Exception(() => engine.Compile(query)));
        Assert.Same(original, Record.Exception(() => engine.ValidateSourceConnection(null)));
        Assert.Same(original, Record.Exception(() => engine.RecognizeFailure(original)));
        Assert.Same(original, await Record.ExceptionAsync(async () => await engine.ReadSourceAsync("s", selected.Compiled, 1, opened, consume, caller.Token)));
    }

    /// <summary>An engine that records the arguments of its last call, returns fixed results, and throws
    /// <see cref="Error"/> when it is set.</summary>
    private sealed class ForwardingRecorder : IDatabaseEngine
    {
        /// <summary>The provider name, distinct from the forwarding engine's alias.</summary>
        public string ProviderName => "selected-provider";
        /// <summary>The arguments of the last call.</summary>
        internal object?[] Arguments = [];
        /// <summary>When set, every call throws it.</summary>
        internal Exception? Error;
        /// <summary>The query <see cref="Compile"/> returns.</summary>
        internal readonly DataGateway.Core.Queries.CompiledQuery Compiled = new SqlServerEngine().Compile(DataGateway.Core.Queries.QueryBuilding.Validate(ErrorLogTests.Contract(), "{}", QueryLimits.ForRowCap(500)).Value);
        /// <summary>The failure <see cref="RecognizeFailure"/> returns.</summary>
        internal readonly ProviderFailure ProviderFailure = new(false, 7);
        /// <summary>The task <see cref="ReadSourceAsync"/> returns.</summary>
        internal readonly Task<DataGateway.Core.Queries.QueryRows> Rows = Task.FromResult(new DataGateway.Core.Queries.QueryRows([], false));
        /// <summary>Records the call's arguments and throws <see cref="Error"/> when set.</summary>
        private void Record(params object?[] args) { Arguments = args; if (Error is { } error) throw error; }
        /// <summary>Records the call and returns <see cref="Compiled"/>.</summary>
        public DataGateway.Core.Queries.CompiledQuery Compile(DataGateway.Core.Queries.ValidatedQuery query) { Record(query); return Compiled; }
        /// <summary>Records the call.</summary>
        public void ValidateSourceConnection(string? connection) => Record(connection);
        /// <summary>Records the call and returns <see cref="ProviderFailure"/>.</summary>
        public ProviderFailure? RecognizeFailure(Exception error) { Record(error); return ProviderFailure; }
        /// <summary>Records the call and returns <see cref="Rows"/>.</summary>
        public Task<DataGateway.Core.Queries.QueryRows> ReadSourceAsync(string connection, DataGateway.Core.Queries.CompiledQuery query, int timeout, Action opened, Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<DataGateway.Core.Queries.QueryRows>> consume, CancellationToken token)
        { Record(connection, query, timeout, opened, consume, token); return Rows; }
    }

    /// <summary>A blank object name or connection found after start is an internal catalog fault: the caller gets the
    /// generic InternalError answer, while the record keeps the InvalidCatalog code, path and fix.</summary>
    [Theory]
    [InlineData("object", "objectName", "Set objectName to a non-blank value.")]
    [InlineData("connection", "connectionString", "Set connectionString to a non-blank value.")]
    public async Task T5_runtime_blank_metadata_keeps_internal_catalog_refusal(string kind, string field, string correction)
    {
        using var settings = new ErrorLogSettingsScope();
        var state = new HostCatalogState { Detail = (name, _) => Task.FromResult(Change(HostCatalogState.HealthyDetail(name), kind)) };
        await using var host = Host(state);
        await host.Start();
        state.Arm();
        await using var mcp = await McpWire.Connect(host.Client);
        const string request = "{\"source\":\"synthetic\",\"query\":{}}";
        using var response = await host.Client.PostAsync("/query", new StringContent(request));
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(500, (int)response.StatusCode);
        Assert.Equal(WireGolden.Cases["InvalidCatalog/route"].Body, WireGoldenTests.Normalize(text));
        using var body = JsonDocument.Parse(text);
        Assert.Equal(new[] { "code", "path", "message", "incidentId" }, body.RootElement.EnumerateObject().Select(property => property.Name));
        var tool = await McpWire.Call(mcp, "query_source", request);
        Assert.True(tool.IsError);
        Assert.Single(tool.Content);
        Assert.Equal(WireGoldenTests.Normalize(text), WireGoldenTests.Normalize(McpWire.Text(tool)));
        var lines = host.Lines();
        Assert.Equal(2, lines.Length);
        Assert.NotEqual(lines[0].GetProperty("IncidentId").GetString(), lines[1].GetProperty("IncidentId").GetString());
        for (var index = 0; index < 2; index++)
        {
            var line = lines[index];
            using var answer = JsonDocument.Parse(index == 0 ? text : McpWire.Text(tool));
            Assert.Equal(answer.RootElement.GetProperty("incidentId").GetString(), line.GetProperty("IncidentId").GetString());
            Assert.Equal("DataGateway.Web", line.GetProperty("Gateway").GetString());
            Assert.Equal("InvalidCatalog", line.GetProperty("Code").GetString());
            Assert.Equal("InternalError", line.GetProperty("ReturnedCode").GetString());
            Assert.Equal("$.detail.rows.source." + field, line.GetProperty("Path").GetString());
            Assert.Equal("Source 'synthetic'. " + correction, line.GetProperty("Message").GetString());
            Assert.Equal("synthetic", line.GetProperty("SourceName").GetString());
            Assert.Equal(7319, line.GetProperty("SourceId").GetInt32());
            foreach (var name in new[] { "ExceptionType", "SqlNumber", "DriverMessage", "ExceptionText", "Sql", "SqlParameters" })
                Assert.Equal(JsonValueKind.Null, line.GetProperty(name).ValueKind);
        }
    }

    /// <summary>An exception of the named kind carrying a private marker that must never reach an answer or a record.</summary>
    private static Exception Fault(string kind) => kind switch
    {
        "io" => new IOException("planted-private-marker"), "timeout" => new TimeoutException("planted-private-marker"),
        "format" => new FormatException("planted-private-marker"), "argument" => new ArgumentException("planted-private-marker"),
        "cast" => new InvalidCastException("planted-private-marker"), "cancel" => new OperationCanceledException("planted-private-marker"),
        _ => new NotSupportedException("planted-private-marker")
    };

    /// <summary>A copy of <paramref name="row"/> with the given fields replaced.</summary>
    private static DataSourceRow Copy(DataSourceRow row, string? name = null, int? cap = null, string? objectName = null, string? connection = null) =>
        new(objectName ?? row.ObjectName, connection ?? row.ConnectionString)
        {
            Id = row.Id, Name = name ?? row.Name, Title = row.Title, Description = row.Description,
            Grain = row.Grain, IsActive = row.IsActive, QueryTimeoutSeconds = row.QueryTimeoutSeconds, RowCap = cap ?? row.RowCap
        };

    /// <summary>A detail broken in the named way, or <see langword="null"/> for a missing detail.</summary>
    private static CatalogDetail? Change(CatalogDetail detail, string kind) => kind switch
    {
        "missing" => null,
        "provider" => new("unregistered", detail.Rows),
        "columns" => new(detail.Provider, new(detail.Rows.Source, [])),
        "all-off" => new(detail.Provider, new(detail.Rows.Source, detail.Rows.Columns.Select(c => c with { CanSelect = false, CanFilter = false, CanSort = false, CanGroup = false, CanAggregate = false }).ToArray())),
        "mismatch" => new(detail.Provider, new(Copy(detail.Rows.Source, name: "other"), detail.Rows.Columns)),
        "cap" => new(detail.Provider, new(Copy(detail.Rows.Source, cap: 0), detail.Rows.Columns)),
        "object" => new(detail.Provider, new(Copy(detail.Rows.Source, objectName: " "), detail.Rows.Columns)),
        "connection" => new(detail.Provider, new(Copy(detail.Rows.Source, connection: " "), detail.Rows.Columns)),
        _ => detail
    };
}
