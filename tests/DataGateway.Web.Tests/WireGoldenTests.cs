using DataGateway.Web.Catalog;
using DataGateway.Data.SqlServer;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DataGateway.Web.Configuration;
using DataGateway.Failures;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Core.Tests;
using DataGateway.Data.Queries;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>Error answers on the wire stay byte for byte what <see cref="WireGolden"/> recorded, on the JSON route and
/// the MCP tool alike, and a source connection the driver cannot parse is refused without quoting it.</summary>
public sealed class WireGoldenTests
{
    /// <summary>Writes every incident id (32 hex digits) as <c>INCIDENT</c>, so bodies compare with the
    /// table.</summary>
    internal static string Normalize(string text) => Regex.Replace(text, "[0-9a-f]{32}", "INCIDENT");

    /// <summary>The SQL Server engine replaces the driver's parse error with its own fixed message and drops the
    /// inner exception.</summary>
    /// <remarks>The driver's message quotes the part it could not parse, which can be a password.</remarks>
    [Fact]
    public void T1_source_parser_refusal_is_rewrapped_without_an_inner_exception()
    {
        const string fragment = "planted-password-fragment";
        var error = Assert.Throws<ArgumentException>(() => new SqlServerEngine().ValidateSourceConnection(
            "Server=host;Password=first;planted-password-fragment;Database=Catalog"));
        Assert.Equal(SqlServerEngine.Unparseable, error.Message);
        Assert.Null(error.InnerException);
        Assert.False(error.ToString().Contains(fragment, StringComparison.OrdinalIgnoreCase), "Password fragment escaped.");
        var serverless = Assert.Throws<ArgumentException>(() => new SqlServerEngine().ValidateSourceConnection("Database=Catalog"));
        Assert.Equal("A source server is required.", serverless.Message);
    }

