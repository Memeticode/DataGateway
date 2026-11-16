using System.Text;
using System.Text.Json;

namespace DataGateway.Demo;

public sealed record StepResult(string Name, string Path, string? Request, DemoAnswer? Answer, IReadOnlyList<string> Problems, string? Skipped);
public sealed record DemoReport(IReadOnlyList<StepResult> Steps, string? Stopped)
{
    public bool Passed => Stopped is null && Steps.All(s => s.Problems.Count == 0);
}

public static class DemoRun
{
    public static async Task<DemoReport> RunAsync(HttpClient signedIn, HttpClient anonymous, string? source,
        TextWriter output, CancellationToken cancellationToken)
    {
        await output.WriteLineAsync("DataGateway: " + signedIn.BaseAddress);
        var steps = new List<StepResult>();
        var total = 11;
        async Task<StepResult> Send(string name, string path, string request, Func<DemoAnswer, IReadOnlyList<string>> check, HttpClient? client = null)
        {
            var answer = await SendAsync(client ?? signedIn, path, request, cancellationToken);
            var result = new StepResult(name, path, request, answer, check(answer), null);
            if (name == "list" && result.Problems.Count == 0)
            {
                using var json = JsonDocument.Parse(answer.Body);
                total = json.RootElement.GetProperty("sources").GetArrayLength() + 10;
            }
            steps.Add(result);
            await Print(output, result, steps.Count, total);
            return result;
        }
        async Task Skip(string name, string path, string reason)
        {
            var result = new StepResult(name, path, null, null, [], reason);
            steps.Add(result);
            await Print(output, result, steps.Count, total);
        }
        async Task<DemoReport> Finish(string? stopped = null)
        {
            var report = new DemoReport(steps.ToArray(), stopped);
            await output.WriteLineAsync($"Summary: {steps.Count} steps: {steps.Count(s => s.Skipped is null && s.Problems.Count == 0)} passed, {steps.Count(s => s.Problems.Count != 0)} failed, {steps.Count(s => s.Skipped is not null)} skipped.");
            if (stopped is not null) await output.WriteLineAsync("Stopped: " + stopped);
            await output.WriteLineAsync("Result: " + (report.Passed ? "PASS" : "FAIL"));
            return report;
        }

        var listed = await Send("list", "/sources/list", DemoPlan.List(), DemoChecks.List);
        if (listed.Problems.Count != 0) return await Finish("list failed; register a view: README, Get started, step 4");
        using var listJson = JsonDocument.Parse(listed.Answer!.Body);
        var names = listJson.RootElement.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("name").GetString()!).ToArray();
        var chosen = source is null ? names[0] : names.FirstOrDefault(n => n.Equals(source, StringComparison.OrdinalIgnoreCase));
        if (chosen is null) return await Finish("--source names no listed source");
        StepResult? selected = null;
        foreach (var name in names)
        {
            var described = await Send("describe " + name, "/sources/describe", DemoPlan.Describe(name), a => DemoChecks.Describe(a, name));
            if (name == chosen)
            {
                if (described.Problems.Count != 0) return await Finish("chosen source describe failed");
                selected = described;
            }
        }
        using var describeJson = JsonDocument.Parse(selected!.Answer!.Body);
        var columns = DemoPlan.Columns(describeJson.RootElement);
        var picks = DemoPlan.Pick(columns, DemoPlan.RowCap(describeJson.RootElement));
        if (picks.Select.Count == 0) return await Finish("chosen source has no selectable column");

        var detail = await Send("detail", "/query", DemoPlan.Detail(chosen, picks),
            a => DemoChecks.Rows(a, chosen, picks.Select, picks.RowCap, 3, ordered: false));
        JsonElement? filterValue = null;
        if (picks.Filter is not null && detail.Problems.Count == 0)
        {
            using var detailJson = JsonDocument.Parse(detail.Answer!.Body);
            filterValue = DemoPlan.FilterValue(detailJson.RootElement.GetProperty("rows"), picks.Filter);
        }
        if (picks.Filter is null) await Skip("filter", "/query", "no selectable, filterable number, text or bool column");
        else if (filterValue is null) await Skip("filter", "/query", "no non-null filter value in detail rows");
        else await Send("filter", "/query", DemoPlan.Filter(chosen, picks.Filter, filterValue.Value),
            a => DemoChecks.Rows(a, chosen, [picks.Filter], picks.RowCap, 3, picks.Filter, filterValue, ordered: false));

