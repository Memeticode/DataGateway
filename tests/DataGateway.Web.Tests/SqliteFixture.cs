using Dapper.Data;
using System.Collections;
using System.Text.Json;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Core.Tests;
using DataGateway.Data;
using DataGateway.Data.Catalog;
using DataGateway.Data.Sqlite;
using DataGateway.Web.Configuration;
using Microsoft.Data.Sqlite;
using DataGateway.Web.Catalog;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>A throwaway SQLite database for the SQLite engine proof: a catalog (<c>gateway_source</c>,
/// <c>gateway_column</c>) and one item table (<c>proof_items</c>), all invented, in a fresh file per test.</summary>
/// <remarks>
/// <para>The catalog lists three sources over the same table: <c>synthetic_items</c> and <c>synthetic_other</c> are
/// active, <c>synthetic_hidden</c> is not, so a test can tell an inactive source from a missing one. Columns are
/// inserted out of ordinal order, so a reader that trusts insertion order instead of <c>ordinal</c> shows up.</para>
/// <para>The item labels hold LIKE metacharacters (<c>%</c>, <c>_</c>, <c>[</c>, backslash) and look-alike text, and
/// one flag is null, so the pattern and boolean tests have rows that a wrong escape or a wrong conversion would
/// match.</para>
/// <para>Setup writes through a separate read-write connection. Everything the product sees uses
/// <see cref="Connection"/>, which is read-only with pooling off: the only form <see cref="SqliteEngine"/> accepts.
/// Pooling off also lets <see cref="Dispose"/> delete the file.</para>
/// </remarks>
internal sealed class SqliteFixture : IDisposable
{
    /// <summary>This fixture's own folder under the test project's <c>obj</c>, removed on dispose.</summary>
    internal string DirectoryPath { get; } = TestFiles.InRepo("tests/DataGateway.Web.Tests/obj/sqlite-proof/" + Guid.NewGuid().ToString("N"));
    /// <summary>The read-only, unpooled connection text that the catalog rows carry and the engine opens.</summary>
    internal string Connection { get; }
    /// <summary>The read-write connection text, used only by setup and <see cref="Change"/>.</summary>
    /// <remarks>Leak scans plant it beside <see cref="Connection"/>, so neither may reach a reply or a log.</remarks>
    internal string WriterConnection { get; }
    /// <summary>The standard query on <c>synthetic_items</c>: four columns, <c>id &gt;= 2</c>, ordered by id,
    /// limit 3.</summary>
    /// <remarks>Four rows match and the source's row cap is 3, so the answer is capped and <c>more</c> is
    /// true.</remarks>
    internal const string Detail = """
        {"select":["label","id","flag","amount"],"filters":[{"col":"id","op":"gte","value":2}],"order_by":[{"col":"id","dir":"asc"}],"limit":3}
        """;
    /// <summary>Creates the file and fills the catalog and items in one transaction, with bound parameters.</summary>
    internal SqliteFixture()
    {
        Directory.CreateDirectory(DirectoryPath);
        var file = Path.Combine(DirectoryPath, "invented-proof.sqlite");
        Connection = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        WriterConnection = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var db = new SqliteConnection(WriterConnection);
        db.Open();
        using var transaction = db.BeginTransaction();
        void Execute(string sql, params object?[] values)
        {
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("@v" + i, values[i] ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        Execute("""
            CREATE TABLE gateway_source(id INTEGER, name TEXT, title TEXT, description TEXT, grain TEXT, row_cap INTEGER, query_timeout_seconds INTEGER NOT NULL DEFAULT 30,
                object_name TEXT, connection_string TEXT, is_active INTEGER);
            CREATE TABLE gateway_column(datasource_fk INTEGER, column_name TEXT, ordinal INTEGER, title TEXT,
                description TEXT, data_type TEXT, unit TEXT, magnitude TEXT, display_format TEXT,
                can_select INTEGER, can_filter INTEGER, can_sort INTEGER, can_group INTEGER, can_aggregate INTEGER);
            CREATE TABLE proof_items(id INTEGER, label TEXT, flag INTEGER, amount INTEGER);
            """);
        foreach (var (id, name, active) in new[] { (102, "synthetic_other", 1), (103, "synthetic_hidden", 0), (101, "synthetic_items", 1) })
        {
            Execute("INSERT INTO gateway_source(id,name,title,description,grain,row_cap,object_name,connection_string,is_active) VALUES(@v0,@v1,@v2,@v3,@v4,@v5,@v6,@v7,@v8)",
                id, name, "Synthetic", "Invented SQLite proof data.", "One item", 3, "proof_items", Connection, active);
            foreach (var (column, ordinal, type) in new[] { ("amount", 4, "number"), ("flag", 3, "bool"), ("id", 1, "number"), ("label", 2, "text") })
                Execute("INSERT INTO gateway_column VALUES(@v0,@v1,@v2,@v3,@v4,@v5,NULL,NULL,NULL,1,1,1,1,1)",
                    id, column, ordinal, column, "Invented value.", type);
        }
        var labels = new[] { "plain", @"50%_[x]\end", "other", @"50AAZxZ\end", "tail" };
        object?[] flags = [1, 1, 0, null, 1];
        int[] amounts = [1, 0, 1, 0, 2];
        for (var i = 0; i < labels.Length; i++)
            Execute("INSERT INTO proof_items VALUES(@v0,@v1,@v2,@v3)", i + 1, labels[i], flags[i], amounts[i]);
        transaction.Commit();
    }

    /// <summary>Runs one write against the fixture file, so a test can change the catalog or the items after a host
    /// has started.</summary>
    /// <param name="sql">The statement, with parameters named <c>@v0</c>, <c>@v1</c> and so on.</param>
    /// <param name="values">The parameter values in order; null binds as a database null.</param>
    internal void Change(string sql, params object?[] values)
    {
        using var db = new SqliteConnection(WriterConnection);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("@v" + i, values[i] ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>An app-owned catalog reader over this file.</summary>
    /// <param name="timeout">The command timeout of each catalog read, in seconds.</param>
    /// <param name="createConnection">Optional connection factory, so a test can pass
    /// <see cref="SqliteProbe.Create"/> and watch each connection.</param>
    /// <param name="provider">The engine name each detail carries; the gateway sends the source's queries to the
    /// engine registered under it.</param>
    internal SqliteTestCatalog Catalog(int timeout = 7, Func<string, SqliteConnection>? createConnection = null, string provider = "sqlite") =>
        new(DbContext<SqliteConnection>.FromFactory(() => createConnection is null ? new SqliteConnection(Connection) : createConnection(Connection)), timeout, provider);
    /// <summary>The <c>synthetic_items</c> source as the product maps it, read through <see cref="Catalog"/>.</summary>
    /// <param name="queryTimeoutSeconds">The query timeout to set on the mapped source.</param>
    internal async Task<SourceContract> Source(int queryTimeoutSeconds = 30)
    {
        var detail = await Catalog().QueryCatalogDetailAsync("synthetic_items", default);
        Assert.NotNull(detail);
        var row = detail.Rows.Source;
        var source = new DataSourceRow(row.ObjectName, row.ConnectionString)
        {
            Id = row.Id, Name = row.Name, Title = row.Title, Description = row.Description,
            Grain = row.Grain, IsActive = row.IsActive, RowCap = row.RowCap, QueryTimeoutSeconds = queryTimeoutSeconds
        };
        var result = CatalogMapping.Map(new CatalogRows(source, detail.Rows.Columns));
        Assert.True(result.IsSuccess, result.Failure?.Message);
        return result.Value;
    }
    /// <summary>Validates <paramref name="json"/> against <c>synthetic_items</c> with a row cap of 3, then compiles it
    /// with <paramref name="engine"/>.</summary>
    internal async Task<CompiledQuery> Compile(IDatabaseEngine engine, string json) =>
        engine.Compile(QueryBuilding.Validate(await Source(), json, QueryLimits.ForRowCap(3)).Value);
    /// <summary>Isolated host settings for this fixture; dispose them before the fixture.</summary>
    internal SqliteSettings Settings() => new(DirectoryPath, ErrorLogSettingsScope.Catalog);
    /// <summary>A full web host whose catalog is this file and whose engine is <paramref name="engine"/>.</summary>
    /// <remarks>The catalog reports provider <c>sqlserver</c> on purpose. The web host registers its one engine under
    /// the configured provider name (SQL Server by default) and forwards to the <see cref="IDatabaseEngine"/> in its
    /// services, which the test replaces. The provider name must match that registration, not the engine
    /// inside.</remarks>
    /// <param name="engine">The engine the host runs queries with.</param>
    /// <param name="settings">Extra configuration for the host.</param>
    /// <param name="configure">Extra builder changes, run after the host's own.</param>
    /// <param name="catalogConnection">Optional factory for catalog connections, usually a probe's.</param>
    /// <param name="catalogTimeoutSeconds">The command timeout of catalog reads.</param>
    internal LoggedHost Host(IDatabaseEngine engine, IDictionary<string, string?>? settings = null,
        Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? configure = null,
        Func<string, SqliteConnection>? catalogConnection = null, int catalogTimeoutSeconds = 15)
    {
        Task<IReadOnlyList<SourceListRow>> List(CancellationToken token) =>
            Catalog(catalogTimeoutSeconds, catalogConnection, "sqlserver").QueryCatalogAsync(token);
        Task<CatalogDetail?> Detail(string name, CancellationToken token) =>
            Catalog(catalogTimeoutSeconds, catalogConnection, "sqlserver").QueryCatalogDetailAsync(name, token);
        var catalog = new HostCatalogState { StartupList = List, StartupDetail = Detail, List = List, Detail = Detail };
        var host = new LoggedHost(catalog, engine, settings, configure: configure);
        return host;
    }

    /// <summary>Deletes the fixture folder; hosts and clients must be closed first.</summary>
    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}

/// <summary>Host settings that come only from the test: the catalog connection through user secrets in a scratch
/// folder, and every inherited <c>DATAGATEWAY_</c> and limit variable cleared until dispose.</summary>
/// <remarks>Without it, a variable set on the developer's machine or build agent would change what the host reads,
/// and the host would read the developer's real user secrets. The constructor checks that the secrets file lands
/// inside the scratch folder before it writes the file.</remarks>
internal sealed class SqliteSettings : IDisposable
{
    /// <summary>Each variable's value before this scope changed it, restored on dispose.</summary>
    private readonly Dictionary<string, string?> original = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Clears the variables, points user secrets at a folder under <paramref name="directory"/> and writes
    /// <paramref name="connection"/> there as the catalog connection setting.</summary>
    /// <remarks>On any failure it restores what it already changed before rethrowing: no caller holds the scope yet
    /// to dispose it.</remarks>
    /// <param name="directory">The folder that receives the scratch settings.</param>
    /// <param name="connection">The catalog connection the host will read.</param>
    internal SqliteSettings(string directory, string connection)
    {
        try
        {
            var root = Path.Combine(directory, "settings");
            Directory.CreateDirectory(root);
            foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
                if (((string)item.Key).StartsWith("DATAGATEWAY_", StringComparison.OrdinalIgnoreCase)) Set((string)item.Key, null);
            foreach (var name in new[] { "ConnectTimeoutSeconds", "CommandTimeoutSeconds", "RowCap", "MaxJsonLength", "MaxFilterDepth", "MaxFilterConditions", "MaxInValues" })
                Set("DataGateway__Limits__" + name, null);
            foreach (var name in new[] { "APPDATA", "HOME", "DOTNET_USER_SECRETS_FALLBACK_DIR" }) Set(name, root);
            var path = PathHelper.GetSecretsPathFromSecretsId("DataGateway-Web");
            var relative = Path.GetRelativePath(root, path);
            Assert.False(Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string> { [DataGatewayConfiguration.ConnectionSetting] = connection }));
        }
        catch { Dispose(); throw; }
    }
    /// <summary>Sets one environment variable, remembering its first original value.</summary>
    private void Set(string name, string? value)
    {
        original.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }
    /// <summary>Restores every variable this scope changed.</summary>
    public void Dispose()
    {
        foreach (var (name, value) in original) Environment.SetEnvironmentVariable(name, value);
    }
}