    /// <summary>Plants each failure in a host, sends the same request on the route and as a tool, and compares status,
    /// body, <c>isError</c> and tool text with the recorded table.</summary>
    /// <remarks>One deliberate change since the recording is stated here instead of in the table: a catalog timeout
    /// and a catalog configuration fault now answer as CatalogUnavailable.</remarks>
    [Fact]
    public async Task T13_unchanged_route_and_tool_wire_bytes()
    {
        using var settings = new ErrorLogSettingsScope();
        var observed = new Dictionary<string, WireGolden.Entry>();
        foreach (var kind in new[] { "InvalidStructure", "UnknownColumn", "UnknownSource", "CatalogUnavailable", "CatalogTimeout",
            "SourceTimeout", "SourceUnavailable", "InvalidCatalog", "CatalogConfiguration", "CatalogCancelled", "SourceCancelled", "UnexpectedError" })
        {
            var body = kind switch
            {
                "InvalidStructure" => "{}",
                "UnknownColumn" => """{"source":"synthetic","query":{"select":["absent"]}}""",
                _ => ErrorLogTests.Body("query")
            };
            var catalog = ErrorLogTests.Catalog(kind == "UnknownSource" ? "unknown" : kind == "InvalidCatalog" ? "stored" : "ok");
            if (kind.StartsWith("Catalog", StringComparison.Ordinal)) catalog.Detail = (_, _) => throw Fault(kind);
            var engine = FakeRows.Engine(new SqlServerEngine(), (_, _, connected, _) =>
            {
                if (kind == "SourceTimeout") connected();
                throw Fault(kind);
            });
            await using var host = new LoggedHost(catalog, engine);
            await host.Start();
            using var response = await host.Client.PostAsync("/query", new StringContent(body));
            observed.Add(kind + "/route", new((int)response.StatusCode, Normalize(await response.Content.ReadAsStringAsync()), null, []));
            await using var client = await McpWire.Connect(host.Client);
            var result = await McpWire.Call(client, "query_source", body);
            observed.Add(kind + "/tool", new((int)response.StatusCode, "", result.IsError,
                Enumerable.Range(0, result.Content.Count).Select(i => Normalize(McpWire.Text(result, i))).ToArray()));
        }
        foreach (var route in new[] { "/query", "/mcp" })
        {
            await using (var host = new LoggedHost(synthetic: false))
            {
                await host.Start();
                using var response = await host.Client.PostAsync(route, new StringContent("{}"));
                observed.Add("NotAuthenticated" + route, new((int)response.StatusCode, Normalize(await response.Content.ReadAsStringAsync()), null, []));
            }
            foreach (var kind in new[] { "InvalidJson", "TooLarge", "Unreadable" })
            {
                await using var host = new LoggedHost(configure: b =>
                {
                    b.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024);
                    if (kind == "Unreadable") b.Services.AddSingleton<IStartupFilter>(new BodyFault(new BadHttpRequestException("synthetic", 400)));
                });
                await host.Start();
                using var request = new HttpRequestMessage(HttpMethod.Post, route)
                { Content = new StringContent(kind == "TooLarge" ? new string('x', 4096) : "{", Encoding.UTF8, "application/json") };
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-11-25");
                using var response = await host.Client.SendAsync(request);
                observed.Add(kind + route, new((int)response.StatusCode, Normalize(await response.Content.ReadAsStringAsync()), null, []));
            }
        }
        Assert.Equal(WireGolden.Cases.Keys, observed.Keys);
        foreach (var (key, baseline) in WireGolden.Cases)
        {
            var expected = key.StartsWith("CatalogTimeout/", StringComparison.Ordinal) || key.StartsWith("CatalogConfiguration/", StringComparison.Ordinal)
                ? WireGolden.Cases["CatalogUnavailable/" + key.Split('/')[1]] : baseline;
            if (key.StartsWith("Catalog", StringComparison.Ordinal) && expected.Status == 503)
                expected = expected with { Body = expected.Body.Replace("\"path\":\"$\"", "\"path\":\"$.source\"", StringComparison.Ordinal),
                    Text = expected.Text.Select(text => text.Replace("\"path\":\"$\"", "\"path\":\"$.source\"", StringComparison.Ordinal)).ToArray() };
            var actual = observed[key];
            Assert.True(expected.Status == actual.Status, key + " status");
            Assert.True(expected.Body == actual.Body, key + " raw body");
            Assert.True(expected.IsError == actual.IsError, key + " isError");
            Assert.True(expected.Text.SequenceEqual(actual.Text), key + " tool text bytes");
        }
    }

    /// <summary>The exception that stands for each case's failure, all carrying the same invented server and user
    /// text that must never reach the answer.</summary>
    private static Exception Fault(string kind) => kind switch
    {
        "CatalogUnavailable" or "SourceUnavailable" => new IOException("driver server wire-host user wire-user detail"),
        "CatalogTimeout" or "SourceTimeout" => new TimeoutException("driver server wire-host user wire-user detail"),
        "CatalogConfiguration" => new ArgumentException("driver server wire-host user wire-user detail"),
        "CatalogCancelled" or "SourceCancelled" => new OperationCanceledException("driver server wire-host user wire-user detail"),
        _ => new SyntheticFault("driver server wire-host user wire-user detail")
    };
}

/// <summary>How DataGateway treats connection strings and their failures: retired connect and row-cap settings are
/// refused, each connection string is used exactly as written and governs its own connect behaviour, and driver text
/// reaches the log without leaking a password into answers.</summary>
/// <remarks>
/// <para>The gateway never builds, edits or adds to a connection string: whoever writes the string decides its
/// timeouts and retries, so there is one place to look and the gateway cannot quietly override it.</para>
/// <para>A retired setting is refused at start, with exit code 2, rather than ignored: an operator who still sets it
/// would otherwise believe it works.</para>
/// </remarks>
public sealed class ConnectionsAndErrorsTests
{
    /// <summary>The start-up refusal of the retired connect timeout setting.</summary>
    private const string Retired = "InvalidConfiguration: DataGateway:Limits:ConnectTimeoutSeconds is no longer read, because each connection string governs its own connect behaviour. Remove it (DataGateway__Limits__ConnectTimeoutSeconds in the environment), and set Connect Timeout and ConnectRetryCount in the connection string if you want values other than the driver's.";