        if (picks.Group is null || picks.Aggregate is null)
            await Skip("group", "/query", picks.Group is null ? "no canGroup column" : "no canAggregate column");
        else
        {
            var count = new DemoColumn("count_" + picks.Aggregate.Name, "number", false, false, false, false, false);
            await Send("group", "/query", DemoPlan.Group(chosen, picks.Group, picks.Aggregate),
                a => DemoChecks.Rows(a, chosen, [picks.Group, count], picks.RowCap, 5, descendingCounts: picks.Aggregate.CanSort, ordered: picks.Aggregate.CanSort));
        }
        await Send("cap", "/query", DemoPlan.Cap(chosen, picks.Select[0]),
            a => DemoChecks.Rows(a, chosen, [picks.Select[0]], picks.RowCap, null, ordered: false));
        string unknown;
        do { unknown = "demo_unknown_source_" + Guid.NewGuid().ToString("N"); }
        while (names.Contains(unknown, StringComparer.OrdinalIgnoreCase));
        await Send("unknown source", "/sources/describe", DemoPlan.Describe(unknown),
            a => DemoChecks.Refusal(a, 404, "UnknownSource", "$.source", DemoChecks.UnknownSourceMessage(names)));
        do { unknown = "demo_unknown_column_" + Guid.NewGuid().ToString("N"); }
        while (columns.Any(c => c.Name.Equals(unknown, StringComparison.OrdinalIgnoreCase)) || names.Contains(unknown, StringComparer.OrdinalIgnoreCase));
        await Send("unknown column", "/query", DemoPlan.UnknownColumn(chosen, unknown),
            a => DemoChecks.Refusal(a, 400, "UnknownColumn", "$.query.select[0]"));
        if (picks.NotGroupable is null) await Skip("not groupable", "/query", "every queryable column is groupable");
        else await Send("not groupable", "/query", DemoPlan.NotGroupable(chosen, picks.NotGroupable),
            a => DemoChecks.Refusal(a, 400, "ColumnOperationNotAllowed", "$.query.group_by[0]", DemoChecks.GroupMessage));
        await Send("over cap", "/query", DemoPlan.OverCap(chosen, picks.Select[0], picks.RowCap),
            a => DemoChecks.Refusal(a, 400, "StructuralLimitExceeded", "$.query.limit"));
        await Send("anonymous", "/sources/list", DemoPlan.List(), DemoChecks.Anonymous, anonymous);
        return await Finish();
    }

    private static async Task<DemoAnswer> SendAsync(HttpClient client, string path, string body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Version = System.Net.HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request, token);
        return new((int)response.StatusCode, response.Content.Headers.ContentType?.ToString(),
            string.Join(", ", response.Headers.WwwAuthenticate), await response.Content.ReadAsStringAsync(token));
    }

    private static string Cut(string text) => text.Length <= 2000 ? text : text[..2000] + $"\u2026 ({text.Length - 2000} more characters)";
    private static async Task Print(TextWriter output, StepResult step, int index, int total)
    {
        await output.WriteLineAsync($"[{index}/{total}] {step.Name}");
        if (step.Skipped is not null) { await output.WriteLineAsync("SKIPPED: " + step.Skipped); return; }
        await output.WriteLineAsync("POST " + step.Path + " " + Cut(step.Request!));
        await output.WriteLineAsync($"-> {step.Answer!.Status} {Cut(step.Answer.Body)}");
        if (step.Problems.Count == 0) await output.WriteLineAsync("PASS");
        else foreach (var problem in step.Problems) await output.WriteLineAsync("FAIL: " + problem);
    }
}
