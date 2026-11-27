using System.Text;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Queries;
using DataGateway.Data.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace DataGateway.Web.Tests;

/// <summary>The row-for-row proof on SQL Server LocalDB: the same queries through the real SqlServerEngine and QueryRunner
/// in four passes. Empty: every store_type empty. Filled: the view's exact types. Mismatch: a deliberately wrong type on
/// every column, which must bind as the empty pass. Override: the exact types except one nvarchar column cataloged as
/// varchar with store_type_override set. Every case asserts its exact ids in every pass; the cases the database refuses
/// are listed apart and assert their failure. Rows differ between passes only where STORE-TYPES.md says they do: a
/// varchar or char LIKE ignores the column's trailing blanks once the pattern is varchar too, and a SQL_ collation
/// compares varchar with varchar by non-Unicode rules. Plans: the filled binding seeks without converting the column,
/// and one plan serves each query shape.</summary>
/// <remarks>The empty pass is this build's store-type-free binding, IN padding included, not a run of the preview.6
/// binaries: it shows what a column with an empty store_type does now, which is the binding the other passes are
/// compared with.</remarks>
public sealed class StoreTypeProofLocalDbTests(ITestOutputHelper output)
{
    private static readonly string X4000 = new('x', 4000), X5000 = new('x', 5000);

    // Every filtered column has its own index except nv4000: a 4000-character nvarchar key (8000 bytes) is over SQL
    // Server's 1700-byte index key limit, so that column is filtered without one.
    private const string Setup = """
        CREATE TABLE dbo.proof (
            id int NOT NULL CONSTRAINT PK_proof PRIMARY KEY CLUSTERED,
            v20 varchar(20) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
            nv4000 nvarchar(4000) NULL,
            nv100 nvarchar(100) NULL,
            c10 char(10) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
            nc5 nchar(5) NULL,
            u8 varchar(40) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
            i int NULL,
            bi bigint NULL,
            d decimal(19,4) NULL,
            dt datetime NULL,
            dd date NULL,
            b bit NULL);
        INSERT INTO dbo.proof (id, v20, nv4000, nv100, c10, nc5, u8, i, bi, d, dt, dd, b) VALUES
            (1, 'abc',    NULL, N'abc',    'abc',        N'ab',    'abc',     1,   3000000000, 1.0000,  '2024-01-02T00:00:00.003', '2024-01-02', 1),
            (2, 'abc  ',  NULL, N'Z' + NCHAR(252) + N'rich', 'ABCDEFGHIJ', N'abcde', N'Z' + NCHAR(252) + N'rich', 2,   2,          2.5000,  '2024-01-01T23:59:59.997', '2024-01-01', 0),
            (3, N'Z' + NCHAR(252) + N'rich', NULL, NCHAR(955),      'xyz',        REPLICATE(NCHAR(955), 3),   NCHAR(955),      3,   3,          19.7500, '2024-01-03T06:00:00.000', '2024-01-03', 1),
            (4, '?',      NULL, N'?',      '?',          N'?',     '?',       100, 100,        0.5000,  NULL, NULL, NULL),
            (5, NCHAR(955),     NULL, NULL,      NCHAR(955),         NULL,     NULL,      NULL, NULL,      NULL,    NULL, NULL, NULL),
            (6, 'xx',     REPLICATE(CONVERT(nvarchar(max), N'x'), 4000), NULL, 'AB_C', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
            (7, 'AB%',    NULL, NULL,      'abc  ',      NULL,     NULL,      7,   7,          7.0000,  NULL, NULL, NULL),
            (8, N'Stra' + NCHAR(223) + N'e', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
        WITH n AS (SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT 0)) AS k FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b)
        INSERT INTO dbo.proof (id, v20, i) SELECT 999 + k, 'f' + CAST(999 + k AS varchar(10)), 999 + k FROM n;
        CREATE INDEX IX_v20 ON dbo.proof (v20);
        CREATE INDEX IX_nv100 ON dbo.proof (nv100);
        CREATE INDEX IX_c10 ON dbo.proof (c10);
        CREATE INDEX IX_nc5 ON dbo.proof (nc5);
        CREATE INDEX IX_u8 ON dbo.proof (u8);
        CREATE INDEX IX_i ON dbo.proof (i);
        CREATE INDEX IX_bi ON dbo.proof (bi);
        CREATE INDEX IX_d ON dbo.proof (d);
        CREATE INDEX IX_dt ON dbo.proof (dt);
        CREATE INDEX IX_dd ON dbo.proof (dd);
        CREATE INDEX IX_b ON dbo.proof (b);
        """;