    /// <summary>The retired connect timeout setting is refused whatever its value, empty included, and the limits type
    /// has no such property any more.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("garbage")]
    public void T5_stale_configuration_is_refused_for_every_value(string value)
    {
        using var scope = new ErrorLogSettingsScope();
        using var config = new ConfigurationManager();
        config["DataGateway:Limits:ConnectTimeoutSeconds"] = value;
        var error = Assert.Throws<OptionsValidationException>(() => DataGatewayConfiguration.Configure(config, new ServiceCollection()));
        Assert.Equal(Retired, error.Message);
        Assert.Null(typeof(DataGatewayLimits).GetProperty("ConnectTimeoutSeconds"));
    }

    /// <summary>Set in the environment, the retired connect timeout stops the real executable with exit code 2 and the
    /// refusal on its output.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("5")]
    public async Task T5_stale_environment_and_executable_exit_two(string value)
    {
        using var scope = new ErrorLogSettingsScope();
        const string key = "DataGateway__Limits__ConnectTimeoutSeconds";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, value);
            // Empty environment values are preserved by the child process start info too.
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                WorkingDirectory = TestFiles.InRepo("DataGateway.Web") };
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.Environment[key] = value;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, process.ExitCode);
            var text = await stdout + await stderr;
            Assert.Contains(Retired, text);
            LoggedHost.Scan.Text("stale executable output", text, Rule.DataGateway);
        }
        finally { Environment.SetEnvironmentVariable(key, previous); }
    }

    /// <summary>A key under the retired row cap setting is refused like the setting itself.</summary>
    [Fact]
    public void T20_stale_row_cap_child_section_is_refused()
    {
        using var scope = new ErrorLogSettingsScope();
        using var config = new ConfigurationManager();
        config["DataGateway:Limits:RowCap:Unexpected"] = "x";
        var error = Assert.Throws<OptionsValidationException>(() => DataGatewayConfiguration.Configure(config, new ServiceCollection()));
        Assert.Equal(RetiredRowCap, error.Message);
        Assert.Equal(220, Encoding.UTF8.GetByteCount(error.Message));
    }

    /// <summary>The start-up refusal of the retired global row cap; each source's catalog row_cap replaced it.</summary>
    private const string RetiredRowCap = "InvalidConfiguration: DataGateway:Limits:RowCap is no longer read, because each source's row_cap in the catalog governs its cap. Remove it (DataGateway__Limits__RowCap in the environment), and set row_cap in the catalog.";

    /// <summary>The retired row cap setting is refused whatever its value, valid numbers included.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("garbage")]
    [InlineData("0")]
    [InlineData("2147483646")]
    [InlineData("2147483647")]
    public void T20_stale_row_cap_configuration_is_refused_for_every_value(string value)
    {
        using var scope = new ErrorLogSettingsScope();
        using var config = new ConfigurationManager();
        config["DataGateway:Limits:RowCap"] = value;
        var error = Assert.Throws<OptionsValidationException>(() => DataGatewayConfiguration.Configure(config, new ServiceCollection()));
        Assert.Equal(RetiredRowCap, error.Message);
        Assert.Null(typeof(DataGatewayLimits).GetProperty("RowCap"));
    }

    /// <summary>Set in the environment, the retired row cap stops the real executable with exit code 2, without
    /// echoing the value.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("5")]
    public async Task T20_stale_row_cap_environment_and_executable_exit_two(string value)
    {
        using var scope = new ErrorLogSettingsScope();
        const string key = "DataGateway__Limits__RowCap";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, value);
            // Empty environment values are preserved by the child process start info too.
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                WorkingDirectory = TestFiles.InRepo("DataGateway.Web") };
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.Environment[key] = value;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, process.ExitCode);
            var text = await stdout + await stderr;
            Assert.Contains(RetiredRowCap, text);
            if (value.Length > 0) Assert.DoesNotContain("= " + value, text);
            LoggedHost.Scan.Text("stale executable output", text, Rule.DataGateway);
        }
        finally { Environment.SetEnvironmentVariable(key, previous); }
    }

    /// <summary>The catalog connection from the environment becomes <c>ConnectionStrings:Catalog</c> character for
    /// character, spaces included, for SQL Server and PostgreSQL, and the raw setting is removed from
    /// configuration.</summary>
    [Fact]
    public void T6_catalog_text_is_published_ordinally_unchanged()
    {
        using var scope = new ErrorLogSettingsScope();
        var original = Environment.GetEnvironmentVariable(DataGatewayConfiguration.ConnectionSetting);
        const string input = "  Server = odd-host ; Database = Catalog ; Connect Timeout = 7 ; ConnectRetryCount = 4 ; ";
        try
        {
            foreach (var (connection, postgreSql) in new[]
            {
                (input, false),
                ("  Host = synthetic-host ; Database = Other ; Username = synthetic-reader ; ", true)
            })
            {
                Environment.SetEnvironmentVariable(DataGatewayConfiguration.ConnectionSetting, connection);
                using var config = new ConfigurationManager();
                DataGatewayConfiguration.Configure(config, new ServiceCollection());
                DataGatewayConfiguration.ConfigureCatalog(config, postgreSql);
                Assert.True(string.Equals(connection, config.GetConnectionString("Catalog"), StringComparison.Ordinal), "Catalog text changed.");
                Assert.Null(config[DataGatewayConfiguration.ConnectionSetting]);
                Assert.Equal("Catalog", Assert.Single(config.GetSection("ConnectionStrings").GetChildren()).Key);
            }
        }
        finally { Environment.SetEnvironmentVariable(DataGatewayConfiguration.ConnectionSetting, original); }
    }

    /// <summary>A missing, unparseable, serverless or databaseless catalog connection gets a fixed message that never
    /// quotes the input.</summary>
    [Theory]
    [InlineData(null, "The catalog connection is missing. ")]
    [InlineData("", "The catalog connection is missing. ")]
    [InlineData("  ", "The catalog connection is missing. ")]
    [InlineData("broken-fragment", "The catalog connection cannot be parsed. ")]
    [InlineData("Server=x;Database=Catalog;Unknown=x", "The catalog connection cannot be parsed. ")]
    [InlineData("Server=x;Database=Catalog;Connect Timeout=wrong", "The catalog connection cannot be parsed. ")]
    [InlineData("Database=Catalog", "The catalog connection must name a SQL Server. ")]
    [InlineData("Data Source=\"   \";Database=Catalog", "The catalog connection must name a SQL Server. ")]
    [InlineData("Server=x", "The connection must explicitly name a database. ")]
    public void T6_fixed_catalog_refusals(string? input, string expected)
    {
        Assert.Equal(expected, HostCatalogs.CatalogConnectionError(input, false));
    }

    /// <summary>Against a listener that accepts and never answers, the connect gives up within the string's own
    /// Connect Timeout, 1 or 5 seconds, and fails as SourceUnavailable with the driver's timeout number.</summary>
    /// <remarks>The 5-second case also checks the wait is not cut short, which would mean the gateway overrode the
    /// string.</remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task T7_T8_each_string_governs_silent_listener_timeout(int seconds)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var connection = $"Server=tcp:127.0.0.1,{port};Database=Catalog;User ID=synthetic;Password=synthetic-secret;Encrypt=false;Pooling=false;Connect Timeout={seconds};ConnectRetryCount=0";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            var accept = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            var watch = Stopwatch.StartNew();
            var source = Source(connection);
            var runner = new QueryRunner(new SqlServerEngine());
            var pending = Read();
            async Task<CatalogFailure?> Read() => (await runner.RunAsync(source, Compile(source), timeout.Token)).Failure;
            using var client = await accept.WaitAsync(TimeSpan.FromSeconds(5));
            var failure = Assert.IsType<CatalogFailure>(await pending.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.True(watch.Elapsed.TotalSeconds < (seconds == 1 ? 4 : 15), "String timeout exceeded its bound.");
            if (seconds == 5) Assert.True(watch.Elapsed.TotalSeconds >= 4, "String timeout was overridden.");
            Assert.Equal("SourceUnavailable", failure.Code);
            Assert.Equal(-2, failure.SqlNumber);
            Assert.IsType<SqlException>(failure.DriverException);
        }
        finally { listener.Stop(); }
    }

    /// <summary>Product source never builds or edits connection strings, sets no connect timeout or retry count, uses
    /// the SQL Server driver only in the web host's catalog code, and logs nothing from the data layers; no file
    /// anywhere still names retired gate markers.</summary>
    [Fact]
    public void T9_product_static_guards()
    {
        foreach (var directory in Directory.EnumerateDirectories(TestFiles.RepoRoot, "DataGateway.*").Append(TestFiles.InRepo("DataGateway")))
        foreach (var path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj")))
        {
            var text = File.ReadAllText(path);
            var name = Path.GetFileName(path);
            var folder = Path.GetFileName(directory);
            Assert.DoesNotContain("SqlConnectionStringBuilder", text);
            Assert.DoesNotMatch(@"\b(?:ConnectTimeout|ConnectRetryCount)\s*=", text);
            if (name != "DataGatewayConfiguration.cs") Assert.DoesNotContain("ConnectTimeoutSeconds", text);
            var hostCatalog = folder == "DataGateway.Web" &&
                Path.GetRelativePath(directory, path).Replace('\\', '/').StartsWith("Catalog/", StringComparison.Ordinal);
            if ((folder is "DataGateway.Web" or "DataGateway") && !hostCatalog)
                Assert.DoesNotContain("using Microsoft.Data.SqlClient", text);
            if (name == "FailureLog.cs")
                foreach (var forbidden in new[] { "DataSource", "UserID", "InitialCatalog" }) Assert.DoesNotContain(forbidden, text);
            Assert.DoesNotContain("DbConnectionStringBuilder", text);
            if (folder is "DataGateway.Data" or "DataGateway.Data.SqlServer")
                foreach (var forbidden in new[] { "ILogger", "Microsoft.Extensions.Logging" }) Assert.DoesNotContain(forbidden, text);
        }
        foreach (var root in new[] { TestFiles.RepoRoot, Path.Combine(Path.GetDirectoryName(TestFiles.RepoRoot)!, "acceptance") })
        {
            if (!Directory.Exists(root)) continue; // The published src tree stands alone.
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj")))
            {
                var text = File.ReadAllText(path);
                Assert.False(text.Contains("Connection" + "Text", StringComparison.Ordinal), "Retired gate reference remains.");
                Assert.False(text.Contains("[with" + "held", StringComparison.Ordinal), "Retired withholding marker remains.");
            }
        }
    }

    /// <summary>The leak scan itself: it finds planted passwords of any length, judges driver text more leniently
    /// than gateway text, never prints the value it found, and a real scan refuses a password too short to find
    /// reliably.</summary>
    /// <remarks>Every leak test leans on this scan, so the scan gets its own test.</remarks>
    [Fact]
    public void T10_leak_scan_oracle()
    {
        foreach (var password in new[] { "x", "12", "abc", "long-secret" })
        {
            var scan = LeakScan.Synthetic(("fixture", "Server=plain-host;User ID=plain-user;Password=" + password));
            Assert.Empty(scan.Found("plain-host plain-user server=other", Rule.Driver));
            Assert.NotEmpty(scan.Found("plain-host", Rule.DataGateway));
            foreach (var field in new[] { "DriverMessage", "ExceptionText" })
            {
                var line = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["SourceContext"] = FailureLog.Category, [field] = "quotes " + password });
                var failure = Record.Exception(() => scan.LogLine("driver oracle", line));
                Assert.IsType<Xunit.Sdk.XunitException>(failure);
                Assert.False(failure!.Message.Contains("quotes " + password, StringComparison.Ordinal), "Oracle printed a planted value.");
            }
            Assert.NotEmpty(scan.Found("PWD=unknown", Rule.Driver));
        }
        Assert.IsType<Xunit.Sdk.XunitException>(Record.Exception(() =>
            LeakScan.Real(("fixture", "Server=plain-host;Password=abc"))));
    }

    /// <summary>A catalog row whose connection the driver cannot parse answers InternalError; the log line holds the
    /// engine's fixed refusal instead of the driver's text, and the password fragment the driver quotes appears
    /// nowhere.</summary>
    [Fact]
    public async Task T11_driver_fragment_and_T14_expected_line_are_safe()
    {
        using var scope = new ErrorLogSettingsScope();
        const string fragment = "planted-password-fragment";
        const string connection = "Server=host;Password=first;planted-password-fragment;Database=Catalog";
        var driver = Assert.Throws<ArgumentException>(() => new SqlConnection(connection));
        Assert.True(driver.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase), "Driver did not quote the fragment.");
        var rows = ErrorLogTests.Rows();
        foreach (var row in rows) row["connection_string"] = connection;
        var catalog = HostCatalogState.Raw(_ => Task.FromResult<IReadOnlyList<dynamic>>([CatalogEngineTests.ListRow()]), (_, _) => Task.FromResult<(IReadOnlyList<dynamic>, IReadOnlyList<dynamic>)>((rows, [])));
        await using var host = new LoggedHost(catalog, ErrorLogTests.Engine());
        await host.Start();
        using var response = await host.Client.PostAsync("/query", new StringContent(ErrorLogTests.Body("query")));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(WireGolden.Cases["InvalidCatalog/route"].Body == WireGoldenTests.Normalize(body), "Golden body changed.");
        var line = Assert.Single(host.Lines());
        Assert.Equal(500, (int)response.StatusCode);
        Assert.Equal("InvalidCatalog", line.GetProperty("Code").GetString());
        Assert.Equal("InternalError", line.GetProperty("ReturnedCode").GetString());
        Assert.Equal("ArgumentException", line.GetProperty("ExceptionType").GetString());
        Assert.True(SqlServerEngine.Unparseable == line.GetProperty("DriverMessage").GetString(), "Driver refusal was not rewrapped.");
        Assert.Equal(JsonValueKind.Null, line.GetProperty("ExceptionText").ValueKind);
        Assert.False(line.GetRawText().Contains(fragment, StringComparison.OrdinalIgnoreCase), "Password fragment escaped.");
        foreach (var path in Directory.GetFiles(host.DirectoryPath, "*.jsonl"))
            Assert.False(LoggedHost.ReadText(path).Contains(fragment, StringComparison.OrdinalIgnoreCase), "Password fragment escaped into the log file.");
        Assert.Equal(7319, line.GetProperty("SourceId").GetInt32());
    }

    /// <summary>A known source failure keeps the driver's own exception instance but never prints it; the log line
    /// carries the driver's multi-line message verbatim, no exception text, and the fixed field order.</summary>
    [Fact]
    public async Task T12_same_driver_instance_and_T14_line_order_and_multiline_text()
    {
        using var scope = new ErrorLogSettingsScope();
        var error = new IOException("driver detail on visible-host for visible-user\nsecond line", new Exception("inner driver detail"));
        var source = ErrorLogTests.Contract();
        var runner = FakeRows.Runner(new SqlServerEngine(), (_, _, connected, _) => { connected(); throw error; });
        var failure = (await runner.RunAsync(source, Compile(source), CancellationToken.None)).Failure!;
        Assert.Same(error, failure.DriverException);
        Assert.Equal(source.Id, failure.SourceId);
        Assert.Equal(source.Source.Name, failure.SourceName);
        foreach (var safe in new[] { failure.ToString(), JsonSerializer.Serialize(failure) })
            Assert.False(safe.Contains("inner driver detail", StringComparison.Ordinal) || safe.Contains(error.Message, StringComparison.Ordinal), "Driver text printed by failure.");
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), FakeRows.Engine(new SqlServerEngine(), (_, _, connected, _) => { connected(); throw error; }));
        await host.Start();
        using var response = await host.Client.PostAsync("/query", new StringContent(ErrorLogTests.Body("query")));
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var line = Assert.Single(host.Lines());
        Assert.True(error.Message == line.GetProperty("DriverMessage").GetString(), "Driver text changed.");
        Assert.Equal(JsonValueKind.Null, line.GetProperty("ExceptionText").ValueKind);
        Assert.Equal(body.GetProperty("incidentId").GetString(), line.GetProperty("IncidentId").GetString());
        var projected = new FailureLine("id", "caller", "query", null, failure, null);
        Assert.Equal(ErrorLogTests.Fields.Append("{OriginalFormat}"), projected.Select(p => p.Key));
        Assert.Equal(ErrorLogTests.Fields, line.EnumerateObject().Select(p => p.Name).Where(ErrorLogTests.Fields.Contains));
    }

    /// <summary>An exception whose <c>ToString</c> throws still gets the caller the fixed UnexpectedError body; the
    /// failure line is lost, and one notice goes to standard error instead.</summary>
    [Fact]
    public async Task T15_throwing_ToString_keeps_fixed_body_and_one_notice()
    {
        using var scope = new ErrorLogSettingsScope();
        using var console = new ConsoleCapture();
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), UnexpectedErrorTests.FaultEngine(new TextFault()));
        await host.Start();
        using var response = await host.Client.PostAsync("/query", new StringContent(ErrorLogTests.Body("query")));
        await UnexpectedErrorTests.Exact(response);
        Assert.Single(console.Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Empty(host.Lines());
    }

    /// <summary>A missing, blank or serverless source connection is InvalidCatalog without any connect attempt; a
    /// data source of spaces still goes on to connect.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Database=Catalog", false)]
    [InlineData("Data Source=\"   \"", true)]
    public async Task T18_source_blank_checks_preserve_existing_outcomes(string? text, bool reachesIo)
    {
        var calls = 0;
        var runner = FakeRows.Runner(new SqlServerEngine(), (_, _, _, _) => { calls++; throw new IOException("source refused"); });
        var source = Source(text!, timeout: 1);
        var result = await runner.RunAsync(source, Compile(source), CancellationToken.None);
        Assert.Equal(reachesIo ? "SourceUnavailable" : "InvalidCatalog", result.Failure!.Code);
        Assert.Equal(reachesIo ? 1 : 0, calls);
        Assert.NotNull(result.Failure.DriverException);
        Assert.Equal("connect", result.Failure.Step);
    }

    /// <summary>The catalog's source id appears in failure lines, as null when no catalog row was found, and never
    /// in a route or tool answer.</summary>
    [Fact]
    public async Task T17_source_ID_stays_in_trail_and_out_of_answers()
    {
        using var scope = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), ErrorLogTests.Engine());
        await host.Start();
        await using var client = await McpWire.Connect(host.Client);
        foreach (var operation in new[] { "describe", "query" })
        {
            using var response = await host.Client.PostAsync(ErrorLogTests.Route(operation), new StringContent(ErrorLogTests.Body(operation)));
            var answer = await response.Content.ReadAsStringAsync();
            Assert.Equal(200, (int)response.StatusCode);
            Assert.DoesNotContain("7319", answer);
            var tool = await McpWire.Call(client, operation == "describe" ? "describe_source" : "query_source", ErrorLogTests.Body(operation));
            Assert.DoesNotContain("7319", McpWire.Text(tool));
        }
        var listCatalog = HostCatalogState.Raw(_ => Task.FromResult<IReadOnlyList<dynamic>>(
            [new Dictionary<string, object?> { ["id"] = 7319, ["name"] = "synthetic", ["title"] = "Synthetic", ["description"] = "Synthetic", ["grain"] = "One row", ["row_cap"] = 500 }]), (_, _) => Task.FromResult<(IReadOnlyList<dynamic>, IReadOnlyList<dynamic>)>((ErrorLogTests.Rows(), [])));
        await using var listHost = new LoggedHost(listCatalog);
        await listHost.Start();
        using var listResponse = await listHost.Client.PostAsync("/sources/list", new StringContent("{}"));
        Assert.Equal(200, (int)listResponse.StatusCode);
        Assert.DoesNotContain("7319", await listResponse.Content.ReadAsStringAsync());
        await using var listClient = await McpWire.Connect(listHost.Client);
        var listTool = await McpWire.Call(listClient, "list_sources", "{}");
        Assert.True(listTool.IsError is not true);
        Assert.DoesNotContain("7319", McpWire.Text(listTool));
        using var refusal = await host.Client.PostAsync("/query", new StringContent("""{"source":"synthetic","query":{"select":["absent"]}}"""));
        var line = Assert.Single(host.Lines());
        Assert.Equal(7319, line.GetProperty("SourceId").GetInt32());
        Assert.Equal("synthetic", line.GetProperty("SourceName").GetString());
        await using var missing = new LoggedHost(ErrorLogTests.Catalog("unknown"));
        await missing.Start();
        using var unknown = await missing.Client.PostAsync("/query", new StringContent(ErrorLogTests.Body("query")));
        var noRow = Assert.Single(missing.Lines());
        Assert.Equal(JsonValueKind.Null, noRow.GetProperty("SourceId").ValueKind);
        Assert.Equal("synthetic", noRow.GetProperty("SourceName").GetString());
    }

    /// <summary>The configuration guide names every current log field and no retired setting, and the README states
    /// that connection strings are used as written.</summary>
    [Fact]
    public void T19_documentation_names_current_fields_and_policy()
    {
        var config = File.ReadAllText(TestFiles.InRepo("DataGateway.Web/CONFIGURATION.md"));
        Assert.DoesNotContain("| `DataGateway:Limits:ConnectTimeoutSeconds`", config);
        Assert.DoesNotContain("Exceptions", config);
        foreach (var field in ErrorLogTests.Fields) Assert.Contains("`" + field + "`", config);
        Assert.Contains("DataGateway uses this string exactly as written and sets no connect timeout or retry count.", File.ReadAllText(TestFiles.InRepo("DataGateway.Web/README.md")));
    }

    /// <summary>An exception whose <c>ToString</c> throws.</summary>
    private sealed class TextFault : Exception { public override string ToString() => throw new NotSupportedException("text unavailable"); }
    /// <summary>A one-column test source over <paramref name="text"/>, with source id 7319.</summary>
    private static SourceContract Source(string text, int timeout = 30) => CatalogMapping.Map(new(new DataSourceRow("dbo.synthetic", text)
    { QueryTimeoutSeconds = timeout, Id = 7319, Name = "synthetic", Title = "Synthetic", Description = "Synthetic", Grain = "One row", RowCap = 500, IsActive = true },
    [new("text", 1, "Text", "Synthetic", "text", null, null, null, true, true, true, true, true)])).Value;
    /// <summary>The SQL Server compilation of the empty query on <paramref name="source"/>.</summary>
    private static CompiledQuery Compile(SourceContract source) => new SqlServerEngine().Compile(QueryBuilding.Validate(source, "{}", QueryLimits.ForRowCap(500)).Value);
}
