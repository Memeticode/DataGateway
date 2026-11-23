#pragma warning disable CS8892 // The test runner supplies its own entry point.
#region Program
namespace GatewaySample
{
    using Dapper.Data;
    using DataGateway;
    using DataGateway.Data.Sqlite;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.Data.Sqlite;

    /// <summary>The quickstart app: one gateway, named <c>sample</c>, over a single SQLite file that holds both the
    /// catalog and the one source table.</summary>
    /// <remarks>The app makes every choice the library leaves open: where the catalog lives and how it is read
    /// (<see cref="SampleCatalog"/>), which engine runs source queries, and which endpoints to map. It adds no
    /// sign-in, so anyone who can reach the port can call it; keep it on loopback.</remarks>
    public static class SampleProgram
    {
        /// <summary>With <c>--seed</c>, creates <c>sample.db</c> in the current folder and exits; otherwise runs the
        /// app over that file until it is stopped.</summary>
        /// <param name="args">The command-line arguments: <c>--seed</c> alone, or the web host's arguments.</param>
        public static async Task Main(string[] args)
        {
            var file = Path.GetFullPath("sample.db");
            if (args is ["--seed"])
            {
                await SampleDatabase.CreateAsync(file, CancellationToken.None);
                return;
            }

            await using var app = CreateApplication(args, file);
            await app.RunAsync();
        }

        /// <summary>Builds the app over an already seeded database file.</summary>
        /// <remarks>
        /// <para>The connection opens the file read-only, with pooling disabled. The SQLite engine accepts a source
        /// connection only in that form, and read-only means no catalog read or source query can change the file.
        /// With pooling off, closing a connection also closes the file instead of keeping it open in a pool. The
        /// catalog and the source use the same string, because the same file holds both.</para>
        /// <para><c>UseCatalog</c> gives the gateway a factory for the catalog reader; the gateway calls it for each
        /// request that needs the catalog, and every read goes to the file afresh. <c>AddEngine</c> registers the
        /// SQLite engine under its provider name, <c>sqlite</c>, which is the provider each catalog detail names.
        /// <c>MapDataGateway</c> maps the three routes and <c>/mcp</c> at the root; <c>MapDataGatewayApiPage</c>
        /// adds the API page and its OpenAPI document.</para>
        /// </remarks>
        /// <param name="args">The web host's arguments.</param>
        /// <param name="file">The SQLite database file that <see cref="SampleDatabase.CreateAsync"/> seeded.</param>
        /// <returns>The built application, not yet started.</returns>
        public static WebApplication CreateApplication(string[] args, string file)
        {
            var connection = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(file),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddDataGateway("sample", gateway => gateway
                .UseCatalog<SqliteConnection>(_ => new SampleCatalog(
                    DbContext<SqliteConnection>.FromFactory(
                        () => new SqliteConnection(connection)), connection))
                .AddEngine(new SqliteEngine()));
            var app = builder.Build();
            app.MapDataGateway("sample", "");
            app.MapDataGatewayApiPage("sample", "");
            return app;
        }
    }
}
#endregion

#region Catalog
namespace GatewaySample
{
    using Dapper.Data;
    using DataGateway.Core.Catalog;
    using DataGateway.Data.Catalog;
    using Microsoft.Data.Sqlite;

