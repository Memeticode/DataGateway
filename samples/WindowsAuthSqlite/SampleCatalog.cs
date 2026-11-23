namespace WindowsAuthSample
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
