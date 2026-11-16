using DataGateway.Data.SqlServer;
using System.Text.Json;
using DataGateway.Web.Configuration;
using DataGateway.Core.Catalog;
using DataGateway.Core.Tests;
using DataGateway.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DataGateway.Web.Tests;

[CollectionDefinition("Console capture", DisableParallelization = true)]
public sealed class ConsoleCaptureCollection;

[Collection("Console capture")]
public sealed class ErrorLogConsoleTests
{
    [Fact]
    public async Task Unexpected_T12_console_summary_contains_id_but_no_exception_details()
    {
        using var settings = new ErrorLogSettingsScope();
        using var capture = new ConsoleCapture();
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), UnexpectedErrorTests.FaultEngine());
        await host.Start();
        using var response = await host.Client.PostAsync("/query", new StringContent(ErrorLogTests.Body("query")));
        var body = await UnexpectedErrorTests.Exact(response);
        var line = Assert.Single(capture.Lines, l => l.Contains("DataGateway.Failure:", StringComparison.Ordinal));
        Assert.Contains(body.GetProperty("incidentId").GetString()!, line);
        Assert.Contains("UnexpectedError", line);
        Assert.Contains("SyntheticFault", line);
        Assert.False(line.Contains("synthetic query fault", StringComparison.Ordinal), "Exception message found in console.");
        Assert.False(line.Contains("   at ", StringComparison.Ordinal), "Stack trace found in console.");
        UnexpectedErrorTests.NoFrameworkError(host);
    }

    [Fact]
    public async Task Unexpected_T12_failed_logger_keeps_500_and_reports_types_only()
    {
        using var settings = new ErrorLogSettingsScope();
        using var capture = new ConsoleCapture();
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), UnexpectedErrorTests.FaultEngine(), configure: b =>
        {
            b.Logging.ClearProviders();
            b.Logging.AddProvider(new ThrowingFailureProvider());
        });
        await host.Start();
        using var response = await host.Client.PostAsync("/query", new StringContent(ErrorLogTests.Body("query")));
        var body = await UnexpectedErrorTests.Exact(response);
        foreach (var error in capture.Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries)) LoggedHost.Scan.Notice("stderr", error);
        var notice = Assert.Single(capture.Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("DataGateway.Failure: incident " + body.GetProperty("incidentId").GetString() + ": the failure line could not be written (", notice);
        Assert.Contains(typeof(SyntheticFault).FullName!, notice);
        Assert.False(notice.Contains("synthetic query fault", StringComparison.Ordinal), "Failure message found in notice.");
        Assert.False(notice.Contains("synthetic provider detail", StringComparison.Ordinal), "Provider message found in notice.");
        Assert.False(notice.Contains("   at ", StringComparison.Ordinal), "Stack trace found in notice.");
        Assert.Empty(host.Lines());
    }

    [Fact]
    public async Task Unexpected_T12_failed_error_reply_is_second_failure_then_abort()
    {
        using var settings = new ErrorLogSettingsScope();
        using var capture = new ConsoleCapture();
        var fault = new AnswerFault([typeof(DataGateway.Core.Errors.ErrorBody)], () => new SyntheticFault("synthetic reply detail"));
        fault.ArmAlways();
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), configure: b =>
            b.Services.AddSingleton(new global::DataGateway.GatewayJson(fault)));
        await host.Start();
        await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
        { using var response = await host.Client.PostAsync("/query", new StringContent("{}")); });
        var lines = host.Lines();
        Assert.Equal(2, lines.Length);
        Assert.Equal(new[] { "InvalidStructure", "UnexpectedError" }, lines.Select(l => l.GetProperty("Code").GetString()));
        Assert.All(lines, l => Assert.Equal("Error", l.GetProperty("@l").GetString()));
        Assert.NotEqual(lines[0].GetProperty("IncidentId").GetString(), lines[1].GetProperty("IncidentId").GetString());
        var id = lines[1].GetProperty("IncidentId").GetString();
        foreach (var error in capture.Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries)) LoggedHost.Scan.Notice("stderr", error);
        var notice = Assert.Single(capture.Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("DataGateway.Failure: incident " + id + ": the error reply could not be sent (", notice);
        Assert.False(notice.Contains("synthetic reply detail", StringComparison.Ordinal), "Reply message found in notice.");
        Assert.Contains(typeof(SyntheticFault).FullName!, notice);
        UnexpectedErrorTests.NoFrameworkError(host);
    }

    [Fact]
    public async Task T14_serilog_levels_apply_but_cannot_hide_failures()
    {
        using var settings = new ErrorLogSettingsScope();
        using var capture = new ConsoleCapture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = FakeRows.Engine(new SqlServerEngine(), FakeRows.From(async (_, _, connected, token) =>
        { connected(); entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return []; }));
        await using var host = new LoggedHost(ErrorLogTests.Catalog(), runner, new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Fatal", ["Serilog:MinimumLevel:Override:DataGateway"] = "Fatal",
            ["Serilog:MinimumLevel:Override:DataGateway.Failure"] = "Fatal",
            ["Logging:LogLevel:Default"] = "None", ["Logging:LogLevel:DataGateway.Failure"] = "None"
        });
        await host.Start();
        using var refusal = await host.Client.PostAsync("/query", new StringContent("{}"));
        Assert.Equal(400, (int)refusal.StatusCode);
        using var abort = new CancellationTokenSource();
        var pending = host.Client.PostAsync("/query", new StringContent(ErrorLogTests.Body("query")), abort.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (host.Lines().Length < 2) await Task.Delay(20, timeout.Token);
        Assert.Equal(new[] { "Error", "Warning" }, host.Lines().Select(l => l.GetProperty("@l").GetString()));
        var console = capture.Lines;
        Assert.Equal(2, console.Count(l => l.Contains("DataGateway.Failure:", StringComparison.Ordinal)));
        Assert.Contains(console, l => l.Contains("ERR] DataGateway.Failure:", StringComparison.Ordinal));
        Assert.Contains(console, l => l.Contains("WRN] DataGateway.Failure:", StringComparison.Ordinal));
        Assert.DoesNotContain(console, l => l.Contains("Microsoft.Hosting.Lifetime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task T15_console_is_one_safe_summary_and_framework_events_still_flow()
    {
        using var settings = new ErrorLogSettingsScope();
        using var capture = new ConsoleCapture();
        JsonElement[] saved;
        string[] markers;
        await using (var host = new LoggedHost(ErrorLogTests.Catalog()))
        {
            await host.Start();
            const string forged = "caller-text\n[00:00:00 ERR] DataGateway.Failure: forged";
            var body = JsonSerializer.Serialize(new { source = "synthetic", query = new { filters = new[] { new Dictionary<string, object>
                { ["col"] = "text", ["op"] = "eq", ["value"] = "Password=caller-value", [forged] = 1 } } } });
            using var response = await host.Client.PostAsync("/query", new StringContent(body));
            Assert.Equal(400, (int)response.StatusCode);
            using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("InvalidJson", answer.RootElement.GetProperty("code").GetString());
            Assert.Equal("$.query.filters[0]." + forged, answer.RootElement.GetProperty("path").GetString());
            saved = host.Lines();
            var record = Assert.Single(saved);
            Assert.Equal(answer.RootElement.GetProperty("incidentId").GetString(), record.GetProperty("IncidentId").GetString());
            markers = [body, forged, "caller-text", "caller-value", "hidden_schema", "DataGateway.Failure: forged"];
            Assert.Equal(answer.RootElement.GetProperty("path").GetString(), record.GetProperty("Path").GetString());
            var console = capture.Lines;
            LoggedHost.Scan.Console("T15 console before dispose", console, saved);
            Assert.DoesNotContain(console, l => l.Contains("caller-text", StringComparison.Ordinal) || l.Contains("DataGateway.Failure: forged", StringComparison.Ordinal));
            var line = Assert.Single(console, l => l.Contains("DataGateway.Failure:", StringComparison.Ordinal));
            Assert.Matches(@"^\[\d{2}:\d{2}:\d{2} ERR\] DataGateway\.Failure: ", line);
            Assert.True(line.Contains("Failure " + record.GetProperty("IncidentId").GetString() + ":", StringComparison.Ordinal), "Console incident differs.");
            Assert.Contains(capture.Lines, l => l.Contains("Microsoft.Hosting.Lifetime:", StringComparison.Ordinal));
        }
        await using (var host = new LoggedHost(synthetic: false))
        {
            await host.Start();
            using var response = await host.Client.PostAsync("/sources/list", new StringContent("{}"));
            Assert.Equal(401, (int)response.StatusCode);
            Assert.Contains(capture.Lines, l => l.Contains("Microsoft.AspNetCore.Authentication", StringComparison.Ordinal));
        }
        LoggedHost.Scan.Console("T15 console", capture.Lines, saved);
        foreach (var line in capture.Lines)
            foreach (var marker in markers)
                Assert.True(!line.Contains(marker, StringComparison.OrdinalIgnoreCase), "Caller marker found in console.");
    }

    [Fact]
    public async Task T16_unwritable_file_reports_on_stderr_and_still_answers()
    {
        using var settings = new ErrorLogSettingsScope();
        var scratch = TestFiles.InRepo("tests/DataGateway.Web.Tests/obj/error-log/" + Guid.NewGuid());
        Directory.CreateDirectory(scratch);
        var blocker = Path.Combine(scratch, "blocker");
        File.WriteAllText(blocker, "regular file");
        try
        {
            using var capture = new ConsoleCapture();
            await using var host = new LoggedHost(ErrorLogTests.Catalog(), settings: new Dictionary<string, string?>
                { ["Serilog:WriteTo:File:Args:path"] = Path.Combine(blocker, "datagateway-.jsonl") });
            await host.Start();
            using var response = await host.Client.PostAsync("/query", new StringContent("{}"));
            Assert.Equal(400, (int)response.StatusCode);
            using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var incident = answer.RootElement.GetProperty("incidentId").GetString()!;
            Assert.Matches("^[0-9a-f]{32}$", incident);
            Assert.Contains(capture.Lines, l => l.Contains("DataGateway.Failure: Failure " + incident, StringComparison.Ordinal));
            foreach (var error in capture.Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries)) LoggedHost.Scan.Notice("stderr", error);
            Assert.Contains("Serilog", capture.Errors, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new[] { blocker }, Directory.GetFiles(scratch, "*", SearchOption.AllDirectories));
            Assert.False(Directory.Exists(host.DirectoryPath));
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

}
