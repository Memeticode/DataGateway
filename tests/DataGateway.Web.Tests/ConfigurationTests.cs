using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataGateway.Web.Configuration;
using DataGateway.Core.Tests;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using SqlKata.JsonQuery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

// These tests temporarily supply process environment configuration and restore it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DataGateway.Web.Tests;

/// <summary>Pins how the web host reads its configuration at startup: the catalog connection (from the environment
/// variable or user secrets), the query limits, and the refusals for missing or invalid values.</summary>
/// <remarks>
/// <para>Every refusal must name the setting and the fix but never echo the value: a connection string carries a
/// password, so each diagnostic is scanned for the planted fake input (<see cref="AssertSafe"/>).</para>
/// <para>The tests change process environment variables, which is why this assembly turns off test parallelization
/// (the attribute at the top of this file).</para>
/// </remarks>
public sealed class ConfigurationTests
{
    // Keyword order, aliases, flags and trailing delimiter match the supplied real sample.
    // Every credential here is an obvious fake; no database connection is opened.
    /// <summary>An obviously fake catalog connection used as the valid input.</summary>
    private const string FakeConnection =
        "Server=fake-host.invalid;User ID=fake-reader;Password=fake-pass;" +
        "TrustServerCertificate=True;Encrypt=False;Connect Timeout=5;Database=Catalog;";

    /// <summary>The environment variable becomes the single <c>ConnectionStrings:Catalog</c> entry, unchanged, and the
    /// raw variable is not left readable in configuration under its own name.</summary>
    [Fact]
    public async Task Environment_connection_starts_real_host_and_exposes_one_catalog_connection()
    {
        using var settings = new SettingsScope();
        settings.Set(DataGatewayConfiguration.ConnectionSetting, FakeConnection);
        var logs = new RecordedLogs();
        await using var app = CreateApplication(logs);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(timeout.Token);
        var entry = Assert.Single(app.Configuration.GetSection("ConnectionStrings").GetChildren());
        Assert.Equal("Catalog", entry.Key);
        Assert.Null(app.Configuration[DataGatewayConfiguration.ConnectionSetting]);
        Assert.Single(app.Configuration.AsEnumerable(), pair => pair.Value?.Contains("fake-pass") == true);
        var parsed = new SqlConnectionStringBuilder(entry.Value);
        Assert.Equal("Catalog", parsed.InitialCatalog);
        Assert.True(string.Equals(FakeConnection, entry.Value, StringComparison.Ordinal), "Catalog text changed.");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync(Assert.Single(app.Urls), timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("Negotiate", Assert.Single(response.Headers.WwwAuthenticate).ToString());
        Assert.Equal("""
            {"code":"NotAuthenticated","path":"$","message":"Sign in with Windows to call DataGateway: send the request with Windows credentials (HTTP Negotiate)."}
            """, await response.Content.ReadAsStringAsync(timeout.Token));
        await app.StopAsync(timeout.Token);
        AssertSafe(logs.Text);
    }

    [Fact]
    public async Task Project_user_secrets_work_in_production_and_environment_takes_precedence()
    {
        using var settings = new SettingsScope();
        settings.WriteSecrets(FakeConnection);
        await using (var fromSecrets = CreateApplication(new RecordedLogs()))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await fromSecrets.StartAsync(timeout.Token);
            Assert.Equal("Catalog", new SqlConnectionStringBuilder(
                fromSecrets.Configuration.GetConnectionString("Catalog")).InitialCatalog);
            await fromSecrets.StopAsync(timeout.Token);
        }

        settings.Set(DataGatewayConfiguration.ConnectionSetting, FakeConnection.Replace("fake-reader", "fake-env-reader"));
        await using var fromEnvironment = CreateApplication(new RecordedLogs());
        Assert.Equal("fake-env-reader", new SqlConnectionStringBuilder(
            fromEnvironment.Configuration.GetConnectionString("Catalog")).UserID);
    }