    /// <summary>The app's catalog reader: it answers the gateway's three catalog questions (the MCP text, the list of
    /// sources, and one source in detail) from the <c>catalog_source</c> table.</summary>
    /// <remarks>
    /// <para>The catalog schema is the app's own; the library needs only these three answers. Here the MCP text
    /// and each source's columns are fixed in code, and every source's connection is the one
    /// <paramref name="sourceConnection"/> the app passes in, because every source table lives in the same
    /// file.</para>
    /// <para>Each read runs inside <c>Db.BatchAsync</c>, which opens a connection for the callback and closes it
    /// afterwards, and copies its rows into new objects before returning, so nothing outlives the connection. The
    /// cancellation token is passed to every asynchronous call and checked before returning.</para>
    /// </remarks>
    /// <param name="db">Creates the catalog connections; see <c>SampleProgram.CreateApplication</c>.</param>
    /// <param name="sourceConnection">The connection every source's queries use.</param>
    public sealed class SampleCatalog(
        DbContext<SqliteConnection> db, string sourceConnection)
        : CatalogService<SqliteConnection>(db)
    {
        /// <summary>Returns the MCP server title and about text and each tool's title and description.</summary>
        /// <remarks>These are the texts a catalog database is seeded with, fixed in code; an app can read them from
        /// its own storage instead. All three tools must be present, under these names.</remarks>
        public override Task<global::DataGateway.Core.Catalog.McpMetadata> QueryMcpMetadataAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new McpMetadata("DataGateway", "Read-only access to cataloged data sources.",
            [
                new("list_sources", "List sources", """No source named? See all data sources and what each holds. Takes no arguments and returns {"sources":[...]}: each active source's name, title, description, grain (what one row represents) and rowCap (maximum rows per query). Pick the source whose description and grain fit the question, then call describe_source with its name."""),
                new("describe_source", "Describe a source", """Named source not yet described? Get schema and query rules. Returns one source (name, title, description, grain, rowCap) with querySchema: every column with its meaning, type, unit, magnitude, display format and can* flags, then the rules of each query clause. Use only listed columns, as their flags allow. Before stating a number, read its unit and magnitude: "millions" means 523.06 is 523.06 million, for sum_, avg_, min_ and max_ outputs too; a count_ output is a plain count."""),
                new("query_source", "Query a source", """Source already described? Run a read-only query, get rows. Build query from that source's querySchema (describe_source). Returns {source, columns, rows, rowCount, more, hint}. Prefer filters and aggregates over pulling raw rows. With or without limit, more is true exactly when rows were left out: never report that answer as complete. Narrow or aggregate; there is no way to ask for the rest. Empty rows, or an aggregate's 0 or null, can mean no row matched or a misspelled value."""),
            ]));
        }

        /// <summary>Returns every source in the catalog, with its row cap.</summary>
        /// <remarks>Inactive rows are returned too, with <c>is_active</c> as stored; the gateway leaves them out of
        /// its list answer.</remarks>
        public override async Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Db.BatchAsync<IReadOnlyList<SourceListRow>>(async session =>
            {
                await using var command = session.Connection.CreateCommand();
                command.CommandText =
                    "SELECT name, title, description, grain, is_active, row_cap FROM catalog_source";
                command.CommandTimeout = 30;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var rows = new List<SourceListRow>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new SourceListRow(reader.GetString(0), reader.GetString(1),
                        reader.GetString(2), reader.GetString(3), reader.GetBoolean(4),
                        reader.GetInt32(5)));
                }
                cancellationToken.ThrowIfCancellationRequested();
                return rows.ToArray();
            }, cancellationToken);
        }

        /// <summary>Returns one source's detail: its row, the object its queries read, its connection, and its
        /// columns; null when no row has that name.</summary>
        /// <remarks>
        /// <para>The name arrives as the caller sent it, and the app decides how to match it: here SQLite's
        /// <c>NOCASE</c>, which ignores case for ASCII letters only. A null answer makes the gateway report an
        /// unknown source. The name travels as a parameter, never inside the SQL text.</para>
        /// <para>The query timeout (30 seconds) and the one <c>id</c> column, open to every operation, are fixed
        /// here because the sample's catalog table has no place for them. The provider name <c>sqlite</c> selects
        /// the engine that runs this source's queries.</para>
        /// </remarks>
        public override async Task<CatalogDetail?> QueryCatalogDetailAsync(
            string sourceName, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(sourceName);
            cancellationToken.ThrowIfCancellationRequested();
            return await Db.BatchAsync<CatalogDetail?>(async session =>
            {
                await using var command = session.Connection.CreateCommand();
                command.CommandText = """
                    SELECT name, title, description, grain, is_active, row_cap, object_name
                    FROM catalog_source WHERE name = @source COLLATE NOCASE
                    """;
                command.CommandTimeout = 30;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@source";
                parameter.Value = sourceName;
                command.Parameters.Add(parameter);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    return null;
                var source = new DataSourceRow(reader.GetString(6), sourceConnection)
                {
                    Name = reader.GetString(0),
                    Title = reader.GetString(1),
                    Description = reader.GetString(2),
                    Grain = reader.GetString(3),
                    IsActive = reader.GetBoolean(4),
                    QueryTimeoutSeconds = 30,
                    RowCap = reader.GetInt32(5)
                };
                DataSourceColumnRow[] columns =
                [
                    new("id", 1, "Id", "Item identifier", "number", null, null, null,
                        true, true, true, true, true)
                ];
                cancellationToken.ThrowIfCancellationRequested();
                return new CatalogDetail("sqlite", new CatalogRows(source, columns));
            }, cancellationToken);
        }
    }
}
#endregion

#region Database
namespace GatewaySample
{
    using Microsoft.Data.Sqlite;

    /// <summary>Creates the quickstart's database: the catalog table with one source, and that source's
    /// table.</summary>
    public static class SampleDatabase
    {
        /// <summary>Creates the file and its two tables in one transaction.</summary>
        /// <remarks>
        /// <para><c>catalog_source</c> gets one row: the source <c>sample_items</c>, active, with a row cap of 2,
        /// reading the table <c>items</c>. <c>items</c> gets three rows, ids 1 to 3, so a query without a limit
        /// stops at the cap and comes back with <c>more</c> set to true. All the names and values are
        /// invented.</para>
        /// <para>This is the one connection that writes, so it opens the file read-write. Pooling is off, so the
        /// file is closed when seeding ends. Seeding runs once: a second run fails because the tables already
        /// exist, and the transaction leaves nothing half made.</para>
        /// </remarks>
        /// <param name="file">The database file to create.</param>
        /// <param name="cancellationToken">Cancels the seeding.</param>
        public static async Task CreateAsync(string file, CancellationToken cancellationToken)
        {
            var connection = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(file),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();
            await using var db = new SqliteConnection(connection);
            await db.OpenAsync(cancellationToken);
            await using var transaction = db.BeginTransaction();
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE catalog_source (
                    name TEXT PRIMARY KEY COLLATE NOCASE,
                    title TEXT NOT NULL,
                    description TEXT NOT NULL,
                    grain TEXT NOT NULL,
                    is_active INTEGER NOT NULL,
                    row_cap INTEGER NOT NULL,
                    object_name TEXT NOT NULL
                );
                INSERT INTO catalog_source VALUES (
                    'sample_items', 'Sample items', 'Three invented items',
                    'One item', 1, 2, 'items'
                );
                CREATE TABLE items (id INTEGER PRIMARY KEY);
                INSERT INTO items VALUES (1), (2), (3);
                """;
            command.CommandTimeout = 30;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }
}
#endregion
