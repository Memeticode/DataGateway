# Writing a catalog reader

The app chooses catalog storage, schema, credentials, lookup rules and completion of source connection strings. The library accepts buffered metadata. The [README](README.md#add-it-to-a-new-app) provides a complete independent app, schema, seeding, fixed column contract, injected connection and project references.

## Implement the three reads

Derive from `CatalogService<TConnection>` in DataGateway.Data.Catalog. Its protected constructor takes `DbContext<TConnection> db`; protected `Db` exposes that context. `TConnection` must satisfy `DbConnection, new()`.

| Override | Return type |
| --- | --- |
| `QueryCatalogAsync(CancellationToken cancellationToken)` | `Task<IReadOnlyList<SourceListRow>>` |
| `QueryCatalogDetailAsync(string sourceName, CancellationToken cancellationToken)` | `Task<CatalogDetail?>` |
| `QueryMcpMetadataAsync(CancellationToken cancellationToken)` | `Task<McpMetadata>` |

Use Dapper.Data 3.1.2 `DbContext<TConnection>.FromFactory` with a factory that supplies fresh closed connections. `BatchAsync` opens an awaited resource scope. Commands use `DbSession<TConnection>.Connection`. If using `QueryUnbufferedAsync`, enumerate inside that scope. Buffer and detach all results before returning. Forward cancellation to asynchronous work and let `OperationCanceledException` propagate.

The reader receives `sourceName` verbatim. The app chooses comparison rules. The sample's SQLite NOCASE lookup covers its invented ASCII names; it does not promise Unicode equivalence.

A null detail or an inactive mapped detail is an unknown source in normal requests. List exposes active names. The example readers throw `InvalidQueryTimeoutException` for missing, null, wrong-type, overflowing or out-of-range `query_timeout_seconds` cells. The gateway turns only that exception into the safe InvalidCatalog timeout correction, discarding the exception and bad cell. Other catalog invocation exceptions are sanitized to `CatalogUnavailable`, retaining exception type internally. Cancellation maps internally to `CatalogCancelled`, subject to the public envelope's `InternalError` redaction while connected. A disconnected caller receives no error body.

The factory runs lazily at most once per HTTP invocation needing catalog data, with scoped services. Each stateless MCP POST invokes QueryMcpMetadataAsync and shares that catalog instance with its tool source reads; JSON routes and page/document handlers do not read MCP metadata. Awaited startup uses a separate scope for each registration, including unmapped ones. Apps own catalogs, supplied contexts and engines; DI owns scoped dependencies. Shared instances require safe concurrency and stable result objects. See [configuration](CONFIGURATION.md#register-a-gateway).

## Return public rows

| Type | Constructor and properties |
| --- | --- |
| `SourceListRow` | `(string Name, string Title, string Description, string Grain, bool IsActive, int RowCap)` |
| `CatalogDetail` | `(string provider, CatalogRows rows)`; `Provider`, `Rows` |
| `CatalogRows` | `(DataSourceRow source, IReadOnlyList<DataSourceColumnRow> columns)`; `Source`, `Columns`; copies the columns |
| `DataSourceRow` | `(string objectName, string connectionString)`; required `Name`, `Title`, `Description`, `Grain`, `IsActive`, `RowCap`, `QueryTimeoutSeconds`; optional diagnostic `Id` |

`DataSourceRow.Id`, `ObjectName` and `ConnectionString` are excluded from JSON. `DataSourceColumnRow` has these thirteen constructor parameters in order:

| Position | Parameter |
| --- | --- |
| 1 | `string ColumnName` |
| 2 | `int Ordinal` |
| 3 | `string Title` |
| 4 | `string Description` |
| 5 | `string DataType` |
| 6 | `string? Unit` |
| 7 | `string? Magnitude` |
| 8 | `string? DisplayFormat` |
| 9 | `bool CanSelect` |
| 10 | `bool CanFilter` |
| 11 | `bool CanSort` |
| 12 | `bool CanGroup` |
| 13 | `bool CanAggregate` |

`DataSourceColumnRow` also has an init-only `string? Expression`, outside the constructor and Deconstruct and excluded from JSON and ToString. It carries the catalog column's reserved `expression` cell. A stored-catalog reader must read that cell by name and set `Expression` from it, null for NULL; the library cannot detect a reader that leaves it out, and such a reader would let a filled-in expression pass unseen. The example readers project `expression` and treat a missing or non-text cell as a bad cell.

`DataSourceColumnRow` likewise has an init-only `string? StoreType`, also outside the constructor, Deconstruct, JSON and ToString. It carries the catalog column's `store_type` cell: the source database's own spelling of the column's type, such as `varchar(20)`, `nvarchar(max)`, `char(10)`, `decimal(19,4)`, `datetime` or `int`. Its init-only `bool StoreTypeOverride` carries the `store_type_override` flag (NOT NULL, default false). A reader should set both from their cells, `StoreType` null for NULL; a reader that leaves them out only loses the typed binding. The example readers project both and treat a missing or non-text `store_type`, or a missing or non-flag `store_type_override`, as a bad cell.

## Distinguish validation, mapping and startup

`CatalogValidator.ValidateList` checks a non-null list and entries, nonblank names without surrounding whitespace, uniqueness ignoring case, `RowCap` from 1 to `QueryLimits.MaxRowCap` (2147483646), and non-null Title/Description/Grain. Inactive rows are checked too. Those display strings may be empty.

`CatalogValidator.ValidateDetail` refuses null `requestedName`, accepts null detail, and otherwise checks a nonblank Provider, non-null Rows/Source, source name equal to the request ignoring case, nonblank ConnectionString/ObjectName, the same cap range, detail-only `QueryTimeoutSeconds` from 1 through 86400 immediately after RowCap, and non-null column entries and ColumnName. CatalogRows construction needs a usable columns sequence. An invalid timeout returns `InvalidCatalog` at `$.detail.rows.source.queryTimeoutSeconds` with "Source '{requestedName}'. Set query_timeout_seconds to an integer from 1 through 86400." The host adds catalog step and requested source context. List rows do not carry this setting. Detail validation does not apply every list display/name rule.

`CatalogMapping` treats an inactive source as unknown. An active source needs columns. Column names match ASCII `[A-Za-z_][A-Za-z0-9_]*` and are unique ignoring case. DataType is exactly text/number/date/bool. Columns sort by Ordinal, then ordinal ColumnName. Flags independently enable operations. All-false columns remain descriptive, but at least one column must enable a capability. A column whose `Expression` is not null, including empty or whitespace text, is refused as `InvalidCatalog` before any flag is read, so an all-false column is refused too: "Column '{ColumnName}' has an expression, which this version does not support. Remove the expression from the catalog and compute the column in the source's view." The expression text is never echoed. `CatalogMapping` keeps each non-null `StoreType` as written, with its override, in the contract's server-side `StoreTypes` (`CatalogStoreType(Text, Override)`); it never parses or refuses one, because the text is in the source database's own spelling. Only an engine that implements `IStoreTypeEngine` reads it: the SQL Server engine, on the first query of a source in the process, reads the view's declared column types once and binds a column by its store type only when the view agrees, or when `store_type_override` is set. Text it cannot read, a type the view contradicts, a column the view lacks or has in two letter cases with neither spelled exactly like the catalog's, or a failed read (retried on a query at least 30 seconds after it began) binds that column, or the whole source, as before, and is logged once at Warning or Error under the `DataGateway.StoreType` category, naming the source, the column and both types but never unreadable text. PostgreSQL and SQLite sources ignore the cells.

Repeated ordinals and optional formatting values are not rejected. Provider aliases are not trimmed. ObjectName has no identifier-grammar check here. A mapped active detail must select a registered engine. Startup requires active valid detail for each active listed source. Missing or inactive detail prevents startup; source connectivity is checked on the query path, not at startup.

## Complete example reader

This is the README's complete `SampleCatalog.cs`. It uses public APIs with no internal helper, reflection or friend-assembly privilege. Use the README's other two source files and commands to run it.

<!-- sample region=Catalog -->
```csharp
namespace GatewaySample
{
    using Dapper.Data;
    using DataGateway.Core.Catalog;
    using DataGateway.Data.Catalog;
    using Microsoft.Data.Sqlite;

    public sealed class SampleCatalog(
        DbContext<SqliteConnection> db, string sourceConnection)
        : CatalogService<SqliteConnection>(db)
    {
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
```


The host examples `SqlServerCatalog(db, int catalogTimeoutSeconds = 15)` and `PostgreSqlCatalog(db, int catalogTimeoutSeconds = 15)` use that positive input only for catalog reads. Each source query uses its own required `query_timeout_seconds` integer, default 30, range 1 through 86400. Run `db/catalog-query-timeout.sql` or `db/postgresql/catalog-query-timeout.sql` on existing catalogs; these idempotent migrations preserve audit history and refresh projections. The setting is absent from wire answers. Then run `db/catalog-expression.sql` or `db/postgresql/catalog-expression.sql`, which add the reserved `expression` cell to the column table and its audit table in one transaction with a bounded lock wait, refresh the column audit trigger and view (the view appends `expression` last), and keep existing grants. To fill `store_type`, `SqlServerStoreTypeScript.CreateAsync(sources, CatalogScriptDialect.SqlServer or PostgreSql, timeoutSeconds, token)` (in DataGateway.Data.SqlServer) reads each SQL Server source's view with the same metadata query and returns UPDATE statements for the catalog's engine, for columns whose `store_type` is empty or differs from the view; it never runs them, and lists overridden columns, columns the view lacks and unreadable views as comments only. `db/catalog-store-type.sql` and `db/postgresql/catalog-store-type.sql` do the same for each column's `store_type` and `store_type_override` (appended after `expression`, in that order); each of the two migrations also adds the other's cells, so either one, in either order or again, leaves the same catalog.

## Return MCP metadata

MCP presentation text comes from two catalog tables: SQL Server `dbo.McpServer` and `dbo.McpTool`, or PostgreSQL `datagateway.mcpserver` and `datagateway.mcptool`.
The readers select their projection views `vMcpServer` / `vMcpTool` or `vmcpserver` / `vmcptool`, with the catalog command timeout.
The server table contains exactly one row, `id = 1`, with nonblank `title` (1-80 characters, no CR/LF) and `about` (1-1,000 characters).
The tool table contains exactly the keys `list_sources`, `describe_source`, and `query_source`; each has nonblank `title` (1-80 characters, no CR/LF) and `description` (1-1,000 characters).
Titles and text are preserved as stored, including surrounding spaces.
Lengths count UTF-16 code units, as .NET strings do, in both catalog CHECKs and runtime validation.
The seeded server title is `DataGateway` and its exact About is `Read-only access to cataloged data sources.`
The seeded tools are:

| tool_name | title | description |
| --- | --- | --- |
| `list_sources` | List sources | No source named? See all data sources and what each holds. Takes no arguments and returns {"sources":[...]}: each active source's name, title, description, grain (what one row represents) and rowCap (maximum rows per query). Pick the source whose description and grain fit the question, then call describe_source with its name. |
| `describe_source` | Describe a source | Named source not yet described? Get schema and query rules. Returns one source (name, title, description, grain, rowCap) with querySchema: every column with its meaning, type, unit, magnitude, display format and can* flags, then the rules of each query clause. Use only listed columns, as their flags allow. Before stating a number, read its unit and magnitude: "millions" means 523.06 is 523.06 million, for sum_, avg_, min_ and max_ outputs too; a count_ output is a plain count. |
| `query_source` | Query a source | Source already described? Run a read-only query, get rows. Build query from that source's querySchema (describe_source). Returns {source, columns, rows, rowCount, more, hint}. Prefer filters and aggregates over pulling raw rows. With or without limit, more is true exactly when rows were left out: never report that answer as complete. Narrow or aggregate; there is no way to ask for the rest. Empty rows, or an aggregate's 0 or null, can mean no row matched or a misspelled value. |

Fresh catalogs use `catalog-ddl.sql`, `catalog-views.sql` and `catalog-seed-example.sql` under `db/` or `db/postgresql/`.
Upgrade existing catalogs with `db/catalog-mcp-metadata.sql` or `db/postgresql/catalog-mcp-metadata.sql`; both scripts are idempotent, create the tables, views and audit triggers, seed missing rows, and preserve edited rows.
PostgreSQL readers receive grants through `db/postgresql/gateway-reader.sql`; SQL Server uses the existing reader role.
Startup validates all metadata and the final prefixed descriptions; an invalid row refuses startup with its table and field.
Every stateless POST reads metadata again through the asynchronous `ConfigureSessionOptions` hook, sharing its request catalog with any source reads.
There is no cache or server-held snapshot across initialize and tools/list; initialize and a later tools/list are separate reads.
Clients normally read the tool list when they connect, so an edit reaches a client on its next connection.
An explicit tools/list can refresh tool titles and descriptions sooner on an existing connection; text already received by the client remains unchanged.
The About precedes the fixed protocol rules with one space; the library still generates schemas and annotations.
`UseMcpTools` applies name prefixes to names and every tool reference in table text, and prepends the description prefix to each table description.
Each final description must be at most 1,000 characters, inclusive, after renaming and prefixing: 1,000 is accepted and 1,001 is refused.
Initialize and tools/list can fail with HTTP 503 CatalogUnavailable; tools/call retains its tool-result errors and existing argument paths and failure logging.
Scalar page titles and OpenAPI `info.title` always use the gateway registration name (`DataGateway.Web` in the packaged host), without reading MCP metadata; About appears in neither surface.

The sample returns valid fixed presentation rows; an application can instead read them from its own storage. Return all three McpToolMetadata rows with canonical ToolName keys. A missing server or tool row is invalid at startup and on each read. Custom readers must implement this third abstract method.
