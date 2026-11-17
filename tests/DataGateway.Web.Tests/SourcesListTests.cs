using DataGateway.Web.Catalog;
using DataGateway.Failures;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using DataGateway.Web.Configuration;
using DataGateway.Core.Catalog;
using DataGateway.Core.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class SourcesListTests
{
    private const string Connection = "Server=tcp:127.0.0.1,1;Database=Catalog;User ID=fake-list-reader;Password=fake-list-password;Encrypt=false;Pooling=false;Connect Timeout=1;ConnectRetryCount=0";

    [Fact]
    public async Task T3_real_host_unreachable_catalog_is_503_and_logged_once_without_connection_fragments()
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        await using var app = Host(logs);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(timeout.Token);
        app.Services.GetRequiredService<HostCatalogState>().Arm();
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        using var response = await client.PostAsJsonAsync("/sources/list", new { }, timeout.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        AssertError(response, body, "CatalogUnavailable");
        Assert.Contains("CatalogUnavailable", Assert.Single(logs.Errors));
        var fields = Assert.Single(logs.ErrorFields);
        Assert.Equal("SqlException", fields["ExceptionType"]);
        Assert.Null(fields["SqlNumber"]);
        Assert.Null(fields["DriverMessage"]);
        Assert.Null(fields["ExceptionText"]);
        Assert.Equal("catalog", fields["Step"]);
        AssertSafe(body + logs.Text);
        await app.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task Real_host_success_has_exact_shape_and_reads_again_on_the_next_request()
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        var calls = 0;
        var reader = Catalog(logs, _ => Task.FromResult<IReadOnlyList<dynamic>>(
            [Row("zulu"), Row(Interlocked.Increment(ref calls) == 1 ? "alpha" : "beta")]));
        await using var app = Host(logs, reader);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(timeout.Token);
        app.Services.GetRequiredService<HostCatalogState>().Arm();
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        foreach (var expected in new[] { "alpha", "beta" })
        {
            using var response = await client.PostAsJsonAsync("/sources/list", new { }, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            Assert.Equal("sources", Assert.Single(body.RootElement.EnumerateObject()).Name);
            var sources = body.RootElement.GetProperty("sources").EnumerateArray().ToArray();
            Assert.Equal(new[] { expected, "zulu" }, sources.Select(source => source.GetProperty("name").GetString()));
            foreach (var source in sources)
            {
                Assert.Equal(new[] { "description", "grain", "name", "rowCap", "title" },
                    source.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
                Assert.Equal("Synthetic title", source.GetProperty("title").GetString());
                Assert.Equal("Synthetic description", source.GetProperty("description").GetString());
                Assert.Equal("One synthetic row", source.GetProperty("grain").GetString());
                Assert.Equal(500, source.GetProperty("rowCap").GetInt32());
            }
        }
        Assert.Equal(2, calls);
        Assert.Empty(logs.Errors);
        await app.StopAsync(timeout.Token);
    }

    [Theory]
    [InlineData("timeout", 503, "CatalogUnavailable")]
    [InlineData("argument", 503, "CatalogUnavailable")]
    [InlineData("cast", 503, "CatalogUnavailable")]
    public async Task Real_host_uses_shared_error_mapping_and_keeps_internal_catalog_code_in_the_log(
        string kind, int status, string code)
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        await using var app = Host(logs, Catalog(logs, _ => throw DriverFailure(kind)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(timeout.Token);
        app.Services.GetRequiredService<HostCatalogState>().Arm();
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        using var response = await client.PostAsJsonAsync("/sources/list", new { }, timeout.Token);
        Assert.Equal(status, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        AssertError(response, body, code);
        var error = Assert.Single(logs.Errors);
        Assert.Contains("CatalogUnavailable", error);
        using var expectedBody = JsonDocument.Parse(body);
        Assert.Equal("Cannot read the catalog. Check catalog availability and the configured catalog connection. Tell the user. Do not retry.", expectedBody.RootElement.GetProperty("message").GetString());
        if (status == 500)
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal("DataGateway could not complete the request because of a fault on its side, not in the request. Contact the DataGateway administrator. Tell the user. Do not retry.", json.RootElement.GetProperty("message").GetString());
            Assert.DoesNotContain(kind == "argument" ? "CatalogConfiguration" : "InvalidCatalog", body);
        }
        AssertSafe(body + logs.Text);
        await app.StopAsync(timeout.Token);
    }

    [Theory]
    [InlineData("pool")]
    [InlineData("io")]
    [InlineData("disposed")]
    [InlineData("timeout")]
    [InlineData("argument")]
    [InlineData("format")]
    [InlineData("cast")]
    [InlineData("sql")]
    public async Task List_cancellation_wins_over_driver_failure_and_route_writes_no_body(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        var logs = new RecordedLogs();
        var reader = Catalog(logs, _ =>
        {
            cancellation.Cancel();
            throw DriverFailure(kind);
        });
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        using var body = new MemoryStream();
        context.Response.Body = body;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        await PublicGatewayProbe.Invoke(context, "list", reader, logs);
        Assert.Equal(0, body.Length);
        Assert.False(context.Response.HasStarted);
        Assert.Contains("CatalogCancelled", Assert.Single(logs.Errors));
        Assert.Equal(LogLevel.Warning, Assert.Single(logs.Levels));
        AssertSafe(logs.Text);
    }

    [Fact]
    public async Task Real_host_request_abort_reaches_catalog_reader()
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Catalog(logs, async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            return [];
        });
        await using var app = Host(logs, reader);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(timeout.Token);
        app.Services.GetRequiredService<HostCatalogState>().Arm();
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        using var abort = new CancellationTokenSource();
        var request = client.PostAsJsonAsync("/sources/list", new { }, abort.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await app.StopAsync(timeout.Token);
        Assert.Contains("CatalogCancelled", Assert.Single(logs.Errors));
        Assert.Equal(LogLevel.Warning, Assert.Single(logs.Levels));
    }

    [Fact]
    public async Task Pre_cancelled_call_does_not_enter_database_and_returns_no_body()
    {
        var logs = new RecordedLogs();
        var reader = Catalog(logs, _ => throw new NotSupportedException("Must not read"));
        var context = new DefaultHttpContext { RequestAborted = new CancellationToken(true) };
        using var body = new MemoryStream();
        context.Response.Body = body;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        await PublicGatewayProbe.Invoke(context, "list", reader, logs);
        Assert.Equal(0, body.Length);
        Assert.Empty(logs.Errors);
    }

    [Fact]
    public void List_sql_reads_only_the_five_list_fields_and_filters_active_sources()
    {
        var projection = SqlServerCatalog.ListSql[..SqlServerCatalog.ListSql.IndexOf("FROM", StringComparison.Ordinal)];
        var fields = System.Text.RegularExpressions.Regex.Matches(projection, @"\[([^\]]+)\]")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "name", "title", "description", "grain", "row_cap" }, fields);
        Assert.DoesNotContain("*", projection);
        Assert.DoesNotContain("connection_string", SqlServerCatalog.ListSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE [is_active] = 1", SqlServerCatalog.ListSql);
        Assert.DoesNotContain("ORDER BY", SqlServerCatalog.ListSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task T12_list_body_is_checked_before_catalog_access()
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        var calls = 0;
        await using var app = Host(logs, Catalog(logs, _ => { calls++; return Task.FromResult<IReadOnlyList<dynamic>>([]); }));
        await Start(app);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        var invalidJson = new[] { "", "not json", "{} {}", "/*c*/{}", "{", new string('[', 65) + "0" + new string(']', 65) }
            .Select(Encoding.UTF8.GetBytes);
        var invalidStructure = new[] { "[]", "null", "{\"source\":\"x\"}", "{\"x\":1}" }.Select(Encoding.UTF8.GetBytes)
            .Append([123, 34, 255, 254, 34, 58, 49, 125]);
        var count = 0;
        foreach (var (bytes, code, message) in invalidJson.Select(b => (b, "InvalidJson", "The request body is not valid JSON. Send an empty JSON object, {}: list sources takes no parameters."))
            .Concat(invalidStructure.Select(b => (b, "InvalidStructure", "Send an empty JSON object, {}: list sources takes no parameters."))))
        {
            using var response = await client.PostAsync("/sources/list", new ByteArrayContent(bytes));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            AssertError(response, text, code);
            using var body = JsonDocument.Parse(text);
            Assert.Equal(message, body.RootElement.GetProperty("message").GetString());
            Assert.Equal(0, calls);
            Assert.Equal(++count, logs.Errors.Count());
            Assert.Equal((code, "$", "request"), (logs.ErrorFields.Last()["Code"], logs.ErrorFields.Last()["Path"], logs.ErrorFields.Last()["Step"]));
            AssertSafe(text + logs.Text);
        }
        foreach (var text in new[] { "{}", " {\r\n} " })
        {
            using var response = await client.PostAsync("/sources/list", new StringContent(text));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(2, calls);
        Assert.Equal(count, logs.Errors.Count());
    }

    [Fact]
    public async Task T13_driver_cancellation_with_live_caller_is_an_internal_error()
    {
        using var settings = new SettingsScope();
        var logs = new RecordedLogs();
        await using var app = Host(logs, Catalog(logs, _ => throw new OperationCanceledException("fake-driver-secret")));
        await Start(app);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        using var response = await client.PostAsync("/sources/list", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        AssertError(response, text, "InternalError");
        using var body = JsonDocument.Parse(text);
        Assert.Equal("DataGateway could not complete the request because of a fault on its side, not in the request. Contact the DataGateway administrator. Tell the user. Do not retry.", body.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("CatalogCancelled", text);
        Assert.Contains("CatalogCancelled", Assert.Single(logs.Errors));
        AssertSafe(text + logs.Text);
    }

    [Theory]
    [InlineData("pre")]
    [InlineData("io")]
    [InlineData("json")]
    [InlineData("cancelled")]
    public async Task T14_body_abort_writes_and_logs_nothing(string kind)
    {
        using var abort = new CancellationTokenSource();
        var logs = new RecordedLogs();
        var calls = 0;
        var reader = Catalog(logs, _ =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<dynamic>>([]);
        });
        var context = new DefaultHttpContext { RequestAborted = abort.Token };
        using var response = new MemoryStream();
        context.Response.Body = response;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        if (kind == "pre") abort.Cancel(); else context.Request.Body = new CancellingStream(abort, kind);
        await PublicGatewayProbe.Invoke(context, "list", reader, logs);
        Assert.Equal(0, response.Length);
        Assert.False(context.Response.HasStarted);
        Assert.Equal(0, calls);
        Assert.Empty(logs.Errors);
        Assert.Empty(logs.Text);
    }

    [Fact]
    public async Task T14_aborted_write_does_not_escape_or_log()
    {
        using var abort = new CancellationTokenSource();
        var logs = new RecordedLogs();
        var context = new DefaultHttpContext { RequestAborted = abort.Token };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        using var response = new AbortedWriteStream(abort);
        context.Response.Body = response;
        await PublicGatewayProbe.Invoke(context, "list", Catalog(logs, _ => Task.FromResult<IReadOnlyList<dynamic>>([])), logs);
        Assert.True(abort.IsCancellationRequested);
        Assert.Equal(0, response.Length);
        Assert.Empty(logs.Errors);
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

    private static HostCatalogState Catalog(RecordedLogs logs, Func<CancellationToken, Task<IReadOnlyList<dynamic>>> read) =>
        HostCatalogState.Raw(read, (name, _) => Task.FromResult<(IReadOnlyList<dynamic>, IReadOnlyList<dynamic>)>((ErrorLogTests.Rows(), [])));

    private static Dictionary<string, object?> Row(string name) => new()
    {
        ["name"] = name, ["title"] = "Synthetic title", ["description"] = "Synthetic description", ["grain"] = "One synthetic row", ["row_cap"] = 500
    };

    private static Exception DriverFailure(string kind) => kind switch
    {
        "pool" => new InvalidOperationException("fake-driver-secret"),
        "io" => new IOException("fake-driver-secret"),
        "disposed" => new ObjectDisposedException("fake-driver-secret"),
        "timeout" => new TimeoutException("fake-driver-secret"),
        "argument" => new ArgumentException("fake-driver-secret"),
        "format" => new FormatException("fake-driver-secret"),
        "cast" => new InvalidCastException("fake-driver-secret"),
        "sql" => (Microsoft.Data.SqlClient.SqlException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(Microsoft.Data.SqlClient.SqlException)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static async Task Start(WebApplication app)
    {
        await app.StartAsync();
        app.Services.GetRequiredService<HostCatalogState>().Arm();
    }

    private static WebApplication Host(RecordedLogs logs, HostCatalogState? catalog = null)
    {
        catalog ??= new HostCatalogState
        {
            List = token => new SqlServerCatalog(new Dapper.Data.DbContext<Microsoft.Data.SqlClient.SqlConnection>(Connection), 1).QueryCatalogAsync(token)
        };
        return Program.CreateApplication(new WebApplicationOptions
        {
            EnvironmentName = "Production", ContentRootPath = TestFiles.InRepo("DataGateway.Web"),
            ApplicationName = typeof(Program).Assembly.GetName().Name, Args = []
        }, builder =>
        {
            SyntheticSignIn.Use(builder);
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { });
            builder.Services.AddSingleton(catalog);
            catalog.Use(builder.Services);
        });
    }

    private static void AssertError(HttpResponseMessage response, string text, string code)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(new[] { "code", "incidentId", "message", "path" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal("$", json.RootElement.GetProperty("path").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("message").GetString()));
    }

    private static void AssertSafe(string text)
    {
        LeakScan.Synthetic(("route catalog", Connection)).Text("route body and logs", text, Rule.Foreign);
        foreach (var marker in new[] { "127.0.0.1,1", "fake-driver-secret" })
            Assert.True(!text.Contains(marker, StringComparison.OrdinalIgnoreCase), "Diagnostic marker found in route output.");
    }

    private sealed class SettingsScope : IDisposable
    {
        private readonly Dictionary<string, string?> original = new();
        private readonly string scratch = TestFiles.InRepo("tests/DataGateway.Web.Tests/obj/list-settings/" + Guid.NewGuid());

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
        private readonly ConcurrentQueue<(LogLevel Level, string Text)> entries = new();
        private readonly ConcurrentQueue<Dictionary<string, object?>> errorFields = new();
        public IEnumerable<Dictionary<string, object?>> ErrorFields => errorFields;
        public IEnumerable<string> Errors => entries.Where(entry => entry.Level >= LogLevel.Warning).Select(entry => entry.Text);
        public IEnumerable<LogLevel> Levels => entries.Where(entry => entry.Level >= LogLevel.Warning).Select(entry => entry.Level);
        public string Text => string.Join("\n", entries.Select(entry => entry.Text));
        public ILogger CreateLogger(string categoryName) => new Recorder(entries, errorFields);
        public void Dispose() { }

        private sealed class Recorder(ConcurrentQueue<(LogLevel Level, string Text)> entries,
            ConcurrentQueue<Dictionary<string, object?>> errorFields) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Assert.Null(exception);
                entries.Enqueue((logLevel, formatter(state, exception)));
                if (logLevel >= LogLevel.Warning)
                    errorFields.Enqueue(Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state)
                        .ToDictionary(pair => pair.Key, pair => pair.Value));
            }
        }
    }
}
