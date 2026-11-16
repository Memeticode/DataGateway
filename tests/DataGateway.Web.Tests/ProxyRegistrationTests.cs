extern alias RegistrationProxy;
using RegistrationProxy::DataGateway.Proxy;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataGateway.Core.Tests;
using ModelContextProtocol.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class ProxyRegistrationTests
{
    private const string Url = "HTTP://example.invalid:80/a/../mcp?X=%2f";
    private const string Footer = "Start Claude Desktop to load it.";
    private const string Tray = "refused: quit Claude Desktop from the tray icon (not just the window), then run the command again";
    private const string Gateway = "refused: gateway could not be reached or its tools could not be listed; check the URL and your Windows access";
    private const string Policy = "refused: your organization's policy disables local MCP servers in Claude Desktop; ask your administrator";
    private static readonly string[] Usage =
    [
        "Usage: DataGateway.Proxy --register <URL of the server's /mcp endpoint> --name <server key>",
        "Usage: DataGateway.Proxy --unregister --name <server key>"
    ];
    private static string Q(string value) => JsonSerializer.Serialize(value);

    [Fact]
    public async Task T1_merge_preserves_raw_value_tokens_and_exact_original_backup()
    {
        using var f = new Fixture();
        const string other = """{"command":"other-app","env": { "TOKEN":"SYNTHETIC_ONLY" },"n":1.2300e+02,"s":"\u0061"}""";
        const string unknown = """{ "enabled":true, "values" : [1, 2, null] }""";
        var before = f.Seed(f.Classic, "{\"unknown\":" + unknown + ",\"preferences\":[ 1, 2 ],\"mcpServers\":{\"other\":" + other + "}}");
        Assert.Equal(0, await f.Run());
        using var old = JsonDocument.Parse(before);
        using var after = JsonDocument.Parse(File.ReadAllBytes(f.Classic));
        foreach (var key in new[] { "unknown", "preferences" })
            RawEqual(old.RootElement.GetProperty(key), after.RootElement.GetProperty(key));
        RawEqual(old.RootElement.GetProperty("mcpServers").GetProperty("other"), after.RootElement.GetProperty("mcpServers").GetProperty("other"));
        AssertEntry(f, f.Classic, "data", Url);
        Assert.Equal(before, File.ReadAllBytes(Assert.Single(f.Backups())));
        Assert.DoesNotContain("SYNTHETIC_ONLY", f.Out.ToString());
        Assert.Empty(f.Error.ToString());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("{\"x\":1,\"x\":2}")]
    [InlineData("{\"x\":[{\"n\":1,\"n\":2}]}")]
    [InlineData("{\"x\":1,}")]
    [InlineData("{/*comment*/}")]
    [InlineData("invalid-utf8")]
    [InlineData("read-denied")]
    public async Task T2_bad_files_are_local_refusals_and_repair_allows_retry(string text)
    {
        using var f = new Fixture();
        var bad = f.Seed(f.Classic, text);
        if (text == "invalid-utf8") { bad = [0xff, 0xfe, 0x80]; File.WriteAllBytes(f.Classic, bad); }
        var good = f.Config("Local", "Claude-3p");
        var originalGood = f.Seed(good, "{}");
        if (text == "read-denied") f.Files.Before = (op, path, _) => { if (op == "read" && path == f.Classic) throw new UnauthorizedAccessException(); };
        Assert.Equal(1, await f.Run());
        Assert.Equal(bad, File.ReadAllBytes(f.Classic));
        AssertEntry(f, good, "data", Url);
        Assert.Equal(originalGood, File.ReadAllBytes(Assert.Single(f.Backups())));
        Assert.Empty(f.Temps());
        Assert.DoesNotContain(f.Classic + ".", f.Backups());
        f.Files.Before = null;
        f.Seed(f.Classic, "{}");
        Assert.Equal(0, await f.Run());
        AssertEntry(f, f.Classic, "data", Url);
        Assert.Contains("skipped: already registered", f.Out.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T3_running_desktop_blocks_both_verbs_before_listing_or_files(bool register)
    {
        using var f = new Fixture();
        f.Seed(f.Classic, f.Entry());
        f.Seed(f.Config("Local", "Claude-3p"), f.Entry());
        var snapshot = f.Snapshot();
        f.Context = f.Context with { ReadProcesses = () => { f.ProcessCalls++; return [f.Desktop]; } };
        Assert.Equal(1, await f.Run(register));
        Assert.Equal(new[] { Tray, Footer }, f.Lines);
        f.Unchanged(snapshot);
        Assert.Equal(0, f.ListCalls);
        Assert.Empty(f.Files.Calls);
        f.Context = f.Context with { ReadProcesses = () => [] };
        Assert.Equal(0, await f.Run(register));
    }

    [Fact]
    public async Task T3_desktop_starting_during_preflight_blocks_even_fallback_creation()
    {
        using var f = new Fixture(false);
        var snapshot = f.Snapshot();
        f.Context = f.Context with { ReadProcesses = () => ++f.ProcessCalls == 1 ? [] : [f.Desktop] };
        Assert.Equal(1, await f.Run());
        Assert.Equal(new[] { Q(f.Classic) + ": " + Tray, Footer }, f.Lines);
        Assert.Equal(1, f.ListCalls);
        Assert.Contains(f.Files.Calls, call => call.Op == "attributes");
        Assert.Equal(0, f.Files.Mutations);
        Assert.False(Directory.Exists(Path.GetDirectoryName(f.Classic)));
        f.Unchanged(snapshot);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T3_precommit_recheck_protects_all_existing_files_on_both_verbs(bool register)
    {
        using var f = new Fixture();
        f.Seed(f.Classic, f.Entry());
        f.Seed(f.Config("Local", "Claude-3p"), f.Entry());
        var snapshot = f.Snapshot();
        f.Context = f.Context with { ReadProcesses = () => ++f.ProcessCalls == 1 ? [] : [f.Desktop] };
        Assert.Equal(1, await f.Run(register));
        Assert.Equal(new[]
        {
            Q(f.Config("Local", "Claude-3p")) + ": " + Tray,
            Q(f.Classic) + ": " + Tray,
            Footer
        }, f.Lines);
        f.Unchanged(snapshot); Assert.Equal(0, f.Files.Mutations);
        Assert.Equal(register ? 1 : 0, f.ListCalls);
    }

    [Theory]
    [InlineData(15, 26, true)]
    [InlineData(15, 27, false)]
    [InlineData(40, 1, true)]
    [InlineData(40, 2, false)]
    [InlineData(41, 1, false)]
    [InlineData(42, 1, false)]
    [InlineData(0, 41, true)]
    [InlineData(0, 42, false)]
    public async Task T4_bridged_name_budget_is_inclusive_and_reports_exact_prefix(int toolLength, int keyLength, bool fits)
    {
        using var f = new Fixture();
        var tool = toolLength == 15 ? "describe_source" : new string('t', toolLength);
        f.Tools = toolLength == 0 ? [] : ["s", tool];
        var key = new string('a', keyLength);
        var snapshot = f.Snapshot();
        Assert.Equal(fits ? 0 : 1, await f.Run(key: key));
        if (fits) AssertEntry(f, f.Classic, key, Url);
        else
        {
            var maximum = 41 - toolLength;
            var expected = $"refused: longest tool {Q(toolLength == 0 ? "<none>" : tool)} leaves room for at most {maximum} key characters; " +
                (maximum > 0 ? $"use {Q(key[..maximum])} or a shorter --name" : "no nonempty --name fits");
            Assert.Equal(new[] { expected, Footer }, f.Lines);
            Assert.Equal(0, f.Files.Mutations);
            f.Unchanged(snapshot);
        }
    }

    [Fact]
    public async Task T4_ties_are_ordinal_and_malformed_tool_names_refuse_as_gateway_failure()
    {
        using var f = new Fixture(false);
        f.Tools = ["zzz", "AAA"];
        Assert.Equal(1, await f.Run(key: new string('a', 39)));
        Assert.Contains("longest tool \"AAA\"", f.Out.ToString());
        foreach (var tools in new IReadOnlyList<string>[] { [""], [null!] })
        {
            f.Tools = tools;
            var snapshot = f.Snapshot();
            Assert.Equal(1, await f.Run());
            Assert.Equal(new[] { Gateway, Footer }, f.Lines);
            f.Unchanged(snapshot);
            Assert.Equal(0, f.Files.Mutations);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a.b")]
    [InlineData("caf\u00e9")]
    [InlineData("\n")]
    [InlineData("key\n")]
    [InlineData("\ud83d\ude00")]
    public async Task T5_invalid_keys_do_not_construct_context(string key)
    {
        await UsageOnly(["--register", Url, "--name", key]);
        await UsageOnly(["--unregister", "--name", key]);
    }

    [Fact]
    public async Task T5_ascii_case_is_retained()
    {
        using var f = new Fixture();
        Assert.Equal(0, await f.Run(key: "Data_2026-a"));
        AssertEntry(f, f.Classic, "Data_2026-a", Url);
    }

    [Fact]
    public async Task T6_alias_identity_normalizes_slashes_case_and_dots_and_preserves_nonowners()
    {
        using var f = new Fixture();
        var opposite = f.Exe.Replace(Path.DirectorySeparatorChar, Path.DirectorySeparatorChar == '/' ? '\\' : '/').ToUpperInvariant();
        var dotted = Path.Combine(Path.GetDirectoryName(f.Exe)!, ".", Path.GetFileName(f.Exe));
        var servers = new JsonObject
        {
            ["z-old"] = Server(opposite, Url), ["a-old"] = Server(dotted, Url),
            ["other"] = Server("other-app"), ["sibling"] = Server(Path.Combine(f.Root, "sibling", Path.GetFileName(f.Exe))),
            ["relative"] = Server("DataGateway.Proxy.exe"), ["null"] = null,
            ["number"] = 12, ["array"] = new JsonArray(1), ["badCommand"] = new JsonObject { ["command"] = 42 }
        };
        f.Seed(f.Classic, new JsonObject { ["mcpServers"] = servers }.ToJsonString());
        using var before = JsonDocument.Parse(File.ReadAllBytes(f.Classic));
        Assert.Equal(0, await f.Run());
        using var after = JsonDocument.Parse(File.ReadAllBytes(f.Classic));
        var retained = after.RootElement.GetProperty("mcpServers");
        Assert.False(retained.TryGetProperty("a-old", out _));
        Assert.False(retained.TryGetProperty("z-old", out _));
        foreach (var key in new[] { "other", "sibling", "relative", "null", "number", "array", "badCommand" })
            RawEqual(before.RootElement.GetProperty("mcpServers").GetProperty(key), retained.GetProperty(key));
        Assert.Equal(2, f.Lines.Length);
        Assert.Contains("; removed \"a-old\"; removed \"z-old\"; backup ", f.Lines[0]);
        AssertEntry(f, f.Classic, "data", Url);
    }

    [Fact]
    public async Task T7_repeat_is_a_complete_byte_noop_and_url_change_has_unique_backup()
    {
        using var f = new Fixture();
        f.Seed(f.Classic, "{}");
        Assert.Equal(0, await f.Run());
        var snapshot = f.Snapshot();
        var mutations = f.Files.Mutations;
        Assert.Equal(0, await f.Run());
        f.Unchanged(snapshot);
        Assert.Equal(mutations, f.Files.Mutations);
        Assert.Contains("skipped: already registered", f.Out.ToString());
        Assert.Equal(0, await f.Run(url: "http://example.invalid/changed"));
        Assert.Equal(2, f.Backups().Length);
        Assert.Contains("updated from " + Q(Url), f.Out.ToString());
        AssertEntry(f, f.Classic, "data", "http://example.invalid/changed");
    }

    [Fact]
    public async Task T8_unregister_removes_only_selected_key_regardless_of_ownership()
    {
        using var f = new Fixture();
        f.Seed(f.Classic, new JsonObject { ["mcpServers"] = new JsonObject
        { ["data"] = Server("other-app"), ["alias"] = Server(f.Exe), ["other"] = Server("other") } }.ToJsonString());
        Assert.Equal(0, await f.Run(false));
        var map = JsonNode.Parse(File.ReadAllBytes(f.Classic))!["mcpServers"]!.AsObject();
        Assert.Equal(new[] { "alias", "other" }, map.Select(p => p.Key));
        var snapshot = f.Snapshot();
        var mutations = f.Files.Mutations;
        Assert.Equal(0, await f.Run(false));
        Assert.Equal(mutations, f.Files.Mutations);
        f.Unchanged(snapshot);
        Assert.Equal(0, f.ListCalls);
        f.Seed(f.Classic, f.Entry());
        Assert.Equal(0, await f.Run(false));
        Assert.Empty(JsonNode.Parse(File.ReadAllBytes(f.Classic))!["mcpServers"]!.AsObject());
        snapshot = f.Snapshot();
        Assert.Equal(0, await f.Run(false));
        f.Unchanged(snapshot);
    }

    [Theory]
    [InlineData("missing-key")]
    [InlineData("absent-map")]
    [InlineData("absent-file")]
    [InlineData("empty-tree")]
    public async Task T8_unregister_missing_things_is_success_without_creates_or_gateway(string kind)
    {
        using var f = new Fixture(kind != "empty-tree");
        if (kind == "missing-key") f.Seed(f.Classic, "{\"mcpServers\":{}}");
        if (kind == "absent-map") f.Seed(f.Classic, "{\"preferences\":true}");
        var snapshot = f.Snapshot();
        f.Context = f.Context with { ListToolsAsync = (_, _) => throw new InvalidOperationException() };
        Assert.Equal(0, await f.Run(false));
        f.Unchanged(snapshot);
        Assert.Equal(0, f.Files.Mutations);
        Assert.Equal(0, f.ListCalls);
        Assert.Contains(kind == "empty-tree" ? "skipped: no Claude Desktop configs found" : "skipped:", f.Out.ToString());
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("DOTNET.EXE")]
    [InlineData("null")]
    [InlineData("relative")]
    [InlineData("invalid")]
    [InlineData("platform")]
    [InlineData("roots")]
    public async Task T9_apphost_platform_and_roots_refuse_before_policy_or_process_work(string kind)
    {
        foreach (var register in new[] { true, false })
        {
            using var f = new Fixture();
            var executable = kind switch
            {
                "null" => null, "relative" => "DataGateway.Proxy.exe", "invalid" => "bad\0path",
                "dotnet" or "DOTNET.EXE" => Path.Combine(f.Root, kind), _ => f.Exe
            };
            f.Context = f.Context with { ExecutablePath = executable, IsWindows = kind != "platform", AppData = kind == "roots" ? "relative" : f.Context.AppData };
            var snapshot = f.Snapshot();
            Assert.Equal(1, await f.Run(register));
            Assert.Equal(0, f.PolicyCalls);
            Assert.Equal(0, f.ProcessCalls);
            Assert.Equal(0, f.ListCalls);
            Assert.Empty(f.Files.Calls);
            f.Unchanged(snapshot);
            var body = kind switch
            {
                "dotnet" or "DOTNET.EXE" => "launched via 'dotnet' host; run the apphost executable instead",
                "platform" => "Claude Desktop registration is supported on Windows only",
                "roots" => "cannot resolve the required Windows profile folders",
                _ => "cannot determine an absolute apphost executable path"
            };
            Assert.Equal(new[] { "refused: " + body, Footer }, f.Lines);
        }
    }

    [Theory]
    [InlineData("http")]
    [InlineData("initialize")]
    [InlineData("list")]
    [InlineData("cancel")]
    public async Task T10_gateway_phase_failures_never_create_fallback(string phase)
    {
        using var f = new Fixture(false);
        f.Context = f.Context with { ListToolsAsync = (_, _) => throw (phase switch
        {
            "http" => new HttpRequestException(), "cancel" => new OperationCanceledException(),
            _ => new InvalidOperationException("synthetic " + phase)
        }) };
        var snapshot = f.Snapshot();
        Assert.Equal(1, await f.Run());
        Assert.Equal(new[] { Gateway, Footer }, f.Lines);
        Assert.Empty(f.Files.Calls);
        f.Unchanged(snapshot);
        Assert.False(Directory.Exists(Path.GetDirectoryName(f.Classic)));
        Assert.Equal(0, await f.Run(false));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("-register")]
    [InlineData("--Register")]
    [InlineData("--")]
    [InlineData("--regitser")]
    public async Task T11_unknown_dash_verbs_are_usage_in_process_and_subprocess(string verb)
    {
        await UsageOnly([verb]);
        var tap = await ProxyWire.Tap([verb], []);
        Assert.Equal(2, tap.ExitCode);
        Assert.Empty(tap.Stdout);
        Assert.Equal(Usage, tap.Stderr);
    }

    [Fact]
    public async Task T11_server_dispatch_fallthrough_does_not_create_context_or_writers()
    {
        foreach (var args in new string[][] { [], ["http://example.invalid/mcp"], ["ftp://bad"], ["http://example.invalid", "--bogus"] })
        {
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            Assert.Null(await ProxyRegistration.TryHandleAsync(args, () => throw new Exception(), stdout, stderr));
            Assert.Equal("", stdout.ToString()); Assert.Equal("", stderr.ToString());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T12_backup_is_exact_including_bom_and_precedes_closed_temp_and_move(bool bom)
    {
        using var f = new Fixture();
        var before = Encoding.UTF8.GetBytes(" {\n \"prefs\":true } ");
        if (bom) before = [0xef, 0xbb, 0xbf, .. before];
        File.WriteAllBytes(f.Classic, before);
        f.Files.Before = (op, path, destination) =>
        {
            if (op != "move") return;
            Assert.Equal(before, File.ReadAllBytes(destination!));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        };
        Assert.Equal(0, await f.Run());
        var backup = Assert.Single(f.Backups());
        Assert.Equal(before, File.ReadAllBytes(backup));
        Assert.Matches(@"\.\d{8}T\d{13}Z\.[0-9a-f]{32}\.bak$", backup);
        var calls = f.Files.Calls;
        var copy = calls.FindIndex(c => c.Op == "copy");
        var verify = calls.FindIndex(c => c.Op == "read" && c.Path == backup);
        var write = calls.FindIndex(c => c.Op == "write");
        var move = calls.FindIndex(c => c.Op == "move");
        Assert.True(copy < verify && verify < write && write < move);
        Assert.False(File.ReadAllBytes(f.Classic).AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        f.Files.Before = null;
        Assert.Equal(0, await f.Run(url: "https://example.invalid/new"));
        Assert.Equal(2, f.Backups().Length);
        Assert.Equal(2, f.Backups().Distinct().Count());
        using var missing = new Fixture();
        Assert.Equal(0, await missing.Run());
        Assert.Empty(missing.Backups());
    }

    [Fact]
    public async Task T13_exhaustive_bounded_discovery_both_package_families_and_lexical_dedup()
    {
        using var f = new Fixture();
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { f.Classic };
        f.Context = f.Context with { ClaudeUserDataDirectory = Path.Combine(f.Context.AppData, ".", "Claude").Replace(Path.DirectorySeparatorChar, '/') };
        foreach (var root in new[] { "Local", "Roaming" })
        {
            var path = f.Config(root, "Claude-3p"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); expected.Add(path);
        }
        foreach (var package in new[] { "Claude_one", "cLaUdE_two", "AnthropicPBC.Claude_one", "aNtHrOpIcPbC.cLaUdE_two" })
        {
            foreach (var tail in new[] { new[] { "Roaming", "Claude" }, ["Local", "Claude"], ["Local", "Claude-3p"], ["Roaming", "Claude-3p"] })
            {
                var path = f.Config("Local", "Packages", package, "LocalCache", tail[0], tail[1]);
                if (tail[0] == "Roaming" && tail[1] == "Claude") Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                else f.Seed(path, "{}");
                expected.Add(path);
            }
        }
        foreach (var tail in new[] { new[] { "Claude-Data" }, ["Claude-Data", "one"], ["Claude-Data", "two"], ["Claude"], ["Claude Nest"], ["Claude Nest-3p"] })
        { var path = f.Config(["Local", .. tail]); f.Seed(path, "{}"); expected.Add(path); }
        var emptyB = f.Config("Local", "Claude-Data", "empty"); Directory.CreateDirectory(Path.GetDirectoryName(emptyB)!);
        var excluded = new[]
        {
            f.Config("Roaming", "Claude-modified"), f.Config("Local", "Packages", "Other_foo", "LocalCache", "Roaming", "Claude"),
            f.Config("ProgramFiles", "WindowsApps", "Claude_one"), f.Config("Local", "AnthropicClaude"),
            f.Config("Local", "OtherClient"), f.Config("Local", "Claude-Data", "one", "grandchild")
        };
        foreach (var path in excluded) f.Seed(path, "{\"sentinel\":true}");
        f.Seed(f.Classic + ".bak", "sentinel"); f.Seed(f.Classic + ".copy", "sentinel");
        Assert.Equal(0, await f.Run());
        Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase), f.Lines[..^1].Select(line => JsonSerializer.Deserialize<string>(line[..line.IndexOf(": ", StringComparison.Ordinal)])!).Order(StringComparer.OrdinalIgnoreCase));
        foreach (var path in expected) AssertEntry(f, path, "data", Url);
        foreach (var path in excluded) Assert.Equal("{\"sentinel\":true}", File.ReadAllText(path));
        Assert.Equal("sentinel", File.ReadAllText(f.Classic + ".bak"));
        Assert.Equal("sentinel", File.ReadAllText(f.Classic + ".copy"));
        Assert.False(File.Exists(emptyB));
        Assert.Equal(1, f.Files.Calls.Count(c => c.Op == "directories" && c.Path == Path.Combine(f.Context.LocalAppData, "Packages")));
        Assert.Equal(1, f.Files.Calls.Count(c => c.Op == "directories" && c.Path == Path.Combine(f.Context.LocalAppData, "Claude-Data")));
        Assert.Empty(f.Temps());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("empty-B")]
    [InlineData("existing-B")]
    [InlineData("separate-relocation")]
    public async Task T13_fallback_requires_conclusive_absence_and_relocation_is_included(string kind)
    {
        using var f = new Fixture(false);
        var b = f.Config("Local", "Claude Nest");
        if (kind == "empty-B") Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        if (kind == "existing-B") f.Seed(b, "{}");
        var relocation = Path.Combine(f.Root, "relocated");
        if (kind == "separate-relocation")
        { Directory.CreateDirectory(relocation); f.Context = f.Context with { ClaudeUserDataDirectory = relocation }; }
        Assert.Equal(0, await f.Run());
        if (kind is "empty" or "empty-B")
        {
            AssertEntry(f, f.Classic, "data", Url);
            Assert.Contains("; created classic config directory", f.Out.ToString());
            Assert.False(File.Exists(b));
        }
        else
        {
            Assert.False(Directory.Exists(Path.GetDirectoryName(f.Classic)));
            AssertEntry(f, kind == "existing-B" ? b : Path.Combine(relocation, ClaudeConfigFiles.Filename), "data", Url);
            Assert.DoesNotContain("created classic", f.Out.ToString());
        }
        Assert.False(Directory.Exists(Path.Combine(f.Context.AppData, "Claude-3p")));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("invalid")]
    [InlineData("excluded")]
    [InlineData("binaries")]
    [InlineData("local-binaries")]
    [InlineData("other-client")]
    [InlineData("managed")]
    [InlineData("Packages")]
    [InlineData("Claude-Data")]
    [InlineData("candidate")]
    public async Task T13_discovery_errors_continue_accessible_targets_and_suppress_fallback(string kind)
    {
        foreach (var accessible in new[] { false, true })
        {
            using var f = new Fixture(accessible);
            var relocation = kind switch
            {
                "relative" => "relative", "invalid" => "bad\0path",
                "excluded" => Path.Combine(f.Context.AppData, "Claude-modified", "child"),
                "binaries" => Path.Combine(f.Context.ProgramFiles, "WindowsApps", "Claude_test"),
                "local-binaries" => Path.Combine(f.Context.LocalAppData, "AnthropicClaude"),
                "other-client" => Path.Combine(f.Context.LocalAppData, ".vscode", "config"),
                "managed" => Path.Combine(f.Context.LocalAppData, "managed-config"), _ => null
            };
            if (relocation is not null) f.Context = f.Context with { ClaudeUserDataDirectory = relocation };
            else
            {
                var denied = Path.Combine(f.Context.LocalAppData, kind == "candidate" ? "Claude-3p" : kind);
                Directory.CreateDirectory(denied);
                f.Files.Before = (op, path, _) => { if (path == denied && op == (kind == "candidate" ? "attributes" : "directories")) throw new UnauthorizedAccessException(); };
            }
            Assert.Equal(1, await f.Run());
            Assert.Contains(relocation is not null ? "invalid or excluded relocation directory" : "cannot inspect candidate folder", f.Out.ToString());
            if (accessible) AssertEntry(f, f.Classic, "data", Url);
            else
            {
                Assert.False(Directory.Exists(Path.GetDirectoryName(f.Classic)));
                Assert.Equal(0, f.Files.Mutations);
                Assert.Empty(f.Snapshot());
            }
            Assert.Empty(f.Temps());
        }
    }

    public static IEnumerable<object?[]> Policies()
    {
        yield return [0, null, false]; yield return [false, null, false];
        yield return [null, 0, false]; yield return [null, false, false];
        yield return [1, 0, true]; yield return [true, false, true];
        yield return [0, 1, false]; yield return [null, null, true];
        yield return [" FALSE ", true, false]; yield return ["0", null, false];
        yield return ["unknown", 0, true]; yield return ["1", false, true];
        yield return ["TrUe", false, true]; yield return [2L, false, true];
        yield return [0L, true, false]; yield return [0U, true, false];
    }
    [Theory]
    [MemberData(nameof(Policies))]
    public async Task T14_policy_precedence_and_disabled_value_truth_table(object? machine, object? user, bool allowed)
    {
        foreach (var register in new[] { true, false })
        {
            using var f = new Fixture();
            f.Context = f.Context with { ReadPolicy = () => new(machine, user) };
            var snapshot = f.Snapshot();
            Assert.Equal(allowed ? 0 : 1, await f.Run(register));
            if (!allowed)
            {
                Assert.Equal(new[] { Policy, Footer }, f.Lines);
                Assert.Equal(0, f.ProcessCalls); Assert.Equal(0, f.ListCalls); Assert.Empty(f.Files.Calls);
                f.Unchanged(snapshot);
            }
        }
    }

    [Fact]
    public async Task T14_unreadable_policy_is_a_separate_refusal()
    {
        using var f = new Fixture();
        f.Context = f.Context with { ReadPolicy = () => throw new UnauthorizedAccessException("secret") };
        var snapshot = f.Snapshot();
        foreach (var register in new[] { true, false })
        {
            Assert.Equal(1, await f.Run(register));
            Assert.Equal(new[] { "refused: cannot read Claude Desktop policy", Footer }, f.Lines);
            f.Unchanged(snapshot);
            Assert.Empty(f.Files.Calls); Assert.Equal(0, f.ProcessCalls); Assert.Equal(0, f.ListCalls);
        }
    }

    [Theory]
    [InlineData("other")]
    [InlineData("missing")]
    [InlineData("number-command")]
    [InlineData("relative")]
    [InlineData("null")]
    [InlineData("array")]
    [InlineData("scalar")]
    public async Task T15_ownership_collision_is_a_batch_barrier_with_explicit_unregister_escape(string kind)
    {
        using var f = new Fixture();
        JsonNode? entry = kind switch
        {
            "other" => Server(Path.Combine(f.Root, "other.exe")), "missing" => new JsonObject(),
            "number-command" => new JsonObject { ["command"] = 42 }, "relative" => Server("relative.exe"),
            "null" => null, "array" => new JsonArray(1), _ => JsonValue.Create(1)
        };
        if (entry is JsonObject obj) obj["env"] = new JsonObject { ["TOKEN"] = "SYNTHETIC_ONLY" };
        f.Seed(f.Classic, new JsonObject { ["mcpServers"] = new JsonObject { ["data"] = entry } }.ToJsonString());
        var good = f.Config("Local", "Claude-3p"); f.Seed(good, "{}");
        var snapshot = f.Snapshot();
        Assert.Equal(1, await f.Run());
        f.Unchanged(snapshot); Assert.Equal(0, f.Files.Mutations);
        Assert.Contains("another config has a name conflict or invalid mcpServers; no configs were changed", f.Out.ToString());
        var program = kind == "other" ? Path.Combine(f.Root, "other.exe") : kind == "relative" ? "relative.exe" : "<missing or non-string command>";
        Assert.Contains($"name \"data\" belongs to {Q(program)}; choose another --name or use --unregister first", f.Out.ToString());
        Assert.DoesNotContain("SYNTHETIC_ONLY", f.Out.ToString());
        Assert.Equal(0, await f.Run(false));
        Assert.Equal(0, await f.Run());
        AssertEntry(f, f.Classic, "data", Url);
        f.Seed(f.Classic, f.Entry(extra: true));
        Assert.Equal(0, await f.Run(url: "https://example.invalid/new"));
        AssertEntry(f, f.Classic, "data", "https://example.invalid/new");
    }

    [Theory]
    [InlineData("store", true)]
    [InlineData("anthropic-store", true)]
    [InlineData("local-root", true)]
    [InlineData("local-app", true)]
    [InlineData("case-slashes", true)]
    [InlineData("vscode", false)]
    [InlineData("wrong-name", false)]
    [InlineData("wrong-file", false)]
    [InlineData("null", false)]
    [InlineData("unrelated", false)]
    [InlineData("store-other", false)]
    [InlineData("local-other", false)]
    [InlineData("modified", false)]
    [InlineData("empty-suffix", false)]
    [InlineData("wrong-ancestor", false)]
    [InlineData("claude-code", false)]
    public void T16_desktop_classification_uses_only_injected_path_segments(string kind, bool expected)
    {
        using var f = new Fixture();
        var path = kind switch
        {
            "store" or "case-slashes" or "wrong-name" => Path.Combine(f.Context.ProgramFiles, "WindowsApps", "Claude_test", "app", "Claude.exe"),
            "anthropic-store" => Path.Combine(f.Context.ProgramFiles, "WindowsApps", "AnthropicPBC.Claude_test", "app", "Claude.exe"),
            "local-root" => Path.Combine(f.Context.LocalAppData, "AnthropicClaude", "Claude.exe"),
            "local-app" => Path.Combine(f.Context.LocalAppData, "AnthropicClaude", "app-1", ".", "Claude.exe"),
            "vscode" => Path.Combine(f.Root, ".vscode", "extensions", "claude.exe"),
            "wrong-file" => Path.Combine(f.Context.LocalAppData, "AnthropicClaude", "other.exe"),
            "null" => null, "unrelated" => Path.Combine(f.Root, "Claude.exe"),
            "store-other" => Path.Combine(f.Context.ProgramFiles, "WindowsAppsOther", "Claude_test", "Claude.exe"),
            "local-other" => Path.Combine(f.Context.LocalAppData, "AnthropicClaudeOther", "Claude.exe"),
            "modified" => Path.Combine(f.Context.ProgramFiles, "WindowsApps", "Claude-modified", "Claude.exe"),
            "empty-suffix" => Path.Combine(f.Context.ProgramFiles, "WindowsApps", "Claude_", "Claude.exe"),
            "wrong-ancestor" => Path.Combine(f.Context.ProgramFiles, "Claude_test", "WindowsApps", "Claude.exe"),
            _ => Path.Combine(f.Root, "claude-code", "Claude.exe")
        };
        if (kind == "case-slashes") path = path!.Replace(Path.DirectorySeparatorChar, Path.DirectorySeparatorChar == '/' ? '\\' : '/').ToUpperInvariant();
        Assert.Equal(expected, ClaudeDesktopEnvironment.IsDesktop(new(kind == "wrong-name" ? "code" : "CLAUDE.EXE", path), f.Context));
    }

    [Fact]
    public async Task T16_whole_process_enumeration_failure_is_global()
    {
        using var f = new Fixture();
        f.Context = f.Context with { ReadProcesses = () => throw new InvalidOperationException() };
        var snapshot = f.Snapshot();
        Assert.Equal(1, await f.Run());
        Assert.Equal(new[] { "refused: cannot inspect running Claude Desktop processes", Footer }, f.Lines);
        f.Unchanged(snapshot); Assert.Empty(f.Files.Calls); Assert.Equal(0, f.ListCalls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public async Task T17_nonobject_server_map_is_global_for_both_verbs(string value)
    {
        foreach (var register in new[] { true, false })
        {
            using var f = new Fixture();
            f.Seed(f.Classic, "{\"mcpServers\":" + value + "}");
            f.Seed(f.Config("Local", "Claude-3p"), f.Entry());
            var snapshot = f.Snapshot();
            Assert.Equal(1, await f.Run(register));
            f.Unchanged(snapshot); Assert.Equal(0, f.Files.Mutations);
            Assert.Contains("refused: mcpServers is not a JSON object", f.Out.ToString());
            Assert.Contains("another config has a name conflict or invalid mcpServers; no configs were changed", f.Out.ToString());
            Assert.Equal(3, f.Lines.Length);
        }
    }

    [Fact]
    public async Task T17_lowercase_map_is_unrelated_and_absent_map_is_legal()
    {
        using var f = new Fixture();
        const string raw = "{ \"secret\":\"SYNTHETIC_ONLY\" }";
        f.Seed(f.Classic, "{\"mcpservers\":" + raw + "}");
        Assert.Equal(0, await f.Run(false));
        Assert.Equal(0, f.Files.Mutations);
        Assert.Equal(0, await f.Run());
        using var doc = JsonDocument.Parse(File.ReadAllBytes(f.Classic));
        Assert.Equal(raw, doc.RootElement.GetProperty("mcpservers").GetRawText());
        AssertEntry(f, f.Classic, "data", Url);
    }

    [Theory]
    [InlineData("copy", "cannot create or verify backup")]
    [InlineData("verify", "cannot create or verify backup")]
    [InlineData("write", "cannot write temporary config")]
    [InlineData("move", "cannot replace config file")]
    [InlineData("cleanup", "cannot replace config file; cannot remove temporary config")]
    [InlineData("drift", "config changed since it was read")]
    [InlineData("late-drift", "config changed since it was read")]
    [InlineData("appeared", "config changed since it was read")]
    [InlineData("collision", "cannot write temporary config")]
    [InlineData("partial-cleanup", "cannot write temporary config; cannot remove temporary config")]
    [InlineData("vanished", "config changed since it was read")]
    public async Task T18_commit_failures_are_local_and_preserve_originals_and_foreign_temps(string fault, string reason)
    {
        using var f = new Fixture();
        // Local sorts first, so this target fails and a later independent target must still succeed.
        var first = f.Config("Local", "Claude-3p");
        var before = f.Seed(first, "{\"old\":true}");
        if (fault == "appeared") File.Delete(first);
        f.Seed(f.Classic, "{}");
        var reads = 0;
        string? foreignTemp = null;
        f.Files.Before = (op, path, destination) =>
        {
            var belongs = path == first || destination == first || Path.GetDirectoryName(path) == Path.GetDirectoryName(first);
            if (!belongs) return;
            if (op == "read" && path == first && ++reads == 2 && fault == "drift") File.WriteAllText(first, "{\"concurrent\":true}");
            if (op == "read" && path == first && reads == 2 && fault == "vanished") File.Delete(first);
            if (op == "attributes" && path == first && fault == "appeared" && f.ListCalls == 1 && f.ProcessCalls == 2)
                File.WriteAllText(first, "{\"concurrent\":true}");
            if (op == "read" && path.EndsWith(".bak", StringComparison.Ordinal) && fault == "verify") File.WriteAllText(path, "wrong backup");
            if (op == "write" && fault == "late-drift") File.WriteAllText(first, "{\"concurrent\":true}");
            if (op == "write" && fault == "collision") { foreignTemp = path; File.WriteAllText(path, "foreign-temp"); }
            if (op == "write" && fault == "partial-cleanup") { File.WriteAllText(path, "partial-temp"); throw new RegistrationFiles.TemporaryCleanupException(); }
            if (op == fault || (fault == "cleanup" && op is "move" or "delete")) throw new IOException("SYNTHETIC_ONLY");
        };
        Assert.Equal(1, await f.Run());
        Assert.Contains(Q(first) + ": refused: " + reason, f.Lines);
        Assert.DoesNotContain("SYNTHETIC_ONLY", f.Out.ToString());
        if (fault == "vanished") Assert.False(File.Exists(first));
        else if (fault is "drift" or "late-drift" or "appeared") Assert.Equal("{\"concurrent\":true}", File.ReadAllText(first));
        else Assert.Equal(before, File.ReadAllBytes(first));
        AssertEntry(f, f.Classic, "data", Url);
        var firstBackups = f.Backups().Where(path => path.StartsWith(first + ".", StringComparison.Ordinal)).ToArray();
        Assert.Equal(fault is "copy" or "drift" or "appeared" or "vanished" ? 0 : 1, firstBackups.Length);
        if (fault == "collision") { Assert.Equal("foreign-temp", File.ReadAllText(foreignTemp!)); Assert.Equal(foreignTemp, Assert.Single(f.Temps())); }
        else if (fault is "cleanup" or "partial-cleanup") Assert.Single(f.Temps());
        else Assert.Empty(f.Temps());
        Assert.Equal(3, f.Lines.Length);
    }

    [Fact]
    public async Task T18_disappeared_tier_b_and_directory_targets_are_not_recreated()
    {
        using var f = new Fixture(false);
        var path = f.Config("Local", "Claude Nest"); f.Seed(path, "{}");
        var attributes = 0;
        f.Files.Before = (op, candidate, _) => { if (op == "attributes" && candidate == path && ++attributes == 2) File.Delete(path); };
        Assert.Equal(1, await f.Run());
        Assert.Contains("discovered config file is no longer present", f.Out.ToString());
        Assert.False(File.Exists(path)); Assert.Equal(0, f.Files.Mutations);
        Assert.False(Directory.Exists(Path.GetDirectoryName(f.Classic)));
        f.Files.Before = null;
        Directory.CreateDirectory(path);
        Assert.Equal(1, await f.Run());
        Assert.Contains("config path is not a file", f.Out.ToString());
        Assert.True(Directory.Exists(path)); Assert.Equal(0, f.Files.Mutations);
    }

    [Fact]
    public async Task T18_backup_copy_drift_and_fallback_mkdir_failure_refuse_safely()
    {
        using var f = new Fixture(false);
        var snapshot = f.Snapshot();
        f.Files.Before = (op, _, _) => { if (op == "mkdir") throw new UnauthorizedAccessException(); };
        Assert.Equal(1, await f.Run());
        Assert.Contains("cannot create classic config directory", f.Out.ToString());
        f.Unchanged(snapshot); Assert.False(Directory.Exists(Path.GetDirectoryName(f.Classic)));
    }

    public static IEnumerable<object[]> Grammar()
    {
        yield return [new[] { "--register" }];
        yield return [new[] { "--register", Url }];
        yield return [new[] { "--register", Url, "--name" }];
        yield return [new[] { "--register", "--name", "data" }];
        yield return [new[] { "--register", "--name", "data", Url }];
        yield return [new[] { "--register", "ftp://example.invalid", "--name", "data" }];
        yield return [new[] { "--register", "relative", "--name", "data" }];
        yield return [new[] { "--register", "://bad", "--name", "data" }];
        yield return [new[] { "--register", Url, "--unknown", "data" }];
        yield return [new[] { "--register", Url, "--name", "data", "--name", "data" }];
        yield return [new[] { "--register", Url, "--name", "data", "extra" }];
        yield return [new[] { "--unregister" }];
        yield return [new[] { "--unregister", "--name" }];
        yield return [new[] { "--unregister", Url, "--name", "data" }];
        yield return [new[] { "--unregister", "--name", "data", "extra" }];
        yield return [new[] { "--unregister", "--name", "data", "--name", "data" }];
        yield return [new[] { "--unregister", "--NAME", "data" }];
    }
    [Theory]
    [MemberData(nameof(Grammar))]
    public async Task T19_exact_grammar_rejects_extras_before_context_creation(string[] arguments) => await UsageOnly(arguments);

    [Fact]
    public async Task T19_valid_forms_retain_original_url_spelling_and_allow_key_register()
    {
        using var f = new Fixture();
        Assert.Equal(0, await f.Run(key: "register"));
        AssertEntry(f, f.Classic, "register", Url);
        Assert.Equal(0, await f.Run(false, "register"));
        Assert.Equal(0, f.ListCalls - 1);
        Assert.Equal(0, await f.Run(url: "https://example.invalid/no-suffix"));
        AssertEntry(f, f.Classic, "data", "https://example.invalid/no-suffix");
        var malformed = await ProxyWire.Tap(["--register", Url, "--name"], []);
        Assert.Equal(2, malformed.ExitCode); Assert.Empty(malformed.Stdout); Assert.Equal(Usage, malformed.Stderr);
    }

    [Fact]
    public async Task T20_actual_streamable_http_adapter_initializes_and_lists_synthetic_host()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost();
        await host.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var endpoint = ProxyWire.Endpoint(host.Client);
        var names = await ClaudeDesktopEnvironment.ListToolNamesAsync(endpoint, timeout.Token);
        await using var independent = await McpWire.Connect(host.Client, token: timeout.Token);
        var tools = await independent.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(tools.Select(tool => tool.Name), names);
        Assert.NotEmpty(names);
        using var f = new Fixture();
        f.Context = f.Context with { ListToolsAsync = ClaudeDesktopEnvironment.ListToolNamesAsync };
        Assert.Equal(0, await f.Run(url: endpoint.OriginalString));
        AssertEntry(f, f.Classic, "data", endpoint.OriginalString);
        // Reserve then release an ephemeral loopback port, without depending on a fixed service port.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var unavailable = new Fixture(false);
        unavailable.Context = unavailable.Context with { ListToolsAsync = ClaudeDesktopEnvironment.ListToolNamesAsync };
        var snapshot = unavailable.Snapshot();
        Assert.Equal(1, await unavailable.Run(url: $"http://127.0.0.1:{port}/mcp"));
        Assert.Equal(new[] { Gateway, Footer }, unavailable.Lines);
        unavailable.Unchanged(snapshot); Assert.Empty(unavailable.Files.Calls);
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("server/discover")]
    [InlineData("tools/list")]
    public async Task T20_actual_initialize_or_list_http_failure_refuses_before_files(string failingMethod)
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost();
        var rejected = 0;
        host.App.Use(async (context, next) =>
        {
            if (context.Request.Method == "POST" && context.Request.Path == "/mcp")
            {
                context.Request.EnableBuffering();
                using var document = await JsonDocument.ParseAsync(context.Request.Body);
                context.Request.Body.Position = 0;
                if (document.RootElement.TryGetProperty("method", out var method))
                {
                    // The current SDK first discovers capabilities; model an older server
                    // to reach the initialize handshake without changing production options.
                    if (failingMethod == "initialize" && method.GetString() == "server/discover")
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }
                    if (method.GetString() == failingMethod)
                    {
                        rejected++;
                        context.Response.StatusCode = 502;
                        return;
                    }
                }
            }
            await next(context);
        });
        await host.Start();
        using var f = new Fixture(false);
        f.Context = f.Context with { ListToolsAsync = ClaudeDesktopEnvironment.ListToolNamesAsync };
        var snapshot = f.Snapshot();
        Assert.Equal(1, await f.Run(url: ProxyWire.Endpoint(host.Client).OriginalString));
        Assert.True(rejected > 0);
        Assert.Equal(new[] { Gateway, Footer }, f.Lines);
        f.Unchanged(snapshot); Assert.Empty(f.Files.Calls);
    }

    // A second gateway (another URL) through the same proxy is kept; only an entry for the same URL is an old alias.
    [Fact]
    public async Task T6b_a_second_gateway_through_the_same_proxy_is_kept()
    {
        using var f = new Fixture();
        f.Seed(f.Classic, new JsonObject { ["mcpServers"] = new JsonObject
        { ["hr-data"] = Server(f.Exe, "http://localhost:5001/mcp"), ["sales-old"] = Server(f.Exe, Url) } }.ToJsonString());
        Assert.Equal(0, await f.Run());
        using var after = JsonDocument.Parse(File.ReadAllBytes(f.Classic));
        var servers = after.RootElement.GetProperty("mcpServers");
        Assert.True(servers.TryGetProperty("hr-data", out _));
        Assert.False(servers.TryGetProperty("sales-old", out _));
        AssertEntry(f, f.Classic, "data", Url);
    }

    [Fact]
    public async Task T21_output_is_one_line_per_target_and_candidate_with_escaped_aliases_and_footer()
    {
        using var f = new Fixture();
        const string alias = "legacy\nname";
        f.Seed(f.Classic, new JsonObject { ["mcpServers"] = new JsonObject
        { [alias] = Server(f.Exe, Url), ["other"] = new JsonObject { ["env"] = new JsonObject { ["TOKEN"] = "SYNTHETIC_ONLY" } } } }.ToJsonString());
        var bad = f.Config("Local", "Claude-3p"); var before = f.Seed(bad, "{");
        var inaccessible = Path.Combine(f.Context.LocalAppData, "Packages");
        f.Files.Before = (op, path, _) => { if (op == "attributes" && path == inaccessible) throw new UnauthorizedAccessException(); };
        Assert.Equal(1, await f.Run());
        Assert.Equal(4, f.Lines.Length); Assert.Equal(Footer, f.Lines[^1]);
        Assert.Equal(1, f.Lines.Count(line => line == Footer));
        Assert.Single(f.Lines, line => line.StartsWith(Q(f.Classic) + ":", StringComparison.Ordinal));
        Assert.Contains("; removed " + Q(alias) + "; backup ", f.Out.ToString());
        Assert.Contains(Q(inaccessible) + ": refused: cannot inspect candidate folder", f.Lines);
        Assert.Equal(before, File.ReadAllBytes(bad));
        Assert.DoesNotContain("SYNTHETIC_ONLY", f.Out.ToString());
        Assert.Equal("", f.Error.ToString()); Assert.Empty(f.Temps());
        f.Files.Before = null; f.Seed(bad, "{}");
        Assert.Equal(0, await f.Run()); Assert.Equal(3, f.Lines.Length);
    }

    [Fact]
    public void T22_readme_cli_order_and_unchanged_manual_examples_are_preserved()
    {
        var markdown = ReadmeTests.Markdown;
        var fragments = new[]
        {
            "DataGateway.Proxy --register http://localhost:5000/mcp --name sales-data",
            "Quit Claude Desktop from its tray icon first, not just its window",
            "DataGateway.Proxy --unregister --name sales-data",
            "For manual setup as a fallback, quit Claude Desktop"
        };
        var previous = -1;
        foreach (var fragment in fragments)
        { var index = markdown.IndexOf(fragment, StringComparison.Ordinal); Assert.True(index > previous); previous = index; }
        new ReadmeTests().T12_client_registration_names_the_data_and_is_not_an_API_example();
        Assert.Equal(new[] { "challenge", "describe", "list", "query", "refusal", "unknown-source", "vocabulary" }, ReadmeExamples.Parse(markdown).Select(example => example.Name).Order());
    }

    [Theory]
    [InlineData(true, "forward")]
    [InlineData(false, "forward")]
    [InlineData(true, "mixed")]
    [InlineData(false, "mixed")]
    [InlineData(true, "backslash")]
    [InlineData(false, "backslash")]
    public async Task T23_excluded_relocation_cannot_reenter_through_child_scan(bool register, string spelling)
    {
        using var f = new Fixture();
        var excluded = f.Config("Local", "Claude-Data", "managed-config");
        var before = f.Seed(excluded, f.Entry());
        var goodBefore = f.Seed(f.Classic, register ? "{}" : f.Entry());
        var directory = Path.GetDirectoryName(excluded)!;
        var forward = directory.Replace('\\', '/');
        var relocation = spelling switch
        {
            "forward" => forward,
            "mixed" => forward[..forward.LastIndexOf('/')] + "\\managed-config",
            _ => directory.Replace('/', '\\')
        };
        f.Context = f.Context with { ClaudeUserDataDirectory = relocation };
        Assert.Equal(1, await f.Run(register));
        Assert.Equal(before, File.ReadAllBytes(excluded));
        var backup = Assert.Single(f.Backups());
        Assert.StartsWith(f.Classic + ".", backup);
        Assert.Equal(goodBefore, File.ReadAllBytes(backup));
        if (register) AssertEntry(f, f.Classic, "data", Url);
        else Assert.Empty(JsonNode.Parse(File.ReadAllBytes(f.Classic))!["mcpServers"]!.AsObject());
        Assert.Equal(new[]
        {
            Q(directory) + ": refused: invalid or excluded relocation directory",
            Q(f.Classic) + ": " + (register ? "registered \"data\"" : "removed \"data\"") + "; backup " + Q(backup),
            Footer
        }, f.Lines);
        Assert.Equal(3, f.Files.Mutations);
        Assert.Empty(f.Temps());
        Assert.Empty(f.Error.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T24_child_exclusions_apply_without_a_relocation_and_keep_allowed_control(bool register)
    {
        foreach (var child in new[] { ".vscode", ".claude", "Claude Code", "Cursor", "managed",
            "managed-config", "archive.bak", "file.json.copy", "ordinary" })
        {
            using var f = new Fixture();
            var path = f.Config("Local", "Claude-Data", child);
            var before = f.Seed(path, f.Entry());
            f.Seed(f.Classic, register ? "{}" : f.Entry());
            Assert.Equal(0, await f.Run(register));
            var allowed = child == "ordinary";
            if (allowed)
            {
                if (register) AssertEntry(f, path, "data", Url);
                else Assert.Empty(JsonNode.Parse(File.ReadAllBytes(path))!["mcpServers"]!.AsObject());
                Assert.Single(f.Lines, line => line.StartsWith(Q(path) + ": ", StringComparison.Ordinal));
            }
            else
            {
                Assert.Equal(before, File.ReadAllBytes(path));
                Assert.DoesNotContain(f.Lines, line => line.StartsWith(Q(path) + ": ", StringComparison.Ordinal));
            }
            if (register) AssertEntry(f, f.Classic, "data", Url);
            else Assert.Empty(JsonNode.Parse(File.ReadAllBytes(f.Classic))!["mcpServers"]!.AsObject());
            Assert.Equal(allowed ? 2 : 1, f.Backups().Length);
            Assert.Equal(allowed ? 6 : 3, f.Files.Mutations);
            Assert.Equal(allowed ? 3 : 2, f.Lines.Length);
            Assert.Equal(Footer, f.Lines[^1]);
            Assert.Empty(f.Temps());
            Assert.Empty(f.Error.ToString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void T25_existing_classic_and_fallback_cannot_bypass_exclusions(bool existing)
    {
        using var f = new Fixture(false);
        var excludedContext = f.Context with { AppData = Path.Combine(f.Root, "managed-config", "Roaming") };
        var excluded = Path.Combine(excludedContext.AppData, "Claude", ClaudeConfigFiles.Filename);
        if (existing) f.Seed(excluded, f.Entry());
        var snapshot = f.Snapshot();
        foreach (var register in new[] { true, false })
        {
            var discovery = ClaudeConfigFiles.Discover(excludedContext, register);
            Assert.Empty(discovery.Targets);
            Assert.Empty(discovery.Errors);
        }
        var control = f.Context with { AppData = Path.Combine(f.Root, "ordinary", "Roaming") };
        var allowed = ClaudeConfigFiles.Discover(control, true);
        var target = Assert.Single(allowed.Targets);
        Assert.Equal(Path.Combine(control.AppData, "Claude", ClaudeConfigFiles.Filename), target.Path);
        Assert.True(target.TierA);
        Assert.True(target.Fallback);
        Assert.Empty(allowed.Errors);
        Assert.Equal(0, f.Files.Mutations);
        f.Unchanged(snapshot);
    }

    public static IEnumerable<object[]> SurrogateDocuments()
    {
        string[] documents =
        [
            """{"\uD800":1}""",
            """{"\uDEAD":1}""",
            """{"outer":[{"\uD800":1}]}""",
            """{"mcpServers":{"other":{"command":"\uD800"}}}""",
            """{"x":"\uDEAD"}""",
            """{"x":["\uD800"]}"""
        ];
        foreach (var register in new[] { true, false })
            foreach (var document in documents)
                yield return [register, document];
    }

    [Theory]
    [MemberData(nameof(SurrogateDocuments))]
    public async Task T26_invalid_surrogates_refuse_only_their_file_and_finish(bool register, string text)
    {
        using var f = new Fixture();
        var bad = f.Config("Local", "Claude-3p");
        var before = f.Seed(bad, text);
        var goodBefore = f.Seed(f.Classic, register ? "{}" : f.Entry());
        Assert.Equal(1, await f.Run(register));
        Assert.Equal(before, File.ReadAllBytes(bad));
        var backup = Assert.Single(f.Backups());
        Assert.StartsWith(f.Classic + ".", backup);
        Assert.Equal(goodBefore, File.ReadAllBytes(backup));
        if (register) AssertEntry(f, f.Classic, "data", Url);
        else Assert.Empty(JsonNode.Parse(File.ReadAllBytes(f.Classic))!["mcpServers"]!.AsObject());
        Assert.Equal(new[]
        {
            Q(bad) + ": refused: not valid JSON or duplicate JSON member",
            Q(f.Classic) + ": " + (register ? "registered \"data\"" : "removed \"data\"") + "; backup " + Q(backup),
            Footer
        }, f.Lines);
        Assert.Equal(3, f.Files.Mutations);
        Assert.Empty(f.Temps());
        Assert.Empty(f.Error.ToString());
    }

    [Theory]
    [MemberData(nameof(SurrogateDocuments))]
    public async Task T27_valid_pairs_are_accepted_and_retained(bool register, string invalid)
    {
        using var f = new Fixture();
        var path = f.Config("Local", "Claude-3p");
        var text = invalid.Replace("\\uD800", "\\uD83D\\uDE00", StringComparison.Ordinal)
            .Replace("\\uDEAD", "\\uD83D\\uDE00", StringComparison.Ordinal);
        var before = f.Seed(path, text);
        f.Seed(f.Classic, register ? "{}" : f.Entry());
        Assert.Equal(0, await f.Run(register));
        using var old = JsonDocument.Parse(before);
        using var after = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var property in old.RootElement.EnumerateObject())
        {
            if (property.Name == "mcpServers")
                RawEqual(property.Value.GetProperty("other"), after.RootElement.GetProperty("mcpServers").GetProperty("other"));
            else RawEqual(property.Value, after.RootElement.GetProperty(property.Name));
        }
        if (register)
        {
            AssertEntry(f, path, "data", Url);
            AssertEntry(f, f.Classic, "data", Url);
            var backup = Assert.Single(f.Backups(), name => name.StartsWith(path + ".", StringComparison.Ordinal));
            Assert.Equal(before, File.ReadAllBytes(backup));
        }
        else
        {
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(JsonNode.Parse(File.ReadAllBytes(f.Classic))!["mcpServers"]!.AsObject());
            Assert.DoesNotContain(f.Backups(), name => name.StartsWith(path + ".", StringComparison.Ordinal));
        }
        Assert.Equal(register ? 2 : 1, f.Backups().Length);
        Assert.Equal(register ? 6 : 3, f.Files.Mutations);
        Assert.Equal(3, f.Lines.Length);
        Assert.DoesNotContain(f.Lines, line => line.Contains("refused:", StringComparison.Ordinal));
        Assert.Equal(Footer, f.Lines[^1]);
        Assert.Empty(f.Temps());
        Assert.Empty(f.Error.ToString());
    }

    [Theory]
    [InlineData(true, "desktop")]
    [InlineData(false, "desktop")]
    [InlineData(true, "inspect-failure")]
    [InlineData(false, "inspect-failure")]
    [InlineData(true, "clear")]
    [InlineData(false, "clear")]
    public async Task T28_late_guard_names_all_plans_and_discovery_errors(bool register, string finalCheck)
    {
        using var f = new Fixture();
        f.Seed(f.Classic, "{");
        var changing = f.Config("Local", "Claude-3p");
        var goodBefore = f.Seed(changing, register ? "{}" : f.Entry());
        var noop = f.Config("Local", "Claude Nest");
        var entry = JsonNode.Parse(f.Entry())!;
        entry["mcpServers"]!["data"]!["args"] = new JsonArray(Url);
        var noopBefore = f.Seed(noop, register ? entry.ToJsonString() : "{}");
        var denied = Path.Combine(f.Context.LocalAppData, "Packages");
        f.Files.Before = (op, path, _) =>
        {
            if (op == "attributes" && path == denied) throw new UnauthorizedAccessException("SYNTHETIC_ONLY");
        };
        f.Context = f.Context with
        {
            ReadProcesses = () =>
            {
                if (++f.ProcessCalls == 1 || finalCheck == "clear") return [];
                if (finalCheck == "inspect-failure") throw new IOException("SYNTHETIC_ONLY");
                return [f.Desktop];
            }
        };
        var snapshot = f.Snapshot();
        Assert.Equal(1, await f.Run(register));
        var discoveryError = Q(denied) + ": refused: cannot inspect candidate folder";
        if (finalCheck == "clear")
        {
            var backup = Assert.Single(f.Backups());
            Assert.StartsWith(changing + ".", backup);
            Assert.Equal(goodBefore, File.ReadAllBytes(backup));
            if (register) AssertEntry(f, changing, "data", Url);
            else Assert.Empty(JsonNode.Parse(File.ReadAllBytes(changing))!["mcpServers"]!.AsObject());
            Assert.Equal("{", File.ReadAllText(f.Classic));
            Assert.Equal(noopBefore, File.ReadAllBytes(noop));
            Assert.Equal(new[]
            {
                discoveryError,
                Q(noop) + ": " + (register ? "skipped: already registered" : "skipped: key \"data\" not present"),
                Q(changing) + ": " + (register ? "registered \"data\"" : "removed \"data\"") + "; backup " + Q(backup),
                Q(f.Classic) + ": refused: not valid JSON or duplicate JSON member",
                Footer
            }, f.Lines);
            Assert.Equal(3, f.Files.Mutations);
        }
        else
        {
            var refusal = finalCheck == "desktop" ? Tray : "refused: cannot inspect running Claude Desktop processes";
            Assert.Equal(new[]
            {
                discoveryError,
                Q(noop) + ": " + refusal,
                Q(changing) + ": " + refusal,
                Q(f.Classic) + ": " + refusal,
                Footer
            }, f.Lines);
            f.Unchanged(snapshot);
            Assert.Equal(0, f.Files.Mutations);
            Assert.Empty(f.Backups());
        }
        Assert.Equal(2, f.ProcessCalls);
        Assert.Equal(register ? 1 : 0, f.ListCalls);
        Assert.Equal(1, f.Lines.Count(line => line == Footer));
        Assert.DoesNotContain("SYNTHETIC_ONLY", f.Out.ToString());
        Assert.Empty(f.Temps());
        Assert.Empty(f.Error.ToString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task T29_empty_plan_late_guard_retains_global_refusal_after_discovery_errors(bool register, bool discoveryError)
    {
        using var f = new Fixture(false);
        var denied = Path.Combine(f.Context.LocalAppData, "Packages");
        if (discoveryError)
            f.Files.Before = (op, path, _) =>
            {
                if (op == "attributes" && path == denied) throw new UnauthorizedAccessException();
            };
        f.Context = f.Context with { ReadProcesses = () => ++f.ProcessCalls == 1 ? [] : [f.Desktop] };
        var snapshot = f.Snapshot();
        Assert.Equal(1, await f.Run(register));
        Assert.Equal(discoveryError
            ? new[] { Q(denied) + ": refused: cannot inspect candidate folder", Tray, Footer }
            : new[] { Tray, Footer }, f.Lines);
        f.Unchanged(snapshot);
        Assert.Equal(0, f.Files.Mutations);
        Assert.Equal(2, f.ProcessCalls);
        Assert.Equal(register ? 1 : 0, f.ListCalls);
        Assert.Empty(f.Backups());
        Assert.Empty(f.Temps());
        Assert.Empty(f.Error.ToString());
    }

    private static async Task UsageOnly(string[] args)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var factories = 0;
        Assert.Equal(2, await ProxyRegistration.TryHandleAsync(args, () => { factories++; throw new InvalidOperationException(); }, output, error));
        Assert.Equal(0, factories); Assert.Equal("", output.ToString());
        Assert.Equal(Usage, Lines(error.ToString()));
    }
    private static string[] Lines(string text) => text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
    private static void RawEqual(JsonElement expected, JsonElement actual) => Assert.Equal(Encoding.UTF8.GetBytes(expected.GetRawText()), Encoding.UTF8.GetBytes(actual.GetRawText()));
    private static JsonObject Server(string command, string url = "old-url") => new() { ["command"] = command, ["args"] = new JsonArray(url) };
    private static void AssertEntry(Fixture f, string path, string key, string url)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var entry = document.RootElement.GetProperty("mcpServers").GetProperty(key);
        Assert.Equal(new[] { "command", "args" }, entry.EnumerateObject().Select(property => property.Name));
        Assert.Equal(f.Exe, entry.GetProperty("command").GetString());
        Assert.Equal(new[] { url }, entry.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "dg-registration-" + Guid.NewGuid().ToString("N"));
        internal string Exe => Path.Combine(Root, "installed", "DataGateway.Proxy.exe");
        internal string Classic => Config("Roaming", "Claude");
        internal RegistrationContext Context;
        internal readonly RecordingFiles Files = new();
        internal readonly StringWriter Out = new();
        internal readonly StringWriter Error = new();
        internal int PolicyCalls, ProcessCalls, ListCalls;
        internal IReadOnlyList<string> Tools = ["list_sources", "describe_source", "query_source"];
        internal DesktopProcess Desktop => new("claude", Path.Combine(Context.LocalAppData, "AnthropicClaude", "app-1", "Claude.exe"));
        internal string[] Lines => ProxyRegistrationTests.Lines(Out.ToString());
        internal Fixture(bool classic = true)
        {
            foreach (var directory in new[] { Root, Path.Combine(Root, "Roaming"), Path.Combine(Root, "Local"), Path.Combine(Root, "ProgramFiles") }) Directory.CreateDirectory(directory);
            if (classic) Directory.CreateDirectory(Path.GetDirectoryName(Classic)!);
            Context = new(Exe, true, Path.Combine(Root, "Roaming"), Path.Combine(Root, "Local"), Path.Combine(Root, "ProgramFiles"), null,
                () => { PolicyCalls++; return new(null, null); }, () => { ProcessCalls++; return []; },
                (_, _) => { ListCalls++; return Task.FromResult(Tools); }, Files);
        }
        internal string Config(params string[] parts) => Path.Combine([Root, .. parts, ClaudeConfigFiles.Filename]);
        internal byte[] Seed(string path, string text)
        { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var bytes = Encoding.UTF8.GetBytes(text); File.WriteAllBytes(path, bytes); return bytes; }
        internal string Entry(bool extra = false)
        {
            var server = Server(Exe);
            if (extra) server["env"] = new JsonObject { ["TOKEN"] = "SYNTHETIC_ONLY" };
            return new JsonObject { ["mcpServers"] = new JsonObject { ["data"] = server } }.ToJsonString();
        }
        internal async Task<int?> Run(bool register = true, string key = "data", string url = Url)
        {
            Out.GetStringBuilder().Clear(); Error.GetStringBuilder().Clear();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await ProxyRegistration.TryHandleAsync(register ? ["--register", url, "--name", key] : ["--unregister", "--name", key], () => Context, Out, Error, timeout.Token);
        }
        internal string[] Backups() => Directory.GetFiles(Root, "*.bak", SearchOption.AllDirectories).Order().ToArray();
        internal string[] Temps() => Directory.GetFiles(Root, "*.tmp", SearchOption.AllDirectories).Order().ToArray();
        internal Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        internal void Unchanged(Dictionary<string, byte[]> snapshot)
        {
            var current = Snapshot(); Assert.Equal(snapshot.Keys.Order(), current.Keys.Order());
            foreach (var pair in snapshot) Assert.Equal(pair.Value, current[pair.Key]);
        }
        public void Dispose() { Out.Dispose(); Error.Dispose(); Directory.Delete(Root, recursive: true); }
    }

    private sealed class RecordingFiles : IRegistrationFiles
    {
        private readonly IRegistrationFiles inner = new RegistrationFiles();
        internal readonly List<(string Op, string Path, string? Destination)> Calls = [];
        internal Action<string, string, string?>? Before;
        internal int Mutations => Calls.Count(call => call.Op is "mkdir" or "copy" or "write" or "move" or "delete");
        private void Record(string op, string path, string? destination = null) { Calls.Add((op, path, destination)); Before?.Invoke(op, path, destination); }
        public FileAttributes? Attributes(string path) { Record("attributes", path); return inner.Attributes(path); }
        public IReadOnlyList<string> Directories(string path) { Record("directories", path); return inner.Directories(path); }
        public byte[] ReadAllBytes(string path) { Record("read", path); return inner.ReadAllBytes(path); }
        public void CreateDirectory(string path) { Record("mkdir", path); inner.CreateDirectory(path); }
        public void CopyFile(string source, string destination) { Record("copy", source, destination); inner.CopyFile(source, destination); }
        public void WriteNewFile(string path, byte[] content) { Record("write", path); inner.WriteNewFile(path, content); }
        public void MoveFile(string source, string destination, bool overwrite) { Record("move", source, destination); inner.MoveFile(source, destination, overwrite); }
        public void DeleteFile(string path) { Record("delete", path); inner.DeleteFile(path); }
    }
}
