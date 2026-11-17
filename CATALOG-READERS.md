# Writing a catalog reader

The app chooses catalog storage, schema, credentials, lookup rules and completion of source connection strings. The library accepts buffered metadata. The [README](README.md#add-it-to-a-new-app) provides a complete independent app, schema, seeding, fixed column contract, injected connection and project references.

## Implement the two reads

Derive from `CatalogService<TConnection>` in DataGateway.Data.Catalog. Its protected constructor takes `DbContext<TConnection> db`; protected `Db` exposes that context. `TConnection` must satisfy `DbConnection, new()`.

| Override | Return type |
| --- | --- |
| `QueryCatalogAsync(CancellationToken cancellationToken)` | `Task<IReadOnlyList<SourceListRow>>` |
| `QueryCatalogDetailAsync(string sourceName, CancellationToken cancellationToken)` | `Task<CatalogDetail?>` |

Use Dapper.Data 3.1.2 `DbContext<TConnection>.FromFactory` with a factory that supplies fresh closed connections. `BatchAsync` opens an awaited resource scope. Commands use `DbSession<TConnection>.Connection`. If using `QueryUnbufferedAsync`, enumerate inside that scope. Buffer and detach all results before returning. Forward cancellation to asynchronous work and let `OperationCanceledException` propagate.

The reader receives `sourceName` verbatim. The app chooses comparison rules. The sample's SQLite NOCASE lookup covers its invented ASCII names; it does not promise Unicode equivalence.

A null detail or an inactive mapped detail is an unknown source in normal requests. List exposes active names. The example readers throw `InvalidQueryTimeoutException` for missing, null, wrong-type, overflowing or out-of-range `query_timeout_seconds` cells. The gateway turns only that exception into the safe InvalidCatalog timeout correction, discarding the exception and bad cell. Other catalog invocation exceptions are sanitized to `CatalogUnavailable`, retaining exception type internally. Cancellation maps internally to `CatalogCancelled`, subject to the public envelope's `InternalError` redaction while connected. A disconnected caller receives no error body.

The factory runs lazily at most once per HTTP invocation needing catalog data, with scoped services. Awaited startup uses a separate scope for each registration, including unmapped ones. Apps own catalogs, supplied contexts and engines; DI owns scoped dependencies. Shared instances require safe concurrency and stable result objects. See [configuration](CONFIGURATION.md#register-a-gateway).

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

## Distinguish validation, mapping and startup

`CatalogValidator.ValidateList` checks a non-null list and entries, nonblank names without surrounding whitespace, uniqueness ignoring case, `RowCap` from 1 to `QueryLimits.MaxRowCap` (2147483646), and non-null Title/Description/Grain. Inactive rows are checked too. Those display strings may be empty.

`CatalogValidator.ValidateDetail` refuses null `requestedName`, accepts null detail, and otherwise checks a nonblank Provider, non-null Rows/Source, source name equal to the request ignoring case, nonblank ConnectionString/ObjectName, the same cap range, detail-only `QueryTimeoutSeconds` from 1 through 86400 immediately after RowCap, and non-null column entries and ColumnName. CatalogRows construction needs a usable columns sequence. An invalid timeout returns `InvalidCatalog` at `$.detail.rows.source.queryTimeoutSeconds` with "Source '{requestedName}'. Set query_timeout_seconds to an integer from 1 through 86400." The host adds catalog step and requested source context. List rows do not carry this setting. Detail validation does not apply every list display/name rule.

`CatalogMapping` treats an inactive source as unknown. An active source needs columns. Column names match ASCII `[A-Za-z_][A-Za-z0-9_]*` and are unique ignoring case. DataType is exactly text/number/date/bool. Columns sort by Ordinal, then ordinal ColumnName. Flags independently enable operations. All-false columns remain descriptive, but at least one column must enable a capability.

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


The host examples `SqlServerCatalog(db, int catalogTimeoutSeconds = 15)` and `PostgreSqlCatalog(db, int catalogTimeoutSeconds = 15)` use that positive input only for catalog reads. Each source query uses its own required `query_timeout_seconds` integer, default 30, range 1 through 86400. Run `db/catalog-query-timeout.sql` or `db/postgresql/catalog-query-timeout.sql` on existing catalogs; these idempotent migrations preserve audit history and refresh projections. The setting is absent from wire answers.