    // Row 5's v20 and c10 hold '?': the column's code page has no lambda. Row 8's v20 holds 'Stra' + U+00DF + 'e'.
    private static readonly int[] Filler = Enumerable.Range(1000, 2000).ToArray();

    private static readonly (string Column, string DataType, string Exact, string Wrong)[] Columns =
    [
        ("v20", "text", "varchar(20)", "varchar(21)"),
        ("nv4000", "text", "nvarchar(4000)", "nvarchar(3999)"),
        ("nv100", "text", "nvarchar(100)", "nvarchar(101)"),
        ("c10", "text", "char(10)", "char(11)"),
        ("nc5", "text", "nchar(5)", "nchar(6)"),
        ("u8", "text", "varchar(40)", "varchar(41)"),
        ("i", "number", "int", "bigint"),
        ("bi", "number", "bigint", "int"),
        ("d", "number", "decimal(19,4)", "decimal(19,3)"),
        ("dt", "date", "datetime", "datetime2(3)"),
        ("dd", "date", "date", "datetime"),
        ("b", "bool", "bit", "tinyint"),
    ];

    // The override pass: every exact type, except nv4000 cataloged as varchar(4000) with store_type_override set.
    private const string OverrideColumn = "nv4000", OverrideType = "varchar(4000)";

    private static string Filter(string column, string op, string value) =>
        $$"""{"select":["id"],"filters":[{"col":"{{column}}","op":"{{op}}","value":{{value}}}]}""";

    private static string Strings(IEnumerable<string> values) => "[" + string.Join(",", values.Select(value => "\"" + value + "\"")) + "]";

    private static int[] Ids(params int[] ids) => ids;

    private static int[] Ids(IEnumerable<int> ids, params int[] more) => ids.Concat(more).Order().ToArray();

    private static int[] Except(IEnumerable<int> ids, params int[] less) => ids.Except(less).Order().ToArray();

    /// <summary>One query and its exact ids in each pass. Filled defaults to the empty pass's ids and override to the
    /// filled pass's; the mismatch pass must always equal the empty one.</summary>
    private sealed record Case(string Name, string Query, int[] Empty, int[]? Filled = null, int[]? Override = null, string Why = "")
    {
        internal int[] FilledIds => Filled ?? Empty;
        internal int[] OverrideIds => Override ?? FilledIds;
    }

    private const string Blanks = "LIKE ignores trailing blanks";
    private const string SqlCollation = "SQL_ collation, non-Unicode rules";

