using DataGateway.Failures;
using System.Text.Json;
using DataGateway.Web.Configuration;
using DataGateway.Core.Catalog;
using DataGateway.Core.Tests;
using DataGateway.Data.Catalog;
using DataGateway.Data.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>The real DataGateway.Web host, started on a loopback port with its file log redirected to a scratch
/// directory, so tests can read the JSON log lines the host actually writes.</summary>
/// <remarks>Every line read back is scanned with <see cref="Scan"/>, so any log test also fails when a connection
/// part reaches the log. Only lines in the failure category are returned; a line from any other DataGateway
/// category fails the read.</remarks>
internal sealed class LoggedHost : IAsyncDisposable
{
    /// <summary>The leak scan for every log line: the catalog connection, a source connection and a malformed stored
    /// connection the tests plant.</summary>
    internal static readonly LeakScan Scan = LeakScan.Synthetic(("log catalog", ErrorLogSettingsScope.Catalog),
        ("log source", ErrorLogTests.SourceConnection), ("stored row", "malformed-secret-connection"));
    /// <summary>The scratch directory the file log writes to, under the test project's <c>obj</c> folder.</summary>
    internal string DirectoryPath { get; } = TestFiles.InRepo("tests/DataGateway.Web.Tests/obj/error-log/" + Guid.NewGuid());
    /// <summary>The test catalog the host runs on.</summary>
    private readonly HostCatalogState? catalog;
    /// <summary>The built host.</summary>
    internal WebApplication App { get; }
    /// <summary>A client bound to the listening address; replaced by <see cref="Start"/>.</summary>
    internal HttpClient Client { get; private set; } = new();

    /// <summary>Builds the host through the real startup code without starting it.</summary>
    /// <param name="catalog">The test catalog; a fresh healthy one when null.</param>
    /// <param name="engine">Replaces the database engine when given.</param>
    /// <param name="settings">Extra in-memory settings, applied after the log path.</param>
    /// <param name="synthetic">Signs every caller in with the synthetic scheme instead of Windows sign-in.</param>
    /// <param name="configure">Changes the builder last.</param>
    /// <param name="environment">The host environment name.</param>
    internal LoggedHost(HostCatalogState? catalog = null, DataGateway.Data.IDatabaseEngine? engine = null,
        IDictionary<string, string?>? settings = null, bool synthetic = true,
        Action<WebApplicationBuilder>? configure = null, string environment = "Production")
    {
        this.catalog = catalog ?? new HostCatalogState();
        App = Program.CreateApplication(new WebApplicationOptions
        {
            EnvironmentName = environment, ContentRootPath = TestFiles.InRepo("DataGateway.Web"),
            ApplicationName = typeof(Program).Assembly.GetName().Name, Args = []
        }, builder =>
        {
            if (synthetic) SyntheticSignIn.Use(builder);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:WriteTo:File:Args:path"] = Path.Combine(DirectoryPath, "datagateway-.jsonl"),
                ["DataGateway:Mcp:Title"] = "", ["DataGateway:Mcp:About"] = ""
            });
            if (settings is not null) builder.Configuration.AddInMemoryCollection(settings);
            builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
            this.catalog?.Use(builder.Services);
            if (engine is not null) builder.Services.AddSingleton(engine);
            configure?.Invoke(builder);
        });
    }

    /// <summary>Starts the host, arms the catalog and points <see cref="Client"/> at the bound address.</summary>
    /// <remarks>Fails first when the scratch directory already holds files, so a test never reads another run's
    /// lines.</remarks>
    internal async Task Start()
    {
        Assert.True(!Directory.Exists(DirectoryPath) || !Directory.EnumerateFileSystemEntries(DirectoryPath).Any(), "Host scratch directory is not empty.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await App.StartAsync(timeout.Token);
        catalog?.Arm();
        Client.Dispose();
        Client = new() { BaseAddress = new Uri(Assert.Single(App.Urls)), Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>The failure lines this host has written so far, each scanned.</summary>
    internal JsonElement[] Lines() => ReadLines(DirectoryPath);
    /// <summary>Reads every failure line from the log files in a directory in file order, scanning each line.</summary>
    /// <remarks>Opens the files with shared read, write and delete access, because the host's log sink still holds
    /// them open.</remarks>
    internal static JsonElement[] ReadLines(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        var result = new List<JsonElement>();
        foreach (var path in Directory.GetFiles(directory, "*.jsonl").Order(StringComparer.Ordinal))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } text)
            {
                if (text.Length == 0) continue;
                using var json = JsonDocument.Parse(text);
                Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
                var line = json.RootElement.Clone();
                Scan.LogLine("synthetic file line", line);
                if (line.GetProperty("SourceContext").GetString() == FailureLog.Category) result.Add(line);
                else Assert.False(line.GetProperty("SourceContext").GetString()!.StartsWith("DataGateway", StringComparison.Ordinal));
            }
        }
        return result.ToArray();
    }

    /// <summary>Every log line this host has written, of any category, each scanned.</summary>
    internal JsonElement[] AllLines() => Directory.Exists(DirectoryPath)
        ? Directory.GetFiles(DirectoryPath, "*.jsonl").Order(StringComparer.Ordinal)
            .SelectMany(path => ReadText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Select(text => { using var json = JsonDocument.Parse(text); Scan.LogLine("synthetic file line", json.RootElement); return json.RootElement.Clone(); }).ToArray()
        : [];

    /// <summary>Reads a log file the host still holds open.</summary>
    internal static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Stops the host and deletes its scratch directory.</summary>
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}

/// <summary>Sets the process environment a logged host reads at startup, and restores it on dispose: a fake catalog
/// connection, cleared limit overrides, and user-profile folders pointed at an empty scratch folder.</summary>
/// <remarks>Pointing the profile folders away keeps the machine's own user secrets out of the test host.</remarks>
internal sealed class ErrorLogSettingsScope : IDisposable
{
    /// <summary>The fake catalog connection the scope sets; its values are planted so the leak scan can find them.</summary>
    internal const string Catalog = "Server=fake-log-catalog-host;Database=Catalog;User ID=fake-log-reader;Password=fake-log-password;Encrypt=false;Connect Timeout=1;ConnectRetryCount=0";
    /// <summary>The value each changed variable had before, for restoring.</summary>
    private readonly Dictionary<string, string?> original = new();
    /// <summary>The empty folder the profile variables point to.</summary>
    private readonly string scratch = TestFiles.InRepo("tests/DataGateway.Web.Tests/obj/error-log-settings/" + Guid.NewGuid());
    /// <summary>Applies the settings.</summary>
    internal ErrorLogSettingsScope()
    {
        Directory.CreateDirectory(scratch);
        foreach (var name in new[] { "APPDATA", "HOME", "DOTNET_USER_SECRETS_FALLBACK_DIR" }) Set(name, scratch);
        Set(DataGatewayConfiguration.ConnectionSetting, Catalog);
        foreach (var name in new[] { "ConnectTimeoutSeconds", "CommandTimeoutSeconds", "RowCap", "MaxJsonLength", "MaxFilterDepth", "MaxFilterConditions", "MaxInValues" }) Set("DataGateway__Limits__" + name, null);
    }
    /// <summary>Saves a variable's value, then sets it; null removes it.</summary>
    private void Set(string name, string? value)
    {
        original.Add(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }
    /// <summary>Restores every variable and deletes the scratch folder.</summary>
    public void Dispose()
    {
        foreach (var (name, value) in original) Environment.SetEnvironmentVariable(name, value);
        Directory.Delete(scratch, recursive: true);
    }
}
