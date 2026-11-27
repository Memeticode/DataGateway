using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DataGateway.Core.Tests;
using DataGateway.Page;
using DataGateway.Routes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class BrowsePageTests
{
    private static readonly string[] Names = BrowsePage.Files.Select(file => file.Name).ToArray();
    private static string Url(string name, string prefix = "") => prefix + BrowsePage.Path + "/" + (name == "index.html" ? "" : name);
    private static string SourcePath(string name) => TestFiles.InRepo("DataGateway/Page/Browse/" + name);
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // The gateway a served page carries in its root element: its name and its routes' paths.
    private static JsonNode Gateway(string html)
    {
        var match = Regex.Match(html, "<html lang=\"en\" data-gateway=\"(?<value>[^\"]*)\">");
        Assert.True(match.Success, html);
        return JsonNode.Parse(WebUtility.HtmlDecode(match.Groups["value"].Value))!;
    }

    private static JsonNode Expected(string name, string? list, string? describe, string? query) =>
        new JsonObject { ["name"] = name, ["routes"] = new JsonObject { ["list"] = list, ["describe"] = describe, ["query"] = query } };

    // A source with every data type and a mix of flags: name and amount allow everything; day and active cannot be
    // aggregated; region can be grouped but not sorted or aggregated; note can only be selected and filtered.
    private static readonly (string Name, string Type, bool Sort, bool Group, bool Aggregate)[] Columns =
    [
        ("name", "text", true, true, true), ("amount", "number", true, true, true), ("day", "date", true, true, false),
        ("active", "bool", true, true, false), ("region", "text", false, true, false), ("note", "text", false, false, false)
    ];

    private static HostCatalogState Catalog() => HostCatalogState.Raw(
        _ => Task.FromResult<IReadOnlyList<dynamic>>([CatalogEngineTests.ListRow()]),
        (name, _) => Task.FromResult<(IReadOnlyList<dynamic>, IReadOnlyList<dynamic>)>(name == "synthetic"
            ? (Columns.Select(Row).ToArray(), [])
            : ([], [new Dictionary<string, object?> { ["name"] = "synthetic" }])));

    private static Dictionary<string, object?> Row((string Name, string Type, bool Sort, bool Group, bool Aggregate) column, int index) => new()
    {
        ["id"] = 7319, ["object_name"] = "hidden_schema.synthetic_view", ["connection_string"] = ErrorLogTests.SourceConnection,
        ["name"] = "synthetic", ["title"] = "Synthetic", ["description"] = "Synthetic", ["grain"] = "One row", ["row_cap"] = 500,
        ["query_timeout_seconds"] = 1, ["column_name"] = column.Name, ["ordinal"] = index + 1, ["column_title"] = column.Name,
        ["column_description"] = "Synthetic " + column.Type, ["data_type"] = column.Type, ["unit"] = null, ["magnitude"] = null,
        ["display_format"] = null, ["can_select"] = true, ["can_filter"] = true, ["can_sort"] = column.Sort,
        ["can_group"] = column.Group, ["can_aggregate"] = column.Aggregate
    };

    private static async Task<LoggedHost> Started()
    {
        var host = new LoggedHost(Catalog(), ErrorLogTests.Engine(), environment: "Development");
        await host.Start();
        return host;
    }

    private static async Task<string> Describe(LoggedHost host)
    {
        using var response = await host.Client.PostAsync("/sources/describe", Json("""{"source":"synthetic"}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("Development", true)]
    public async Task T1_page_files_are_served_from_the_build_only_in_development(string environment, bool enabled)
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = new LoggedHost(environment: environment);
        await host.Start();
        foreach (var (name, contentType) in BrowsePage.Files)
        {
            using var response = await host.Client.GetAsync(Url(name));
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (!enabled)
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Empty(bytes);
                continue;
            }
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(contentType, response.Content.Headers.ContentType?.ToString());
            Assert.Equal(BrowsePage.ContentSecurityPolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
            if (name != "index.html")
            {
                Assert.Equal(File.ReadAllBytes(SourcePath(name)), bytes);
                continue;
            }
            // The page is its file, with this gateway's name and route paths in the root element's placeholder.
            var html = Encoding.UTF8.GetString(bytes);
            Assert.True(JsonNode.DeepEquals(Expected("DataGateway.Web", "/sources/list", "/sources/describe", "/query"), Gateway(html)));
            Assert.Equal(File.ReadAllText(SourcePath(name)), Regex.Replace(html, "data-gateway=\"[^\"]*\"", BrowsePage.Placeholder));
        }
        using var redirects = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = host.Client.BaseAddress };
        using var redirect = await redirects.GetAsync(BrowsePage.Path);
        Assert.Equal(enabled ? HttpStatusCode.Redirect : HttpStatusCode.NotFound, redirect.StatusCode);
        if (enabled) Assert.Equal("browse-data-gateway/", redirect.Headers.Location?.OriginalString);
        Assert.Empty(host.Lines());
    }

    [Fact]
    public void T2_files_reach_only_this_server_and_never_write_markup()
    {
        Assert.Equal(Names.Order(StringComparer.Ordinal),
            Directory.GetFiles(SourcePath("")).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal));
        var html = File.ReadAllText(SourcePath("index.html"));
        var scripts = Names.Where(name => name.EndsWith(".js", StringComparison.Ordinal)).ToArray();
        // Everything the page loads is one of its own files, named relatively.
        var loaded = Regex.Matches(html, "\\b(?:src|href)\\s*=\\s*\"(?<path>[^\"]+)\"").Select(match => match.Groups["path"].Value)
            .Concat(scripts.SelectMany(name => Regex.Matches(File.ReadAllText(SourcePath(name)), "\\bfrom\\s+'\\./(?<path>[^']+)'")
                .Select(match => match.Groups["path"].Value)))
            .ToArray();
        Assert.NotEmpty(loaded);
        Assert.All(loaded, path => Assert.Contains(path, Names));
        // The content security policy refuses inline code and styles, so the page has none.
        Assert.DoesNotMatch("(?i)<script(?![^>]*\\bsrc=)|<style|\\sstyle\\s*=|\\son[a-z]+\\s*=", html);
        foreach (var name in Names)
        {
            var text = File.ReadAllText(SourcePath(name));
            Assert.DoesNotMatch("(?i)https?:", text);
            if (name.EndsWith(".css", StringComparison.Ordinal)) Assert.DoesNotMatch("(?i)@import|url\\(", text);
            Assert.DoesNotMatch("innerHTML|outerHTML|insertAdjacentHTML|document\\.write|\\beval\\(|new Function|XMLHttpRequest|WebSocket|EventSource|sendBeacon", text);
            Assert.Equal(name == "api.js" ? 1 : 0, Regex.Matches(text, "\\bfetch\\(").Count);
        }
        // Its one call goes to a route path the server wrote into the page, in the placeholder the page has once.
        Assert.Contains("fetch(gateway.routes[route],", File.ReadAllText(SourcePath("api.js")));
        Assert.Single(Regex.Matches(html, Regex.Escape(BrowsePage.Placeholder)));
    }

    [Fact]
    public async Task T3_every_request_the_form_builds_is_accepted_and_its_limits_are_the_servers()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = await Started();
        var describe = await Describe(host);
        var built = await Node(await host.Client.GetByteArrayAsync(Url("query.js")), Build, new { describe });
        using var schema = JsonDocument.Parse(describe);
        var querySchema = schema.RootElement.GetProperty("querySchema");

        // The form offers each column every operator and function querySchema allows for its type, and no other.
        foreach (var column in querySchema.GetProperty("columns").EnumerateArray())
        {
            var name = column.GetProperty("name").GetString()!;
            var type = column.GetProperty("dataType").GetString()!;
            Assert.Equal(Allowed(querySchema.GetProperty("filters").GetProperty("operators"), "op", type),
                Strings(built.GetProperty("operators").GetProperty(name)));
            Assert.Equal(column.GetProperty("canAggregate").GetBoolean(), built.GetProperty("functions").TryGetProperty(name, out var offered));
            if (column.GetProperty("canAggregate").GetBoolean())
                Assert.Equal(Allowed(querySchema.GetProperty("aggregates").GetProperty("functions"), "fn", type), Strings(offered));
        }
        Assert.Equal(querySchema.GetProperty("filters").GetProperty("maxGroupDepth").GetInt32(), built.GetProperty("depth").GetInt32());
        Assert.Equal(querySchema.GetProperty("filters").GetProperty("maxConditions").GetInt32(), built.GetProperty("conditions").GetInt32());
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"source":"synthetic","query":{"select":["name","amount","day","active","region","note"],"limit":10}}"""),
            JsonNode.Parse(built.GetProperty("cases")[0].GetProperty("body").GetRawText())));

        // DataGateway accepts all of them, and refuses only the two built by hand past the form's limits.
        var refused = 0;
        foreach (var request in built.GetProperty("cases").EnumerateArray())
        {
            var expected = request.GetProperty("status").GetInt32();
            using var response = await host.Client.PostAsync("/query", Json(request.GetProperty("body").GetRawText()));
            var text = await response.Content.ReadAsStringAsync();
            Assert.True((int)response.StatusCode == expected, $"{request.GetProperty("name").GetString()}: {(int)response.StatusCode} {text}");
            if (expected == 200) continue;
            refused++;
            using var error = JsonDocument.Parse(text);
            Assert.Equal("StructuralLimitExceeded", error.RootElement.GetProperty("code").GetString());
        }
        Assert.Equal(2, refused);
        Assert.Equal(refused, host.Lines().Length);
    }

    // Builds a request for each column with each of its operators, each grouping with each aggregate and every sort
    // it allows, the deepest groups and the most conditions the form offers, and each column's values list.
    private const string Build = """
        const described = JSON.parse(input.describe);
        const fresh = () => module.initialForm(described);
        const samples = { text: ['north', 'south'], number: ['1.5', '7'], date: ['2024-01-01', '2024-12-31'], bool: ['true', 'false'] };
        const cases = [];
        const add = (name, form, status = 200) => cases.push({ name, status, body: module.buildRequest(described, form).body });
        const operators = {};
        const functions = {};
        add('initial form', fresh());
        for (const column of module.filterable(described)) {
          operators[column.name] = module.operators(described, column).map(rule => rule.op);
          for (const op of operators[column.name]) {
            const form = fresh();
            module.addCondition(described, form, form.filters);
            const condition = form.filters.items[0];
            module.setColumn(described, condition, column.name);
            module.setOperator(described, condition, op);
            const [low, high] = samples[column.dataType];
            condition.value = { none: null, one: low, list: [low, high], range: [low, high] }[module.shapeOf(op)];
            add(`${column.name} ${op}`, form);
          }
        }
        for (const column of module.sortable(described)) {
          const form = fresh();
          module.addSort(described, form);
          Object.assign(form.rows.sort[0], { col: column.name, dir: 'desc' });
          add(`rows sorted by ${column.name}`, form);
        }
        for (const column of module.aggregatable(described)) {
          functions[column.name] = module.functions(described, column).map(rule => rule.fn);
          for (const group of module.groupable(described))
            for (const fn of functions[column.name]) {
              const form = fresh();
              form.mode = 'summary';
              module.setGroupBy(described, form, group.name, true);
              module.addAggregate(described, form);
              module.setAggregate(described, form, form.summary.aggregates[0], { col: column.name, fn });
              module.sortTargets(described, form).forEach(() => module.addSort(described, form));
              for (const sort of form.summary.sort) sort.dir = 'desc';
              add(`${fn} of ${column.name} by ${group.name}`, form);
            }
        }
        // The deepest groups the form offers, under a root that matches any; then one level more, built by hand.
        const deep = fresh();
        module.addCondition(described, deep, deep.filters);
        if (!module.canMatchAny(described, deep)) throw new Error('The root cannot match any.');
        deep.filters.match = 'any';
        let inner = deep.filters;
        while (module.canAddGroup(described, deep, inner)) {
          module.addGroup(described, deep, inner);
          inner = inner.items.at(-1);
        }
        add('deepest groups', deep);
        const deeper = structuredClone(deep);
        let last = deeper.filters;
        while (last.items.at(-1).kind === 'group') last = last.items.at(-1);
        last.items.push({ id: 10000, kind: 'group', match: 'all', items: [{ ...last.items[0], id: 10001 }] });
        add('one group deeper than the form offers', deeper, 400);
        // The most conditions the form offers; then one more, built by hand.
        const many = fresh();
        while (module.canAddCondition(described, many)) module.addCondition(described, many, many.filters);
        add('most conditions', many);
        const more = structuredClone(many);
        more.filters.items.push({ ...more.filters.items[0], id: 10002 });
        add('one condition more than the form offers', more, 400);
        for (const column of module.groupable(described))
          cases.push({ name: `values of ${column.name}`, status: 200, body: module.valuesRequest(described, column) });
        return { cases, operators, functions, depth: module.groupDepth(deep, inner), conditions: module.conditionCount(many.filters) };
        """;

    private static string[] Allowed(JsonElement rules, string member, string type) => rules.EnumerateArray()
        .Where(rule => rule.GetProperty("types").EnumerateArray().Any(each => each.GetString() == type))
        .Select(rule => rule.GetProperty(member).GetString()!).ToArray();

    private static string[] Strings(JsonElement values) => values.EnumerateArray().Select(value => value.GetString()!).ToArray();

    [Fact]
    public async Task T4_a_refusal_is_marked_on_the_field_that_wrote_its_path()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = await Started();
        var describe = await Describe(host);
        var module = await host.Client.GetByteArrayAsync(Url("query.js"));
        // A range whose low end is above its high end, in a group: the form sends it, and DataGateway refuses it.
        var built = await Node(module, """
            const described = JSON.parse(input.describe);
            const form = module.initialForm(described);
            module.addCondition(described, form, form.filters);
            module.addGroup(described, form, form.filters);
            const group = form.filters.items[1];
            const inner = group.items[0];
            module.setColumn(described, inner, 'amount');
            module.setOperator(described, inner, 'between');
            inner.value = ['9', '1'];
            const { body, fields } = module.buildRequest(described, form);
            return { body, fields: [...fields], group: `filters.${group.id}`, inner: `filters.${inner.id}` };
            """, new { describe });
        using var refusal = await host.Client.PostAsync("/query", Json(built.GetProperty("body").GetRawText()));
        Assert.Equal(HttpStatusCode.BadRequest, refusal.StatusCode);
        using var error = JsonDocument.Parse(await refusal.Content.ReadAsStringAsync());
        Assert.Equal("InvalidValue", error.RootElement.GetProperty("code").GetString());

        string[] paths = [error.RootElement.GetProperty("path").GetString()!, "$.query.filters[1].or[0].value[1]",
            "$.query.filters[1].or", "$.query.select[1]", "$.query.limit", "$.query", "$", "$.source"];
        var keys = await Node(module, "const fields = new Map(input.fields); return input.paths.map(path => module.fieldFor(fields, path));",
            new { fields = built.GetProperty("fields"), paths });
        var inner = built.GetProperty("inner").GetString()!;
        Assert.Equal([inner + ".value", inner + ".value", built.GetProperty("group").GetString(), "select.amount", "limit", null, null, null],
            keys.EnumerateArray().Select(key => key.ValueKind == JsonValueKind.Null ? null : key.GetString()));
        Assert.Equal("InvalidValue", Assert.Single(host.Lines()).GetProperty("Code").GetString());
    }

    [Fact]
    public async Task T5_edits_keep_the_form_valid_and_problems_name_their_fields()
    {
        using var settings = new ErrorLogSettingsScope();
        await using var host = await Started();
        var failures = await Node(await host.Client.GetByteArrayAsync(Url("query.js")), Edits, new { describe = await Describe(host) });
        Assert.Empty(failures.EnumerateArray().Select(failure => failure.GetString()));
        Assert.Empty(host.Lines());
    }

    // Each check records a failure when an edit leaves the form other than expected.
    private const string Edits = """
        const described = JSON.parse(input.describe);
        const failures = [];
        const expect = (label, actual, expected) => {
          if (JSON.stringify(actual) !== JSON.stringify(expected))
            failures.push(`${label}: ${JSON.stringify(actual)} is not ${JSON.stringify(expected)}`);
        };
        const fresh = () => module.initialForm(described);
        const column = name => module.columnNamed(described, name);
        const condition = (form, col, op) => {
          module.addCondition(described, form, form.filters);
          const added = form.filters.items.at(-1);
          module.setColumn(described, added, col);
          if (op !== undefined) module.setOperator(described, added, op);
          return added;
        };

        // Values carry across operator changes, shaped for each.
        let form = fresh();
        const amount = condition(form, 'amount');
        amount.value = '5';
        module.setOperator(described, amount, 'in');
        expect('one value to a list', amount.value, ['5']);
        module.setOperator(described, amount, 'between');
        expect('a list to a range', amount.value, ['5', '']);
        module.setOperator(described, amount, 'eq');
        expect('a range to one value', amount.value, '5');
        module.setOperator(described, amount, 'is_null');
        expect('to no value', amount.value, null);
        module.setOperator(described, amount, 'eq');
        expect('from no value', amount.value, '');

        // A new column keeps the operator where its type allows it, and starts its value afresh.
        const named = condition(form, 'name', 'contains');
        named.value = 'nor';
        module.setColumn(described, named, 'amount');
        expect('contains moved to a number', [named.op, named.value], ['eq', '']);
        module.setColumn(described, named, 'name');
        module.setOperator(described, named, 'neq');
        named.value = 'x';
        module.setColumn(described, named, 'region');
        expect('neq kept for text', [named.op, named.value], ['neq', '']);
        module.setColumn(described, named, 'active');
        expect('a bool starts true', named.value, 'true');

        // A values list fills a condition: one value replaces, a list adds without repeating.
        module.setColumn(described, named, 'region');
        module.setOperator(described, named, 'in');
        for (const value of ['west', 'east', 'west']) module.useValue(named, value);
        expect('a list of values', named.value, ['west', 'east']);
        module.setOperator(described, named, 'eq');
        module.useValue(named, 'north');
        expect('one value', named.value, 'north');
        expect('values offered', [module.offersValues(column('region'), 'eq'), module.offersValues(column('note'), 'eq'),
          module.offersValues(column('region'), 'contains')], [true, false, false]);

        // A new group matches the other way from its parent, and goes with its last condition.
        form = fresh();
        condition(form, 'name');
        module.addGroup(described, form, form.filters);
        const group = form.filters.items[1];
        expect('a new group matches any', group.match, 'any');
        module.removeFilter(form, group.items[0]);
        expect('an emptied group goes', form.filters.items.length, 1);

        // The root matches any only while that keeps every group within the depth limit.
        let inner = form.filters;
        while (module.canAddGroup(described, form, inner)) {
          module.addGroup(described, form, inner);
          inner = inner.items.at(-1);
        }
        expect('the root at full depth', module.canMatchAny(described, form), false);
        module.removeFilter(form, inner);
        expect('the root one level short', module.canMatchAny(described, form), true);

        // A summary sort follows its aggregate's new name, and goes with its group column or its aggregate.
        form = fresh();
        form.mode = 'summary';
        module.setGroupBy(described, form, 'name', true);
        module.addAggregate(described, form);
        const aggregate = form.summary.aggregates[0];
        module.setAggregate(described, form, aggregate, { col: 'amount' });
        module.addSort(described, form);
        module.addSort(described, form);
        expect('sort targets', form.summary.sort.map(sort => sort.col), ['name', 'count_amount']);
        module.setAggregate(described, form, aggregate, { fn: 'sum' });
        expect('the sort follows its aggregate', form.summary.sort.map(sort => sort.col), ['name', 'sum_amount']);
        module.setAggregate(described, form, aggregate, { col: 'name' });
        expect('a function text allows', [aggregate.fn, form.summary.sort.map(sort => sort.col)], ['count', ['name', 'count_name']]);
        module.setGroupBy(described, form, 'name', false);
        expect('the group sort goes', form.summary.sort.map(sort => sort.col), ['count_name']);
        module.removeAggregate(described, form, aggregate);
        expect('the aggregate sort goes', form.summary.sort, []);

        // Problems name the field to fix before the form sends anything.
        const problems = edit => {
          const each = fresh();
          edit(each);
          return [...module.problems(described, each)].map(([key, message]) => [key.replace(/\d+/, 'N'), message]);
        };
        const valueProblem = (col, op, value) => problems(each => {
          const added = condition(each, col, op);
          if (value !== undefined) added.value = value;
        });
        expect('a complete form', problems(() => {}), []);
        expect('no columns', problems(each => { each.rows.select = []; }), [['select', 'Choose at least one column.']]);
        expect('an empty summary', problems(each => { each.mode = 'summary'; }), [['group_by', 'Group by a column or add an aggregate.']]);
        expect('no number', valueProblem('amount', 'eq'), [['filters.N.value', 'Enter a number.']]);
        expect('half a date range', valueProblem('day', 'between', ['2024-01-01', '']), [['filters.N.value', 'Enter a date at both ends.']]);
        expect('an empty list', valueProblem('name', 'in'), [['filters.N.value', 'Add at least one value.']]);
        expect('a long list', valueProblem('name', 'in', Array.from({ length: 101 }, (_, index) => `v${index}`)),
          [['filters.N.value', 'Use at most 100 values.']]);
        expect('empty text is a value', valueProblem('name', 'eq'), []);
        for (const limit of ['0', '501', '2.5', 'ten'])
          expect(`limit ${limit}`, problems(each => { each.limit = limit; }).map(([key]) => key), ['limit']);
        for (const limit of ['', '1', '500']) expect(`limit ${limit}`, problems(each => { each.limit = limit; }), []);
        return failures;
        """;

    private static void Refusal(Action action, string name, string reason) =>
        Assert.Equal($"Gateway '{name}'. {reason}", Assert.Throws<InvalidOperationException>(action).Message);

    [Fact]
    public async Task T6_mapping_refusals_and_shared_routes_match_the_api_page()
    {
        Assert.Equal("endpoints",
            Assert.Throws<ArgumentNullException>(() => DataGatewayExtensions.MapDataGatewayBrowsePage(null!, "a", "/pages")).ParamName);
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        await using (var missing = new NamedGatewayHost())
        {
            foreach (var name in new string?[] { null, "", " ", "\t" })
                Assert.Equal("Gateway name must contain a non-whitespace character.",
                    Assert.Throws<InvalidOperationException>(() => missing.App.MapDataGatewayBrowsePage(name!, "/pages")).Message);
            Refusal(() => missing.App.MapDataGatewayBrowsePage("a", "/pages"), "a", "Register this gateway with AddDataGateway before mapping it.");
        }
        await using (var host = new NamedGatewayHost(builder => NamedGatewayHost.Register(builder.Services, "a", state)))
        {
            Refusal(() => host.App.MapDataGatewayBrowsePage("A", "/pages"), "A", "Register this gateway with AddDataGateway before mapping it.");
            foreach (var prefix in new string?[] { null, "/broken/{" })
                Refusal(() => host.App.MapDataGatewayBrowsePage("a", prefix!), "a", "Prefix must be a valid route pattern.");
            Assert.Empty(host.Endpoints);
            host.App.MapDataGatewayBrowsePage("a", "/pages");
            Refusal(() => host.App.MapDataGatewayBrowsePage("a", "/else"), "a", "This gateway's browse page has already been mapped.");
            Refusal(() => host.App.MapGroup("/parent").MapDataGatewayBrowsePage("a", "/other"), "a",
                "This gateway's browse page has already been mapped.");
            // The API page and the routes map apart from it, here under the same prefix as the browse page.
            host.App.MapDataGatewayApiPage("a", "/pages");
            host.App.MapDataGateway("a", "/pages");
            await host.Start();
            using var page = await host.Client.GetAsync(Url("index.html", "/pages"));
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            using var absent = await host.Client.GetAsync(Url("index.html", "/else"));
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        }
        // Two gateways' pages on one effective route fail when the endpoints are built, before anything is served.
        foreach (var mode in new[] { "case", "parameter", "parents" })
        {
            await using var host = new NamedGatewayHost(builder =>
            {
                NamedGatewayHost.Register(builder.Services, "a", state);
                NamedGatewayHost.Register(builder.Services, "b", state);
            });
            var (left, right) = mode switch
            {
                "case" => ("/shared", "/SHARED/"),
                "parameter" => ("/tenants/{tenant}/pages", "/tenants/{tenant}/pages"),
                _ => ("/pages", "/pages")
            };
            var parent = mode == "parents" ? "/outer" : "";
            host.App.MapGroup(parent).MapDataGatewayBrowsePage("a", left);
            host.App.MapGroup(parent).MapDataGatewayBrowsePage("b", right);
            var error = Assert.Throws<InvalidOperationException>(() => _ = host.Endpoints);
            string Route(string prefix) => parent + prefix.TrimEnd('/') + BrowsePage.Path + "/";
            Assert.Contains(error.Message, new[]
            {
                $"Gateway 'b'. Browse page route '{Route(right)}' is already mapped by gateway 'a'.",
                $"Gateway 'a'. Browse page route '{Route(left)}' is already mapped by gateway 'b'."
            });
        }
    }

    [Fact]
    public async Task T7_the_page_carries_its_gateways_route_paths_wherever_they_are_mapped()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        // A name that needs escaping in JSON and in an HTML attribute; a path base; routes apart from the page, under a
        // route value the page's request shares.
        const string name = "north \"<&>\" 'south'";
        await using (var host = new NamedGatewayHost(builder => NamedGatewayHost.Register(builder.Services, name, state),
            app => { app.UsePathBase("/base"); app.UseRouting(); }))
        {
            host.App.MapDataGateway(name, "/tenants/{tenant}/api");
            host.App.MapDataGatewayBrowsePage(name, "/tenants/{tenant}/tools");
            await host.Start();
            var gateway = Gateway(await host.Client.GetStringAsync(Url("index.html", "/base/tenants/acme/tools")));
            var api = "/base/tenants/acme/api";
            Assert.True(JsonNode.DeepEquals(Expected(name, api + "/sources/list", api + "/sources/describe", api + "/query"), gateway),
                gateway.ToJsonString());
            using var listed = await host.Client.PostAsync(gateway["routes"]!["list"]!.GetValue<string>(), Json("{}"));
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        }
        // Routes the page cannot reach: none mapped, or mapped under a route value the page's request does not have.
        foreach (var routes in new string?[] { null, "/tenants/{tenant}/api" })
        {
            await using var host = new NamedGatewayHost(builder => NamedGatewayHost.Register(builder.Services, "a", state));
            if (routes is not null) host.App.MapDataGateway("a", routes);
            host.App.MapDataGatewayBrowsePage("a", "/tools");
            await host.Start();
            Assert.True(JsonNode.DeepEquals(Expected("a", null, null, null),
                Gateway(await host.Client.GetStringAsync(Url("index.html", "/tools")))));
        }
    }

    [Fact]
    public async Task T8_policy_on_the_returned_or_a_parent_group_covers_every_page_file()
    {
        foreach (var parentPolicy in new[] { false, true })
        {
            using var fixture = new SqliteFixture();
            var state = new NamedGatewayState(fixture);
            await using var host = new NamedGatewayHost(builder => NamedGatewayHost.Register(builder.Services, "private", state));
            host.App.MapDataGateway("private", "/private-data");
            if (parentPolicy)
                host.App.MapGroup("/parent").RequireAuthorization().MapDataGatewayBrowsePage("private", "/private");
            else
                host.App.MapDataGatewayBrowsePage("private", "/parent/private").RequireAuthorization();
            await host.Start();
            using var unsigned = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = host.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(15) };
            using var signed = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = host.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(15) };
            signed.DefaultRequestHeaders.Add("X-Synthetic-User", "reader");
            var redirect = "/parent/private" + BrowsePage.Path;
            foreach (var path in Names.Select(file => Url(file, "/parent/private")).Append(redirect))
            {
                using var refused = await unsigned.GetAsync(path);
                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
                using var allowed = await signed.GetAsync(path);
                Assert.Equal(path == redirect ? HttpStatusCode.Redirect : HttpStatusCode.OK, allowed.StatusCode);
            }
            // Every page endpoint keeps the policy, and the gateway's error boundary knows it as the browse operation.
            var endpoints = host.Endpoints.Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/parent/private", StringComparison.Ordinal)).ToArray();
            Assert.Equal(Names.Length, endpoints.Length);
            foreach (var endpoint in endpoints)
            {
                Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
                Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
                Assert.Equal("browse", endpoint.Metadata.GetMetadata<GatewayOperation>()?.Name);
                Assert.Equal("private", endpoint.Metadata.GetMetadata<GatewayRegistration>()?.Name);
            }
            Assert.Empty(host.Logs.Failures);
        }
    }

    // Runs body as an async function of input and module (the page file, imported as served) and returns its result.
    private static async Task<JsonElement> Node(byte[] module, string body, object input)
    {
        var script = $$"""
            let text = '';
            for await (const chunk of process.stdin) text += chunk;
            const input = JSON.parse(text);
            const module = await import('data:text/javascript;base64,' + input.module);
            const result = await (async () => { {{body}} })();
            process.stdout.write(JSON.stringify(result));
            """;
        var start = new ProcessStartInfo("node") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--input-type=module");
        start.ArgumentList.Add("--eval");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            var arguments = JsonSerializer.SerializeToNode(input)!.AsObject();
            arguments["module"] = Convert.ToBase64String(module);
            await process.StandardInput.WriteAsync(arguments.ToJsonString());
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, await errors);
            using var result = JsonDocument.Parse(await output);
            return result.RootElement.Clone();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