    private static readonly Case[] Cases =
    [
        new("v20 eq 'abc'", Filter("v20", "eq", "\"abc\""), Ids(1, 2)),
        new("v20 eq 'Z\u00fcrich'", Filter("v20", "eq", "\"Z\u00fcrich\""), Ids(3)),
        new("v20 eq '\u03bb'", Filter("v20", "eq", "\"\u03bb\""), Ids()),
        new("v20 eq '?'", Filter("v20", "eq", "\"?\""), Ids(4, 5)),
        new("v20 neq 'abc'", Filter("v20", "neq", "\"abc\""), Ids(Filler, 3, 4, 5, 6, 7, 8)),
        new("v20 in 1", Filter("v20", "in", Strings(["abc"])), Ids(1, 2)),
        new("v20 in 6", Filter("v20", "in", Strings(["abc", "xx", "AB%", "?", "f1000", "nope"])), Ids(1, 2, 4, 5, 6, 7, 1000)),
        new("v20 in 11 (with Z\u00fcrich)", Filter("v20", "in", Strings(["abc", "xx", "AB%", "?", "Z\u00fcrich", "f1000", "f1001", "f1002", "f1003", "f1004", "nope"])),
            Ids(1, 2, 3, 4, 5, 6, 7, 1000, 1001, 1002, 1003, 1004)),
        new("v20 in 100", Filter("v20", "in", Strings(Enumerable.Range(1000, 100).Select(n => "f" + n))), Ids(Enumerable.Range(1000, 100))),
        new("v20 not_in 6", Filter("v20", "not_in", Strings(["abc", "xx", "AB%", "?", "f1000", "nope"])), Except(Ids(Filler, 3, 8), 1000)),
        new("v20 contains 'bc'", Filter("v20", "contains", "\"bc\""), Ids(1, 2)),
        new("v20 contains '%'", Filter("v20", "contains", "\"%\""), Ids(7)),
        new("v20 like 'ab%' (ends in %)", Filter("v20", "like", "\"ab%\""), Ids(1, 2, 7)),
        new("v20 like 'abc' (ends in letters)", Filter("v20", "like", "\"abc\""), Ids(1), Filled: Ids(1, 2), Why: Blanks),
        new("v20 like '%abc' (trailing blanks)", Filter("v20", "like", "\"%abc\""), Ids(1), Filled: Ids(1, 2), Why: Blanks),
        new("v20 like 'Z\u00fcr%'", Filter("v20", "like", "\"Z\u00fcr%\""), Ids(3)),
        // The second visible difference: under a SQL_ collation Unicode rules equate U+00DF with 'ss', non-Unicode rules do not.
        new("v20 eq 'Strasse' (stored Stra\u00dfe)", Filter("v20", "eq", "\"Strasse\""), Ids(8), Filled: Ids(), Why: SqlCollation),
        new("v20 neq 'Strasse' (stored Stra\u00dfe)", Filter("v20", "neq", "\"Strasse\""), Ids(Filler, 1, 2, 3, 4, 5, 6, 7),
            Filled: Ids(Filler, 1, 2, 3, 4, 5, 6, 7, 8), Why: SqlCollation),
        new("v20 in ['Strasse'] (stored Stra\u00dfe)", Filter("v20", "in", Strings(["Strasse"])), Ids(8), Filled: Ids(), Why: SqlCollation),
        new("c10 eq 'abc'", Filter("c10", "eq", "\"abc\""), Ids(1, 7)),
        new("c10 eq 'ABCDEFGHIJ' (exact length)", Filter("c10", "eq", "\"ABCDEFGHIJ\""), Ids(2)),
        new("c10 like 'abc%'", Filter("c10", "like", "\"abc%\""), Ids(1, 2, 7)),
        new("c10 like '%abc' (padded char)", Filter("c10", "like", "\"%abc\""), Ids(), Filled: Ids(1, 7), Why: Blanks),
        new("c10 like 'AB_C' (literal _)", Filter("c10", "like", "\"AB_C\""), Ids(), Filled: Ids(6), Why: Blanks),
        new("nc5 eq 'ab'", Filter("nc5", "eq", "\"ab\""), Ids(1)),
        new("nc5 eq '\u03bb\u03bb\u03bb'", Filter("nc5", "eq", "\"\u03bb\u03bb\u03bb\""), Ids(3)),
        new("nc5 like '%ab' (Unicode either way)", Filter("nc5", "like", "\"%ab\""), Ids()),
        new("nv100 eq 'Z\u00fcrich'", Filter("nv100", "eq", "\"Z\u00fcrich\""), Ids(2)),
        new("nv100 contains 'ri'", Filter("nv100", "contains", "\"ri\""), Ids(2)),
        new("nv100 eq '\u03bb'", Filter("nv100", "eq", "\"\u03bb\""), Ids(3)),
        new("nv4000 eq 4000 x's", Filter("nv4000", "eq", "\"" + X4000 + "\""), Ids(6)),
        // The override pass binds these as varchar: 5000 characters go as varchar(max), so nothing is cut to 4000.
        new("nv4000 eq 5000 x's (longer than the column)", Filter("nv4000", "eq", "\"" + X5000 + "\""), Ids()),
        new("nv4000 contains 'xxx'", Filter("nv4000", "contains", "\"xxx\""), Ids(6)),
        new("u8 eq 'Z\u00fcrich' (UTF8)", Filter("u8", "eq", "\"Z\u00fcrich\""), Ids(2)),
        new("u8 eq 'abc' (UTF8)", Filter("u8", "eq", "\"abc\""), Ids(1)),
        new("u8 eq '\u03bb' (UTF8)", Filter("u8", "eq", "\"\u03bb\""), Ids(3)),
        new("i eq 2", Filter("i", "eq", "2"), Ids(2)),
        new("i neq 2", Filter("i", "neq", "2"), Ids(Filler, 1, 3, 4, 7)),
        new("i between 2..100", Filter("i", "between", "[2,100]"), Ids(2, 3, 4, 7)),
        new("i lt 3", Filter("i", "lt", "3"), Ids(1, 2)),
        new("i gt 2.5 (fractional)", Filter("i", "gt", "2.5"), Ids(Filler, 3, 4, 7)),
        new("i eq 3000000000 (out of int)", Filter("i", "eq", "3000000000"), Ids()),
        new("i in 6", Filter("i", "in", "[1,2,3,100,1000,5]"), Ids(1, 2, 3, 4, 1000)),
        new("i in 11", Filter("i", "in", "[1,2,3,100,1000,1001,1002,1003,1004,1005,5]"), Ids(1, 2, 3, 4, 1000, 1001, 1002, 1003, 1004, 1005)),
        new("i not_in 11", Filter("i", "not_in", "[1,2,3,100,1000,1001,1002,1003,1004,1005,5]"), Except(Ids(Filler, 7), 1000, 1001, 1002, 1003, 1004, 1005)),
        // 2050 values and TOP: 2051 parameters. Padded to 2100 values it would be 2101, which SQL Server refuses.
        new("i in 2050 (not padded past 2098)", Filter("i", "in", "[" + string.Join(",", Enumerable.Range(1000, 2050)) + "]"), Filler),
        new("bi eq 3000000000", Filter("bi", "eq", "3000000000"), Ids(1)),
        new("d eq 2.5", Filter("d", "eq", "2.5"), Ids(2)),
        new("d eq 1 (whole)", Filter("d", "eq", "1"), Ids(1)),
        new("d between 1..20", Filter("d", "between", "[1,20]"), Ids(1, 2, 3, 7)),
        new("d gt 2", Filter("d", "gt", "2"), Ids(2, 3, 7)),
        new("dt eq 2024-01-02 (.003 stored)", Filter("dt", "eq", "\"2024-01-02\""), Ids()),
        new("dt gte 2024-01-02", Filter("dt", "gte", "\"2024-01-02\""), Ids(1, 3)),
        new("dt lt 2024-01-02 (.997 stored)", Filter("dt", "lt", "\"2024-01-02\""), Ids(2)),
        new("dt between 2024-01-01..2024-01-02", Filter("dt", "between", "[\"2024-01-01\",\"2024-01-02\"]"), Ids(2)),
        new("dd eq 2024-01-02", Filter("dd", "eq", "\"2024-01-02\""), Ids(1)),
        new("dd between 2024-01-01..2024-01-02", Filter("dd", "between", "[\"2024-01-01\",\"2024-01-02\"]"), Ids(1, 2)),
        new("b eq true", Filter("b", "eq", "true"), Ids(1, 3)),
    ];