    /// <summary>User secrets still resolve on a machine with no <c>APPDATA</c> (Linux and macOS), from the scratch
    /// <c>HOME</c>, never from the real user's profile.</summary>
    [Fact]
    public async Task Project_user_secrets_are_isolated_when_only_HOME_is_available()
    {
        using var settings = new SettingsScope();
        settings.Set("APPDATA", null);
        settings.WriteSecrets(FakeConnection);
        await using var app = CreateApplication(new RecordedLogs());
        Assert.Equal("fake-reader", new SqlConnectionStringBuilder(
            app.Configuration.GetConnectionString("Catalog")).UserID);
    }

    /// <summary>A connection that names a database other than the default is accepted as given, and building the host
    /// opens no connection and binds no port.</summary>
    [Fact]
    public async Task Explicit_other_database_is_accepted_unchanged_without_startup_io()
    {
        using var settings = new SettingsScope();
        var input = BadConnection(2);
        settings.Set(DataGatewayConfiguration.ConnectionSetting, input);
        await using var app = CreateApplication(new RecordedLogs());
        Assert.Empty(app.Urls);
        var entry = Assert.Single(app.Configuration.GetSection("ConnectionStrings").GetChildren());
        Assert.Equal("Catalog", entry.Key);
        Assert.Equal(input, entry.Value, StringComparer.Ordinal);
        Assert.Null(app.Configuration[DataGatewayConfiguration.ConnectionSetting]);
        Assert.Single(app.Configuration.AsEnumerable(), pair => pair.Value?.Contains("fake-pass") == true);
    }

    /// <summary>Each invalid connection fails while the host is built, with the setting name and the user-secrets fix,
    /// and with no inner exception that could carry the parser's view of the input.</summary>
    [Theory]
    [InlineData(0)] // Missing.
    [InlineData(1)] // Malformed syntax.
    [InlineData(3)] // Database omitted.
    [InlineData(4)] // Invalid provider keyword; its text is sensitive too.
    [InlineData(5)] // Invalid provider boolean value.
    [InlineData(6)] // Server omitted.
    [InlineData(7)] // Whitespace.
    public void Invalid_connections_fail_before_start_without_disclosing_input(int scenario)
    {
        using var settings = new SettingsScope();
        var input = BadConnection(scenario);
        settings.Set(DataGatewayConfiguration.ConnectionSetting, input);
        var logs = new RecordedLogs();
        var failure = Assert.Throws<OptionsValidationException>(() => CreateApplication(logs));
        Assert.Contains(DataGatewayConfiguration.ConnectionSetting, failure.Message);
        Assert.Contains("user secrets", failure.Message);
        Assert.Contains("dotnet user-secrets set", failure.Message);
        Assert.Null(failure.InnerException);
        AssertSafe(failure + logs.Text, input);
    }

    /// <summary>The real executable, started as a child process, exits with code 2 and a sanitized message when the
    /// connection is missing or invalid.</summary>
    /// <remarks>Runs the built program rather than <c>CreateApplication</c> so the top-level error handling and exit
    /// code are covered too. The process is killed if it hangs.</remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Executable_exits_with_fix_the_call_and_sanitized_diagnostics(int scenario)
    {
        using var settings = new SettingsScope();
        var input = BadConnection(scenario);
        settings.Set(DataGatewayConfiguration.ConnectionSetting, input);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = TestFiles.InRepo("DataGateway.Web"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("DataGateway.Web configuration refusal subprocess hung.");
        }

        var output = await stdout + await stderr;
        Assert.Equal(2, process.ExitCode);
        Assert.Contains(DataGatewayConfiguration.ConnectionSetting, output);
        Assert.Contains("user secrets", output);
        AssertSafe(output, input);
    }

