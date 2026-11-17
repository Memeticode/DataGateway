# Configuration

## Catalog connection

The API requires one catalog connection before it can start: the `DATAGATEWAY_CATALOG_CONNECTION` environment variable, or the same setting in this project's user secrets. The [README's setup step 5](README.md#5-set-the-catalog-connection) gives the SQL Server database-name rule and both secure-prompt examples.

The environment variable takes precedence, including an invalid or blank value; validation fails rather than falling back to user secrets. User secrets work in every host environment. These inputs are loaded separately from ordinary application configuration. Do not put connections in appsettings files or pass them as application command arguments. The existing user-secrets command stores the securely prompted value; it is not an application startup argument.

A supplied `ConnectionStrings` section is refused. After validation the host exposes its one connection under the logical key documented in setup step 5 and hides the raw input key from application configuration. HostCatalogs.CatalogConnectionError validates the selected catalog connection without opening it, independently of source-engine validation. Both supported catalog readers use the string exactly as written.

These rules are enforced by [DataGatewayConfiguration.Configure and ConfigureCatalog](Configuration/DataGatewayConfiguration.cs). [HostCatalogs.CatalogConnectionError](Catalog/HostCatalogs.cs) enforces the SQL Server database-name and server-name checks.

## Limits

Limits can be set in appsettings, user secrets through normal ASP.NET Core
configuration in Development, environment variables, or command arguments:

| Configuration key | Environment variable | Default | Range |
| --- | --- | --- | --- |
| `DataGateway:Limits:MaxJsonLength` | `DataGateway__Limits__MaxJsonLength` | 65536 | 1 to 2147483647 |
| `DataGateway:Limits:MaxFilterDepth` | `DataGateway__Limits__MaxFilterDepth` | 8 | 0 to 30 |
| `DataGateway:Limits:MaxFilterConditions` | `DataGateway__Limits__MaxFilterConditions` | 50 | 1 to 2147483647 |
| `DataGateway:Limits:MaxInValues` | `DataGateway__Limits__MaxInValues` | 100 | 1 to 2147483647 |

Every supplied limit must be an integer in its listed range; blank values are refused.
Absent settings keep their defaults. The retired `DataGateway:Limits:ConnectTimeoutSeconds` setting is refused at startup whenever present, even blank; remove it (including `DataGateway__Limits__ConnectTimeoutSeconds` in the environment).
A shared `JsonQueryLimits` holds the structural limits configured at startup.
The structural limits are SqlKata.JsonQuery's, with its defaults. `MaxJsonLength`
counts the characters of the query text. `MaxFilterDepth` 0 allows flat filters only.
Each describe or query call copies those limits with the source's catalog `row_cap` as `MaxLimit`.
The retired `DataGateway:Limits:RowCap` setting is refused at startup whenever present, even blank.
Remove it, including `DataGateway__Limits__RowCap` in the environment.

## Connection behaviour

Each source's catalog `query_timeout_seconds` bounds its query as one deadline from the open connection to the last row kept. It is an integer from 1 through 86400, default 30. A query past it fails with `QueryTimeout`. The retired `DataGateway:Limits:CommandTimeoutSeconds` setting is refused whenever present; remove it, including `DataGateway__Limits__CommandTimeoutSeconds` in the environment, and set query_timeout_seconds in the catalog.
The sealed example catalogs accept `(db, int catalogTimeoutSeconds = 15)` for catalog reads only; the host uses the default. Existing catalogs require the idempotent `db/catalog-query-timeout.sql` or `db/postgresql/catalog-query-timeout.sql` migration, preserving audit history. The timeout is detail-only and absent from all wire answers.
Each connection string governs its own connect behaviour: DataGateway uses every source's stored string and the catalog connection exactly as written and sets no connect timeout or retry count. For SQL Server, set `Connect Timeout` and `ConnectRetryCount` in the string to choose your own; otherwise the SQL Server driver's defaults apply (15 seconds and one retry). PostgreSQL uses Npgsql's connection options and defaults; `ConnectRetryCount` is SQL Server-specific. DataGateway itself never retries. A stored string the driver refuses, or one missing a required connection field, is logged as `InvalidCatalog`. Nothing about a source is cached; each query opens its own context.

The source's `row_cap` shapes an answer through `limit` and `more`. See Query in the [README](README.md#query).

The catalog stores `row_cap` as a required integer from 1 to 2147483646, with a database default of 500. List and describe return it as `rowCap`. Describe also advertises it as `querySchema.limit.maximum`, the largest `limit` a query may request. Each call reads the catalog afresh.
Invalid startup configuration is reported to stderr and the executable exits 2.
Credential values and provider exception details are omitted from these errors.
Unexpected host failures exit 4 with an error type and replay guidance.

Run tests with `dotnet test DataGateway.slnx`. They supply synthetic credentials
themselves and require no database, user-secret setup, or environment settings.
Run tests may also call `Program.CreateApplication(WebApplicationOptions, ...)`
and start/stop the returned application with cancellation tokens.

## Replace the database engine

SQL Server and PostgreSQL are supported. Set `DataGateway:DatabaseEngine` (environment variable `DataGateway__DatabaseEngine`) to `SqlServer` or `PostgreSql`, case-insensitively, and restart. Absence selects SQL Server; blank and unknown values fail startup with `InvalidConfiguration: DataGateway:DatabaseEngine must be SqlServer or PostgreSql.` There is no runtime DLL discovery.

PostgreSQL catalog and source connections must explicitly name nonblank Host, Database and Username; the gateway does not use implicit database or login defaults. Accepted connection text is preserved. PostgreSQL 15 catalog setup uses [catalog-ddl.sql](../db/postgresql/catalog-ddl.sql), [catalog-views.sql](../db/postgresql/catalog-views.sql), [catalog-row-cap.sql](../db/postgresql/catalog-row-cap.sql), [catalog-query-timeout.sql](../db/postgresql/catalog-query-timeout.sql), [gateway-reader.sql](../db/postgresql/gateway-reader.sql) and the [seed example](../db/postgresql/catalog-seed-example.sql), in that order. Run them as a trusted schema owner with psql `ON_ERROR_STOP`; supply the existing role as `ReaderRole` and a password-free source connection as `SourceConnection`. Create source views and grant their SELECT separately. The generic scripts need no extension. The seed prerequisite is `synthetic.items` with id, label, amount, day and flag.

The composition is in [Program.cs](Program.cs). The selector chooses the host-owned SQL Server or PostgreSQL catalog reader and a forwarding engine alias. The library permits each source detail to select its own registered engine. Catalog storage is an app choice; this host's SQL schema is not a library requirement.

The `configureBuilder` hook runs before the selected `TryAddSingleton` registration. An earlier instance or factory registration retains precedence, including when the selector names another supported engine. After build, the host binds the gateway's forwarding engine to that singleton. Overriding this source singleton does not replace the selected catalog reader. Catalog connection validation remains independent in [HostCatalogs.CatalogConnectionError](Catalog/HostCatalogs.cs).

For a complete independent app, use the [library walkthrough](../README.md#add-it-to-a-new-app), [registration guide](../CONFIGURATION.md), [catalog-reader guide](../CATALOG-READERS.md) and [engine-writing guide](../ENGINES.md). The tests-only [SQLite proof](../DataGateway.Data.Sqlite/README.md#proof-boundary) composes an app-owned test catalog with its query engine. Its synthetic sign-in adds no sign-in switch to this host.

## Sign-in

Every request needs Windows sign-in (HTTP Negotiate over HTTP/1.1). The 401 body, and how a client signs in, are in the [README](README.md) (Errors; Get started, step 7). No setting turns sign-in off. Public tests sign in through a test-only scheme added with `CreateApplication`'s `configureBuilder`, so they need no Windows account.

A refused or broken handshake adds `incidentId` as the fourth body member, equal to its one failure line. An anonymous challenge and a handshake continuation leg keep the README's three-member challenge body byte for byte and leave no failure line. DataGateway does not read bodies before sign-in.

## MCP endpoint

The path `/mcp` and stateless mode are fixed in code. The existing `DataGateway:Limits` apply.

Two optional settings tell a client more about the server. Set them in appsettings; both are empty by default.

| Configuration key | Effect | Limit |
| --- | --- | --- |
| `DataGateway:Mcp:Title` | `serverInfo.title` (the display name; `name` and `version` do not change) | one line, at most 80 bytes (UTF-8) |
| `DataGateway:Mcp:About` | placed, with one space, before the fixed server instructions | one line, at most 120 bytes (UTF-8) |

Empty or blank means off: no `title`, and the instructions are exactly the fixed text. A blank value (empty or only whitespace, line breaks included) counts as unset. A non-blank value is checked as written, before trimming: over its limit, or holding a line break, it stops start-up with exit 2 and a message naming the key; otherwise it is trimmed. Leading and trailing spaces are dropped.

Only POST is mapped: a signed-in GET or DELETE
gets the framework's empty 405. Sign-in works as for the routes; a refusal line on
`/mcp` has `Operation` null because no tool is known yet.

`MaxJsonLength` counts the query argument as rebuilt from the parsed arguments,
in compact form. The OpenAPI document and API page leave `/mcp` out.
The SDK's `ModelContextProtocol.*` log categories appear on the console at
Information and in the file at Warning and above, and carry no DataGateway data.

## API page

In the Development environment DataGateway serves an interactive page for trying
the three operations at `/scalar/`, and its OpenAPI document at `/openapi/v1.json`.
From the public repository root, start it with:

```sh
dotnet run --project DataGateway.Web -- --environment Development
```

Alternatively, set `ASPNETCORE_ENVIRONMENT=Development`. In every other environment
neither is served, and no setting turns them on. Their paths answer like any
unmapped path: 401 before sign-in and 404 after sign-in.

The page, document and page scripts need Windows sign-in like every request.
Edge or Chrome on Windows sign in automatically at `http://localhost:5000`.
The document lists no server, so the page sends each call to the address it was
opened from, with the browser's sign-in.

The page loads nothing from outside this machine. Its scripts come from the Scalar
package and are served by DataGateway; web fonts, telemetry and the AI agent are off.
The document describes each operation's request body, answer and error answers,
with codes and statuses. It holds no catalog content: take source names from list
sources and each source's exact query schema from describe source.

## Errors

The error body, every code with its status, and where `path` points are in the [README](README.md#errors), Errors. This section covers what the server does around them.
Any other known DataGateway-side failure is logged with its own code and answered as 500 `InternalError` at `$`, with advice to contact the DataGateway administrator. The failure is still logged once the caller has disconnected, but no response body is written.
An exception outside every known failure answers HTTP 500 `UnexpectedError` at `$`,
with the fixed message "An unexpected error stopped DataGateway from completing the request. Report the incidentId to the DataGateway administrator. Tell the user. Do not retry."
and the incident ID. Its details are only in the error log under that ID, in
`ExceptionText`. `InternalError` stays for known faults.
Development returns the same JSON body, never an HTML exception page.
After the response starts, the connection is aborted and the line has `Step` `"response"`;
the incident ID then reaches only the log.

A body refused while being read, on the routes or `/mcp`, answers 400: Kestrel status 413 becomes
`StructuralLimitExceeded` at `$` with "The request body is larger than DataGateway accepts. Send a smaller request."
Other body read refusals become `InvalidJson` at `$` with "The request body could not be read in full. Send the complete JSON body in one request."
Both have an incident ID, `Step` `"request"` and no recorded body or exception text.
On `/mcp`, these refusals have `Operation` null and are HTTP errors before any tool runs.
Kestrel's default body limit is 30,000,000 bytes. Rejections before the application
runs, such as request line or header refusals, remain framework-owned.

## Error log

Serilog is the host's only logging provider. DataGateway needs `appsettings.json`
in its content root (by default the working directory). Without any child under
`Serilog:WriteTo`, startup stops with exit 2 and this exact error:

```text
InvalidConfiguration: Serilog:WriteTo names no sink, so failures would not be logged. Start DataGateway from its folder, whose appsettings.json declares the File and Console sinks, or set Serilog:WriteTo in configuration.
```

The shipped configuration declares the named Console and File sinks, loaded by
`Serilog:Using` (`Serilog__Using__0` and `Serilog__Using__1` in the environment),
with the values `Serilog.Sinks.Console` and `Serilog.Sinks.File`. Names allow one
argument to be overridden with one key. No other connection entry exists; the
validated `ConnectionStrings:Catalog` guard described above is unchanged.

| Configuration key | Environment variable | Default |
| --- | --- | --- |
| `Serilog:WriteTo:Console:Name` | `Serilog__WriteTo__Console__Name` | Console |
| `Serilog:WriteTo:Console:Args:outputTemplate` | `Serilog__WriteTo__Console__Args__outputTemplate` | `[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}` |
| `Serilog:WriteTo:File:Name` | `Serilog__WriteTo__File__Name` | File |
| `Serilog:WriteTo:File:Args:path` | `Serilog__WriteTo__File__Args__path` | `logs/datagateway-.jsonl` |
| `Serilog:WriteTo:File:Args:formatter` | `Serilog__WriteTo__File__Args__formatter` | `Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact` |
| `Serilog:WriteTo:File:Args:restrictedToMinimumLevel` | `Serilog__WriteTo__File__Args__restrictedToMinimumLevel` | Warning |
| `Serilog:WriteTo:File:Args:rollingInterval` | `Serilog__WriteTo__File__Args__rollingInterval` | Day |
| `Serilog:WriteTo:File:Args:fileSizeLimitBytes` | `Serilog__WriteTo__File__Args__fileSizeLimitBytes` | 1073741824 (1 GiB) |
| `Serilog:WriteTo:File:Args:rollOnFileSizeLimit` | `Serilog__WriteTo__File__Args__rollOnFileSizeLimit` | true |
| `Serilog:WriteTo:File:Args:retainedFileCountLimit` | `Serilog__WriteTo__File__Args__retainedFileCountLimit` | 31 |
| `Serilog:MinimumLevel:Default` | `Serilog__MinimumLevel__Default` | Information |
| `Serilog:MinimumLevel:Override:<category>` | `Serilog__MinimumLevel__Override__<category>` | No shipped overrides; `DataGateway.Failure` pinned to Warning in code |

Relative paths resolve against the working directory. Use an absolute path when
running as a service. Serilog creates missing directories. The daily date precedes
the extension, for example `datagateway-20261031.jsonl`; a full file rolls to `_001`
and later suffixes. At most 31 files are retained. There is no async sink,
buffering, shared writer, fallback, retry, startup write probe or stop-on-write-failure.
Each line reaches the file sink before the error reply is written. An unwritable
file is reported by Serilog's self-log on stderr while requests still answer.

The file uses CompactJsonFormatter: UTF-8 without BOM, one CLEF JSON object per
physical line. It holds full failure records, plus framework events at Warning
and above. Find failure lines by `SourceContext` equal to `DataGateway.Failure` or by
`IncidentId`. Fields are:

- `@t` (event time), `@mt` (message template), `@l` (Error or Warning).
- `IncidentId` (fresh random v4 GUID, 32 lowercase hex digits), `Caller`
  (signed-in identity name, or `anonymous` for a failed sign-in), `Operation`
  (`list`, `describe`, `query` for routes; `list_sources`, `describe_source`,
  `query_source` for tool calls; null when no endpoint matched or, on `/mcp`, when no tool is known yet).
- `Request` (UTF-8 request text, replacement characters for invalid bytes; on a tool line, the arguments object as JSON),
  `RequestBytes` (full byte count), `RequestCut` (whether text was cut).
  These three are null when no body was read in full, including failed sign-in and body refusals. Text is bounded to MaxJsonLength +
  1024 characters, capped at int.MaxValue (66,560 by default), with no split
  surrogate pair. Bodies that fit the configured query limit are kept whole.
- `Sql` (compiled SQL on every runner failure) and `SqlParameters` (name-to-value
  JSON object); both are null before a query reaches the runner.
- `Code`, `Path`, `Message` (the original internal failure, even when the response
  is redacted to InternalError), `ExceptionType`, `SqlNumber`, `Step` (diagnostics).
- `ReturnedCode`: the code in the error body. It may be `InternalError` while `Code` keeps the internal code. No body is written when the caller has gone or the response has started.
- `SourceId`, `SourceName`: the reached source's catalog ID and name; null before a source row is read. The requested name remains in `Request`.
- `DriverMessage`: the driver's message on expected failures, null when no exception was involved.
- `ExceptionText`: the exception's full text, including inner and aggregate exceptions and stacks, on unexpected failures only. Null on expected failures.
- `Step`: `connect`, `command`, `catalog`, `request`, `query`, `seed`, `sign-in`, `response`, or null. Pre-cancelled calls keep null.
- `SourceContext`, request context `RequestId`, `RequestPath`, `ConnectionId`,
  `Scope`, and trace keys when present (`TraceId`, `SpanId`, `ParentId`, `@tr`, `@sp`).
  `RequestId` correlates with the framework's console events.

The console shows a one-line failure summary containing the incident ID, operation,
caller, code, fix-it message, step, exception type and SQL number. It holds no
request, path, SQL or parameter values. Caller text in the declared file fields
is kept verbatim and JSON-escaped, so embedded newlines cannot forge a new line.
`DriverMessage`, `ExceptionText` and the short exception type are written as the driver and the runtime produced them; text is never edited, and no exception object is passed to the logger. DataGateway itself never writes a connection string or a password, and it trusts the driver not to disclose them. The one known exception is handled where it arises: when the driver refuses a source connection string as unparseable, it quotes the unparseable part, which can be part of a password, so DataGateway reports that refusal with its own message, `The source connection string cannot be parsed.`, and drops the driver's. Server, database and user names may appear. Request and SQL are kept as read and compiled.

If the failure line or the catch-all's error reply cannot be written, stderr gets
one notice: `DataGateway.Failure: incident <id>: the failure line could not be written (<cause full type name>); the failure was <failure full type name>.`
For a reply failure the problem text is `the error reply could not be sent` and
the connection is aborted. Notices contain type names only. The ID identifies an
attempted line when the log write failed. No retry, second sink or fallback file is added.

Failures are Error unless the caller has already gone when the line is written; those are Warning. On a route the caller has gone once `RequestAborted` is cancelled; on a tool call, once the call's cancellation token is, which on stateless `/mcp` happens when the call's HTTP request ends. A driver cancellation while the caller is present stays Error:
a catalog cancellation answers 500 `InternalError`, and a runner cancellation
answers 500 `UnexpectedError`. The failure category's minimum is pinned to Warning after reading
configuration, so category overrides cannot suppress its Error or Warning events.
Explicit sink restrictions still decide each sink's destination.
No `Logging` rule applies, provider-specific ones included (for example `Logging:Serilog:LogLevel:...`); set levels under `Serilog:MinimumLevel`.
Framework Information events remain on the console with the shipped settings and do not
reach the Warning file. Successful calls write no DataGateway success line. A caller
who leaves while sending a body has no failure value and leaves no line.

On `/mcp`, when a caller ends a tool call's request, the MCP SDK cancels the call, waits for it to return, then ends the request by rethrowing the abort. The call's own line, if it failed, is the only one; the rethrown abort writes none. A `notifications/cancelled` message is accepted and writes no line.