    // The queries the database refuses in every pass, and how. 2100 values and TOP are 2101 parameters, one over SQL
    // Server's limit (error 8003); padding never causes this, the list is that long as sent. A contains of 4001
    // characters is a LIKE pattern of 4003 characters, over 8000 bytes once compared with an nvarchar column, whichever
    // type it is sent as (error 8152).
    private static readonly (string Name, string Query, string Failure)[] Refused =
    [
        ("i in 2100 (over the parameter limit as sent)", Filter("i", "in", "[" + string.Join(",", Enumerable.Range(1000, 2100)) + "]"), "SourceUnavailable 8003"),
        ("nv4000 contains 4001 x's (pattern over 8000 bytes)", Filter("nv4000", "contains", "\"" + new string('x', 4001) + "\""), "SourceUnavailable 8152"),
    ];

    [LocalDbFact]
    public async Task T1_each_pass_returns_its_exact_rows_and_the_filled_plans_seek_and_are_shared()
    {
        await using var scratch = await StoreTypeLocalDbTests.Scratch.CreateAsync();
        await scratch.ExecuteAsync(Setup);
        await scratch.ExecuteAsync("CREATE VIEW dbo.vproof AS SELECT id, v20, nv4000, nv100, c10, nc5, u8, i, bi, d, dt, dd, b FROM dbo.proof;");
        var empty = Pass(scratch, Columns.Select(column => (column.Column, column.DataType, (string?)null, false)));
        var filled = Pass(scratch, Columns.Select(column => (column.Column, column.DataType, (string?)column.Exact, false)));
        var wrong = Pass(scratch, Columns.Select(column => (column.Column, column.DataType, (string?)column.Wrong, false)));
        var overridden = Pass(scratch, Columns.Select(column => column.Column == OverrideColumn
            ? (column.Column, column.DataType, (string?)OverrideType, true)
            : (column.Column, column.DataType, (string?)column.Exact, false)));
        Runner[] passes = [empty, filled, wrong, overridden];

        var table = new StringBuilder();
        table.AppendLine($"{"query",-44} | {"empty",-12} | {"filled",-12} | {"mismatch",-12} | {"override",-12} | expected difference");
        var results = new List<(Case Case, Outcome[] Passes)>();
        foreach (var query in Cases)
        {
            var outcomes = new Outcome[passes.Length];
            for (var pass = 0; pass < passes.Length; pass++) outcomes[pass] = await passes[pass].TryRunAsync(query.Query);
            results.Add((query, outcomes));
            table.AppendLine($"{query.Name,-44} | {string.Join(" | ", outcomes.Select(outcome => $"{outcome,-12}"))} | " +
                (query.Why.Length == 0 ? "none" : query.Why));
        }
        var refusals = new List<(string Name, string Failure, Outcome[] Passes)>();
        foreach (var (name, query, failure) in Refused)
        {
            var outcomes = new Outcome[passes.Length];
            for (var pass = 0; pass < passes.Length; pass++) outcomes[pass] = await passes[pass].TryRunAsync(query);
            refusals.Add((name, failure, outcomes));
            table.AppendLine($"{name,-44} | {string.Join(" | ", outcomes.Select(outcome => $"{outcome,-12}"))} | refused in every pass");
        }
        output.WriteLine(table.ToString());

        // The mismatched catalog binds as an empty store_type: every column reported once as an Error. The override is
        // reported once as a Warning and binds as the catalog says.
        Assert.Equal(Columns.Length, wrong.Findings.Count(finding => finding.Severity == StoreTypeSeverity.Error));
        Assert.Equal(Columns.Length, wrong.Findings.Count);
        Assert.Empty(filled.Findings);
        Assert.Empty(empty.Findings);
        var warning = Assert.Single(overridden.Findings);
        Assert.Equal((StoreTypeSeverity.Warning, OverrideColumn), (warning.Severity, warning.ColumnName));
        Assert.All(results, result =>
        {
            var (query, outcomes) = result;
            Assert.True(outcomes.All(outcome => outcome.Failure is null), $"{query.Name}: refused ({string.Join(", ", outcomes.Select(o => o.ToString()))}).");
            Assert.True(query.Empty.SequenceEqual(outcomes[0].Ids!), $"{query.Name}: empty {outcomes[0]}, expected {Show(query.Empty)}.");
            Assert.True(query.FilledIds.SequenceEqual(outcomes[1].Ids!), $"{query.Name}: filled {outcomes[1]}, expected {Show(query.FilledIds)}.");
            Assert.True(query.Empty.SequenceEqual(outcomes[2].Ids!), $"{query.Name}: mismatch {outcomes[2]}, expected the empty pass's {Show(query.Empty)}.");
            Assert.True(query.OverrideIds.SequenceEqual(outcomes[3].Ids!), $"{query.Name}: override {outcomes[3]}, expected {Show(query.OverrideIds)}.");
            // A case whose passes differ says why: only the documented differences may.
            Assert.True(query.Why.Length > 0 || (query.Empty.SequenceEqual(query.FilledIds) && query.FilledIds.SequenceEqual(query.OverrideIds)),
                $"{query.Name}: passes differ without a documented reason.");
        });
        Assert.All(refusals, refusal => Assert.All(refusal.Passes, outcome => Assert.Equal(refusal.Failure, outcome.Failure)));

        // Plans for the filled binding: seeks with no conversion of the column, one plan per query shape. LocalDB
        // databases close when their last connection does (AUTO_CLOSE), which empties their plan cache.
        await scratch.ExecuteAsync("ALTER DATABASE CURRENT SET AUTO_CLOSE OFF;");
        await scratch.ExecuteAsync("ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;");
        foreach (var value in new[] { "a", "abc", "abcdefgh" }) await filled.RunAsync(Filter("v20", "eq", $"\"{value}\""));
        foreach (var value in new[] { 2, 3, 100 }) await filled.RunAsync(Filter("i", "eq", value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        for (var count = 6; count <= 10; count++)
            await filled.RunAsync(Filter("i", "in", "[" + string.Join(",", Enumerable.Range(1, count)) + "]"));
        var plans = await Plans(scratch);
        var v20 = Assert.Single(plans, plan => plan.Text.Contains("WHERE [v20] = @p1", StringComparison.Ordinal));
        var integer = Assert.Single(plans, plan => plan.Text.Contains("WHERE [i] = @p1", StringComparison.Ordinal));
        var membership = Assert.Single(plans, plan => plan.Text.Contains("WHERE [i] IN (@p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10)", StringComparison.Ordinal));
        output.WriteLine($"filled v20 eq: {v20.UseCounts} uses of one plan; {Declared(v20.Text)}; ops {Ops(v20.Xml)}");
        output.WriteLine($"filled i eq: {integer.UseCounts} uses of one plan; ops {Ops(integer.Xml)}");
        output.WriteLine($"filled i in 6..10: {membership.UseCounts} uses of one plan");
        output.WriteLine($"filled conversions (v20 eq, i eq): {string.Join("; ", Conversions(v20.Xml).Concat(Conversions(integer.Xml)).Distinct())}");
        Assert.Contains("@p1 varchar(20)", v20.Text, StringComparison.Ordinal);
        Assert.Equal(3, v20.UseCounts);
        Assert.Equal(3, integer.UseCounts);
        Assert.Equal(5, membership.UseCounts);
        foreach (var plan in new[] { v20, integer })
        {
            Assert.Contains("PhysicalOp=\"Index Seek\"", plan.Xml, StringComparison.Ordinal);
            Assert.DoesNotContain(Conversions(plan.Xml), conversion => conversion.Contains("[v20]", StringComparison.Ordinal) || conversion.Contains("[i]", StringComparison.Ordinal));
        }
        Assert.Contains("[IX_v20]", v20.Xml, StringComparison.Ordinal);
        Assert.Contains("[IX_i]", integer.Xml, StringComparison.Ordinal);

        // Recorded, not asserted: what the store-type-free nvarchar binding does to the SQL_ collation varchar column.
        await scratch.ExecuteAsync("ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;");
        foreach (var value in new[] { "a", "abc", "abcdefgh" }) await empty.RunAsync(Filter("v20", "eq", $"\"{value}\""));
        var before = (await Plans(scratch)).Where(plan => plan.Text.Contains("WHERE [v20] = @p1", StringComparison.Ordinal)).ToArray();
        foreach (var plan in before)
            output.WriteLine($"empty v20 eq: {plan.UseCounts} use(s); {Declared(plan.Text)}; " +
                $"ops {Ops(plan.Xml)}; conversions {string.Join("; ", Conversions(plan.Xml).Distinct())}");
        output.WriteLine($"empty v20 eq: {before.Length} plan(s) for 3 value lengths");
    }

    private static string Declared(string text) => text[..text.IndexOf("SELECT", StringComparison.Ordinal)];

    /// <summary>Each CONVERT_IMPLICIT(...) in a plan's scalar strings, with its balanced parentheses.</summary>
    private static List<string> Conversions(string xml)
    {
        var found = new List<string>();
        for (var at = xml.IndexOf("CONVERT_IMPLICIT(", StringComparison.Ordinal); at >= 0; at = xml.IndexOf("CONVERT_IMPLICIT(", at + 1, StringComparison.Ordinal))
        {
            var end = at + "CONVERT_IMPLICIT(".Length;
            for (var depth = 1; depth > 0 && end < xml.Length; end++) depth += xml[end] == '(' ? 1 : xml[end] == ')' ? -1 : 0;
            found.Add(xml[at..end]);
        }
        return found;
    }

    private static string Show(int[] ids) => ids.Length > 6 ? $"{ids.Length} rows" : "[" + string.Join(",", ids) + "]";

    private static string Ops(string xml) => string.Join(",", System.Text.RegularExpressions.Regex.Matches(xml, "PhysicalOp=\"([^\"]+)\"")
        .Select(match => match.Groups[1].Value).Distinct());

    private static async Task<List<(string Text, int UseCounts, string Xml)>> Plans(StoreTypeLocalDbTests.Scratch scratch)
    {
        await using var session = new SqlConnection(scratch.Connection);
        await session.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT st.[text], cp.[usecounts], CONVERT(nvarchar(max), qp.[query_plan])
            FROM sys.dm_exec_cached_plans AS cp
            CROSS APPLY sys.dm_exec_sql_text(cp.[plan_handle]) AS st
            CROSS APPLY sys.dm_exec_query_plan(cp.[plan_handle]) AS qp
            CROSS APPLY sys.dm_exec_plan_attributes(cp.[plan_handle]) AS pa
            WHERE pa.[attribute] = N'dbid' AND CONVERT(int, pa.[value]) = DB_ID() AND st.[text] LIKE N'%FROM \[dbo\].\[vproof\]%' ESCAPE N'\';
            """, session);
        await using var reader = await command.ExecuteReaderAsync();
        var plans = new List<(string, int, string)>();
        while (await reader.ReadAsync())
            plans.Add((reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? "" : reader.GetString(2)));
        return plans;
    }

    private static Runner Pass(StoreTypeLocalDbTests.Scratch scratch,
        IEnumerable<(string Column, string DataType, string? StoreType, bool Override)> typed)
    {
        var columns = typed.Select((column, index) =>
                new DataSourceColumnRow(column.Column, index + 2, column.Column, "Invented.", column.DataType, null, null, null, true, true, true, true, true)
                { StoreType = column.StoreType, StoreTypeOverride = column.Override })
            .Prepend(new DataSourceColumnRow("id", 1, "id", "Invented.", "number", null, null, null, true, true, true, true, true))
            .ToArray();
        var mapped = CatalogMapping.Map(new(new DataSourceRow("dbo.vproof", scratch.Connection)
        {
            Id = 1, Name = "proof", Title = "Proof", Description = "Invented.", Grain = "One row", IsActive = true,
            RowCap = 100000, QueryTimeoutSeconds = 30
        }, columns));
        Assert.True(mapped.IsSuccess, mapped.Failure?.Message);
        // A fresh engine per pass, so each pass does its own once-per-process check.
        return new Runner(new QueryRunner(new SqlServerEngine()), mapped.Value);
    }

    /// <summary>A query's ids in order, or the failure code and SQL Server error number of a refusal.</summary>
    private sealed record Outcome(int[]? Ids, string? Failure)
    {
        public override string ToString() => Failure ?? Show(Ids!);
    }

    private sealed class Runner(QueryRunner runner, SourceContract source)
    {
        internal List<StoreTypeFinding> Findings { get; } = [];

        internal async Task<int[]> RunAsync(string query)
        {
            var outcome = await TryRunAsync(query);
            return outcome.Ids ?? throw new Xunit.Sdk.XunitException(query[..Math.Min(120, query.Length)] + ": refused, " + outcome.Failure);
        }

        internal async Task<Outcome> TryRunAsync(string query)
        {
            // MaxInValues 2100, so the in-list cases reach SQL Server's parameter limit instead of the gateway's own.
            var validated = QueryBuilding.Validate(source, query, QueryLimits.ForRowCap(source.Source.RowCap) with { MaxInValues = 2100 });
            Assert.True(validated.IsSuccess, validated.Failure?.Message);
            var compiled = await runner.BindStoreTypesAsync(source, runner.Compile(validated.Value), Findings.Add, default);
            var rows = await runner.RunAsync(source, compiled, default);
            if (!rows.IsSuccess) return new(null, $"{rows.Failure!.Code} {rows.Failure.SqlNumber}");
            Assert.False(rows.Value.More);
            return new(rows.Value.Rows.Select(row => Convert.ToInt32(row["id"])).Order().ToArray(), null);
        }
    }
}
