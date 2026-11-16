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

internal sealed class SqliteFixture : IDisposable
{
    internal string DirectoryPath { get; } = TestFiles.InRepo("tests/DataGateway.Web.Tests/obj/sqlite-proof/" + Guid.NewGuid().ToString("N"));
    internal string Connection { get; }
    internal string WriterConnection { get; }
    internal const string Detail = """
        {"select":["label","id","flag","amount"],"filters":[{"col":"id","op":"gte","value":2}],"order_by":[{"col":"id","dir":"asc"}],"limit":3}
        """;
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
            CREATE TABLE gateway_source(id INTEGER, name TEXT, title TEXT, description TEXT, grain TEXT, row_cap INTEGER,
                object_name TEXT, connection_string TEXT, is_active INTEGER);
            CREATE TABLE gateway_column(datasource_fk INTEGER, column_name TEXT, ordinal INTEGER, title TEXT,
                description TEXT, data_type TEXT, unit TEXT, magnitude TEXT, display_format TEXT,
                can_select INTEGER, can_filter INTEGER, can_sort INTEGER, can_group INTEGER, can_aggregate INTEGER);
            CREATE TABLE proof_items(id INTEGER, label TEXT, flag INTEGER, amount INTEGER);
            """);
        foreach (var (id, name, active) in new[] { (102, "synthetic_other", 1), (103, "synthetic_hidden", 0), (101, "synthetic_items", 1) })
        {
            Execute("INSERT INTO gateway_source VALUES(@v0,@v1,@v2,@v3,@v4,@v5,@v6,@v7,@v8)",
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

    internal void Change(string sql, params object?[] values)
    {
        using var db = new SqliteConnection(WriterConnection);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("@v" + i, values[i] ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    internal SqliteTestCatalog Catalog(int timeout = 7, Func<string, SqliteConnection>? createConnection = null, string provider = "sqlite") =>
        new(DbContext<SqliteConnection>.FromFactory(() => createConnection is null ? new SqliteConnection(Connection) : createConnection(Connection)), timeout, provider);
    internal async Task<SourceContract> Source()
    {
        var detail = await Catalog().QueryCatalogDetailAsync("synthetic_items", default);
        Assert.NotNull(detail);
        var result = CatalogMapping.Map(detail.Rows);
        Assert.True(result.IsSuccess, result.Failure?.Message);
        return result.Value;
    }
    internal async Task<CompiledQuery> Compile(IDatabaseEngine engine, string json) =>
        engine.Compile(QueryBuilding.Validate(await Source(), json, QueryLimits.ForRowCap(3)).Value);
    internal SqliteSettings Settings() => new(DirectoryPath, ErrorLogSettingsScope.Catalog);
    internal LoggedHost Host(IDatabaseEngine engine, IDictionary<string, string?>? settings = null,
        Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? configure = null,
        Func<string, SqliteConnection>? catalogConnection = null)
    {
        LoggedHost? host = null;
        int Timeout() => (host ?? throw new InvalidOperationException("The fixture host has not been composed.")).App.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<DataGatewayLimits>>().Value.CommandTimeoutSeconds;
        Task<IReadOnlyList<SourceListRow>> List(CancellationToken token) =>
            Catalog(Timeout(), catalogConnection, "sqlserver").QueryCatalogAsync(token);
        Task<CatalogDetail?> Detail(string name, CancellationToken token) =>
            Catalog(Timeout(), catalogConnection, "sqlserver").QueryCatalogDetailAsync(name, token);
        var catalog = new HostCatalogState { StartupList = List, StartupDetail = Detail, List = List, Detail = Detail };
        host = new LoggedHost(catalog, engine, settings, configure: configure);
        return host;
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}

internal sealed class SqliteSettings : IDisposable
{
    private readonly Dictionary<string, string?> original = new(StringComparer.OrdinalIgnoreCase);
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
    private void Set(string name, string? value)
    {
        original.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }
    public void Dispose()
    {
        foreach (var (name, value) in original) Environment.SetEnvironmentVariable(name, value);
    }
}