    /// <summary>With no limit settings, the shared query limits are the library defaults with no maximum row limit, and
    /// the removed timeout and row-cap settings no longer exist.</summary>
    [Fact]
    public async Task Limits_have_required_defaults()
    {
        using var settings = new SettingsScope();
        settings.Set(DataGatewayConfiguration.ConnectionSetting, FakeConnection);
        await using var app = CreateApplication(new RecordedLogs());
        var limits = app.Services.GetRequiredService<IOptions<DataGatewayLimits>>().Value;
        Assert.Null(typeof(DataGatewayLimits).GetProperty("CommandTimeoutSeconds"));
        
        Assert.Null(typeof(DataGatewayLimits).GetProperty("RowCap"));
        Assert.Equal(JsonQueryLimits.Default with { MaxLimit = null }, app.Services.GetRequiredService<JsonQueryLimits>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_limit_can_be_overridden_in_configuration_or_environment(bool environment)
    {
        using var settings = new SettingsScope();
        settings.Set(DataGatewayConfiguration.ConnectionSetting,
            FakeConnection.Replace("Connect Timeout=5", "Connect Timeout=99"));
        var overrides = new Dictionary<string, string?>
        {
            ["DataGateway:Limits:MaxJsonLength"] = "2048",
            ["DataGateway:Limits:MaxFilterDepth"] = "3",
            ["DataGateway:Limits:MaxFilterConditions"] = "11",
            ["DataGateway:Limits:MaxInValues"] = "13"
        };
        if (environment)
        {
            foreach (var pair in overrides)
            {
                settings.Set(pair.Key.Replace(":", "__"), pair.Value);
            }
        }

        await using var app = CreateApplication(new RecordedLogs(), builder =>
        {
            if (!environment)
            {
                builder.Configuration.AddInMemoryCollection(overrides);
            }
        });
        var limits = app.Services.GetRequiredService<IOptions<DataGatewayLimits>>().Value;
        Assert.Null(typeof(DataGatewayLimits).GetProperty("CommandTimeoutSeconds"));
        
        var shared = app.Services.GetRequiredService<JsonQueryLimits>();
        Assert.Equal(JsonQueryLimits.Default with { MaxLimit = null, MaxJsonLength = 2048, MaxFilterDepth = 3,
            MaxFilterConditions = 11, MaxInValues = 13 }, shared);
        Assert.Same(shared, app.Services.GetRequiredService<JsonQueryLimits>());
        Assert.True(string.Equals(Environment.GetEnvironmentVariable(DataGatewayConfiguration.ConnectionSetting),
            app.Configuration.GetConnectionString("Catalog"), StringComparison.Ordinal), "Catalog text changed.");
    }

    /// <summary>A limit that is not an integer in range is refused with one exact message naming the setting in both its
    /// configuration and environment-variable forms.</summary>
    [Theory]
    [InlineData("MaxJsonLength", "")]
    [InlineData("MaxJsonLength", "   ")]
    [InlineData("MaxJsonLength", "abc")]
    [InlineData("MaxJsonLength", "1.5")]
    [InlineData("MaxJsonLength", "1e3")]
    [InlineData("MaxJsonLength", "-1")]
    [InlineData("MaxJsonLength", "2147483648")]
    [InlineData("MaxFilterDepth", "")]
    [InlineData("MaxFilterDepth", "   ")]
    [InlineData("MaxFilterDepth", "abc")]
    [InlineData("MaxFilterDepth", "1.5")]
    [InlineData("MaxFilterDepth", "1e3")]
    [InlineData("MaxFilterDepth", "-1")]
    [InlineData("MaxFilterDepth", "2147483648")]
    [InlineData("MaxFilterConditions", "")]
    [InlineData("MaxFilterConditions", "   ")]
    [InlineData("MaxFilterConditions", "abc")]
    [InlineData("MaxFilterConditions", "1.5")]
    [InlineData("MaxFilterConditions", "1e3")]
    [InlineData("MaxFilterConditions", "-1")]
    [InlineData("MaxFilterConditions", "2147483648")]
    [InlineData("MaxInValues", "")]
    [InlineData("MaxInValues", "   ")]
    [InlineData("MaxInValues", "abc")]
    [InlineData("MaxInValues", "1.5")]
    [InlineData("MaxInValues", "1e3")]
    [InlineData("MaxInValues", "-1")]
    [InlineData("MaxInValues", "2147483648")]
    [InlineData("MaxJsonLength", "0")]
    [InlineData("MaxFilterConditions", "0")]
    [InlineData("MaxInValues", "0")]
    [InlineData("MaxFilterDepth", "31")]
    public void Invalid_limits_are_refused_with_setting_name(string name, string value)
    {
        using var settings = new SettingsScope();
        settings.Set(DataGatewayConfiguration.ConnectionSetting, FakeConnection);
        var logs = new RecordedLogs();
        var key = $"DataGateway:Limits:{name}";
        var failure = Assert.Throws<OptionsValidationException>(() => CreateApplication(logs,
            builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
        Assert.Contains(key, failure.Message);
        var minimum = name == nameof(JsonQueryLimits.MaxFilterDepth) ? 0 : 1;
        var maximum = name == nameof(JsonQueryLimits.MaxFilterDepth) ? JsonQueryLimits.None.MaxFilterDepth
            : int.MaxValue;
        Assert.Contains($"from {minimum} to {maximum}", failure.Message);
        Assert.Contains(key.Replace(":", "__"), failure.Message);
        Assert.Equal($"InvalidConfiguration: {key} must be an integer from {minimum} to {maximum}. " +
            $"Set {key} in configuration or {key.Replace(":", "__")} in the environment.", failure.Message);
        AssertSafe(failure + logs.Text);
    }

    /// <summary>The smallest and largest allowed value of each limit is accepted.</summary>
    [Theory]
    [InlineData("MaxFilterDepth", 0)]
    [InlineData("MaxFilterDepth", 30)]
    [InlineData("MaxJsonLength", 1)]
    [InlineData("MaxFilterConditions", 1)]
    [InlineData("MaxInValues", 1)]
    public async Task T13_shared_limits_accept_configuration_boundaries(string name, int value)
    {
        using var settings = new SettingsScope();
        settings.Set(DataGatewayConfiguration.ConnectionSetting, FakeConnection);
        await using var app = CreateApplication(new RecordedLogs(), builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"DataGateway:Limits:{name}"] = value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }));
        var limits = app.Services.GetRequiredService<JsonQueryLimits>();
        Assert.Same(limits, app.Services.GetRequiredService<JsonQueryLimits>());
        var expected = JsonQueryLimits.Default with { MaxLimit = null };
        expected = name switch
        {
            "MaxFilterDepth" => expected with { MaxFilterDepth = value },
            "MaxJsonLength" => expected with { MaxJsonLength = value },
            "MaxFilterConditions" => expected with { MaxFilterConditions = value },
            "MaxInValues" => expected with { MaxInValues = value },
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
        Assert.Equal(expected, limits);
    }

    /// <summary>A host that registers its own query limits before the gateway keeps them; configuration does not replace them.</summary>
    [Fact]
    public async Task Earlier_shared_limits_registration_wins()
    {
        using var settings = new SettingsScope();
        settings.Set(DataGatewayConfiguration.ConnectionSetting, FakeConnection);
        var earlier = QueryLimits.ForRowCap(17);
        await using var app = CreateApplication(new RecordedLogs(), builder => builder.Services.AddSingleton(earlier));
        Assert.Same(earlier, app.Services.GetRequiredService<JsonQueryLimits>());
    }

    /// <summary>The connection is accepted only from the environment or user secrets, not from any other configuration
    /// source such as an in-memory or JSON setting.</summary>
    [Fact]
    public void Connection_supplied_only_as_generic_configuration_is_refused()
    {
        using var settings = new SettingsScope();
        var failure = Assert.Throws<OptionsValidationException>(() => CreateApplication(new RecordedLogs(),
            builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DataGatewayConfiguration.ConnectionSetting] = FakeConnection
            })));
        Assert.Contains(DataGatewayConfiguration.ConnectionSetting, failure.Message);
        AssertSafe(failure.ToString());
    }

    /// <summary>The host accepts exactly one connection; an extra <c>ConnectionStrings</c> entry is refused without
    /// naming its key, which could itself be sensitive.</summary>
    [Fact]
    public void Second_connection_entry_is_refused_without_disclosing_key_or_value()
    {
        using var settings = new SettingsScope();
        settings.Set(DataGatewayConfiguration.ConnectionSetting, FakeConnection);
        var failure = Assert.Throws<OptionsValidationException>(() => CreateApplication(new RecordedLogs(),
            builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:fake-private-key"] = FakeConnection
            })));
        Assert.Contains("ConnectionStrings", failure.Message);
        Assert.DoesNotContain("fake-private-key", failure.ToString());
        AssertSafe(failure.ToString());
    }

    /// <summary>No file in the repository, except C# test code, contains text shaped like a connection string.</summary>
    /// <remarks>Guards against a real connection string reaching a public file. Test code is exempt because it needs
    /// fake connection strings; the one SQL script line that uses a sqlcmd placeholder is allowed by name.</remarks>
    [Fact]
    public void Public_sources_have_no_connection_literals_outside_synthetic_test_code()
    {
        var pattern = LeakScan.KeywordForm;
        var violations = Directory.EnumerateFiles(TestFiles.RepoRoot, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(TestFiles.RepoRoot, path).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or "logs" or ".git"))
            .Where(path => !(path.StartsWith(TestFiles.InRepo("tests") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && Path.GetExtension(path) == ".cs"))
            .Where(path => File.ReadLines(path).Any(line => pattern.IsMatch(line) &&
                // Existing SQL login DDL uses a sqlcmd placeholder, not a connection string.
                !(Path.GetRelativePath(TestFiles.RepoRoot, path).Replace('\\', '/') == "db/gateway-reader.sql" &&
                  line.Trim() == "CREATE LOGIN [gateway_reader] WITH PASSWORD = N'$(ReaderPassword)', " +
                      "DEFAULT_DATABASE = [master], CHECK_EXPIRATION = OFF, CHECK_POLICY = ON")))
            .Select(path => Path.GetRelativePath(TestFiles.RepoRoot, path)).ToArray();
        Assert.True(violations.Length == 0, "Connection literals in: " + string.Join(", ", violations));
    }

    /// <summary>Builds the real web host in Production with recorded logs, a random local port and the synthetic catalog.</summary>
    /// <param name="logs">Receives every log message.</param>
    /// <param name="configure">Further changes to the builder, applied last.</param>
    /// <returns>The built, not yet started, application.</returns>
    private static WebApplication CreateApplication(RecordedLogs logs,
        Action<WebApplicationBuilder>? configure = null) =>
        Program.CreateApplication(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ContentRootPath = TestFiles.InRepo("DataGateway.Web"),
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            Args = []
        }, builder =>
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
            new HostCatalogState().Use(builder.Services);
            configure?.Invoke(builder);
        });

    /// <summary>The connection input for a numbered scenario; the scenarios are named at the tests' InlineData.</summary>
    /// <remarks>Each invalid input carries a <c>fake-private</c> or <c>broken-fake</c> marker, so <see cref="AssertSafe"/>
    /// can prove the marker never reaches a diagnostic.</remarks>
    private static string? BadConnection(int scenario) => scenario switch
    {
        0 => null,
        1 => FakeConnection + "broken-fake-password",
        2 => FakeConnection.Replace("Database=Catalog", "Database=fake-other-db"),
        3 => FakeConnection.Replace("Database=Catalog;", ""),
        4 => FakeConnection + "fake-private-key=fake-private-value;",
        5 => FakeConnection.Replace("Encrypt=False", "Encrypt=fake-private-value"),
        6 => FakeConnection.Replace("Server=fake-host.invalid;", ""),
        7 => "   ",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    /// <summary>Fails if a diagnostic contains the fake connection, the refused input, or any of the planted markers.</summary>
    /// <param name="output">The diagnostic text: exception, logs or process output.</param>
    /// <param name="input">The refused input, when the test supplied one.</param>
    private static void AssertSafe(string output, string? input = null)
    {
        LeakScan.Synthetic(("fake connection", FakeConnection), ("refused input", input)).Text("configuration diagnostic", output, Rule.DataGateway);
        foreach (var marker in new[] { "fake-private-key", "fake-private-value", "broken-fake-password", "fake-other-db" })
            Assert.True(!output.Contains(marker, StringComparison.OrdinalIgnoreCase), "Diagnostic marker found in configuration output.");
        if (!string.IsNullOrWhiteSpace(input))
            Assert.True(!output.Contains(input, StringComparison.Ordinal), "The input escaped into diagnostics.");
    }

    /// <summary>Points the process environment at a fresh scratch folder for one test and restores every variable it
    /// changed on dispose.</summary>
    /// <remarks>Redirecting <c>APPDATA</c>, <c>HOME</c> and the user-secrets fallback keeps the tests away from the
    /// developer's real user secrets, and clearing the connection and limit variables keeps a developer's own settings
    /// from changing the results.</remarks>
    private sealed class SettingsScope : IDisposable
    {
        /// <summary>Each changed variable's value before the scope first changed it.</summary>
        private readonly Dictionary<string, string?> original = new();
        /// <summary>The scratch folder, under the test project's <c>obj</c> folder.</summary>
        private readonly string scratch = TestFiles.InRepo("tests/DataGateway.Web.Tests/obj/settings/" + Guid.NewGuid());

        /// <summary>Creates the scratch folder and clears the settings the host reads.</summary>
        public SettingsScope()
        {
            Directory.CreateDirectory(scratch);
            Set("APPDATA", scratch);
            Set("HOME", scratch);
            Set("DOTNET_USER_SECRETS_FALLBACK_DIR", scratch);
            Set(DataGatewayConfiguration.ConnectionSetting, null);
            foreach (var name in new[] { "CommandTimeoutSeconds", "ConnectTimeoutSeconds", "RowCap", "MaxJsonLength", "MaxFilterDepth", "MaxFilterConditions", "MaxInValues" })
            {
                Set("DataGateway__Limits__" + name, null);
            }
        }

        /// <summary>Sets one environment variable for the scope, remembering its original value the first time.</summary>
        /// <param name="name">The variable name.</param>
        /// <param name="value">The new value, or <see langword="null"/> to remove it.</param>
        public void Set(string name, string? value)
        {
            original.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }

        /// <summary>Writes a user-secrets file holding <paramref name="connection"/> at both the Windows and the Unix
        /// user-secrets location under the scratch folder.</summary>
        /// <param name="connection">The connection to store.</param>
        public void WriteSecrets(string connection)
        {
            foreach (var relative in new[] { "Microsoft/UserSecrets", ".microsoft/usersecrets" })
            {
                var path = Path.Combine(scratch, relative, "DataGateway-Web/secrets.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    [DataGatewayConfiguration.ConnectionSetting] = connection
                }));
            }
        }

        /// <summary>Restores every changed variable and deletes the scratch folder.</summary>
        public void Dispose()
        {
            foreach (var pair in original)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }

            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>A logger provider that keeps every message, with any exception appended, for leak checks.</summary>
    private sealed class RecordedLogs : ILoggerProvider
    {
        /// <summary>The recorded messages, in arrival order.</summary>
        private readonly ConcurrentQueue<string> messages = new();
        /// <summary>All recorded messages, one per line.</summary>
        public string Text => string.Join("\n", messages);
        /// <summary>Returns a logger that records into this provider.</summary>
        public ILogger CreateLogger(string categoryName) => new Recorder(messages);
        /// <summary>Nothing to release.</summary>
        public void Dispose() { }

        /// <summary>A logger that records every message at every level.</summary>
        private sealed class Recorder(ConcurrentQueue<string> messages) : ILogger
        {
            /// <summary>Scopes are not recorded.</summary>
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            /// <summary>Every level is recorded.</summary>
            public bool IsEnabled(LogLevel logLevel) => true;
            /// <summary>Records the formatted message followed by the exception's text.</summary>
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception) + exception);
        }
    }
}
