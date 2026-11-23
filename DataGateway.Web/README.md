# DataGateway.Web

DataGateway is a generic, read-only window onto data in relational databases. The database owners choose which data to expose to DataGateway. Claude Code and Claude Desktop can find that data, learn what every column means, and query it: filter, sort, group and aggregate. It exposes a JSON POST API and the same three operations as MCP tools at `/mcp` on the same server. A small local stdio proxy, `DataGateway.Proxy`, carries the tools to clients that cannot sign in with Windows.

This document describes the example ASP.NET Core host. All commands run from the public source root (the exported `src/` directory), not from `DataGateway.Web/`. Its API page is available only in Development; see [API page](CONFIGURATION.md#api-page). Authentication and the following Windows sign-in policy belong to this host.

Status: the JSON API (list, describe and query) and the same three operations as MCP tools at `/mcp` run on the same server, with Windows sign-in, fix-it errors with incident IDs, and an error log. A small local stdio proxy, `DataGateway.Proxy`, carries the tools to clients that cannot sign in with Windows.

No sample data ships: you register one of your own views. SQL Server and PostgreSQL are supported; SQL Server is the default. Set `DataGateway:DatabaseEngine` to `PostgreSql` and restart to use PostgreSQL. See [engine selection and PostgreSQL setup](CONFIGURATION.md#replace-the-database-engine).

## How it works

- The catalog database holds `DataSource` and `DataSourceColumn`, plus two audit tables filled by triggers.
- Each source is one view. Its catalog row holds the full connection string the source needs, credentials included when it uses a SQL login. That string never appears in an answer or a log line. The audit tables keep copies of whole rows, connection strings included.
- Each request reads the catalog afresh. Nothing is cached, and a change applies on the next request.
- Only active, cataloged sources and cataloged columns are reachable.
- DataGateway reaches SQL through its configured read-only identity. The SQL Server setup uses `gateway_reader`, which holds `db_datareader` and `db_denydatawriter`. PostgreSQL uses an existing reader role with schema USAGE and SELECT on catalog and source views. Callers' own SQL rights are never used.

## Requirements

The walkthrough below configures SQL Server. PostgreSQL 15 setup and its explicit Host, Database and Username rules are in [configuration](CONFIGURATION.md#replace-the-database-engine). Both engines use .NET 10 and Windows sign-in.

- Windows and a Windows account: sign-in is HTTP Negotiate.
- SQL Server 2019 or later, and a Windows login allowed to create a database and a login.
- The .NET 10 SDK, `sqlcmd`, and PowerShell 5.1 or 7.
- One view of your own.

The SQL Server catalog database needs a case-insensitive collation. The server default `SQL_Latin1_General_CP1_CI_AS` is one. Otherwise add `COLLATE Latin1_General_100_CI_AS` to `CREATE DATABASE` below.

## Get started

Work from the public source root (the exported `src/` directory). Every PowerShell snippet starts with `$instance = 'localhost'`; change that value to your SQL instance. Enter secrets at the secure prompts, one line only.

### 1. Build and test

```powershell
$instance = 'localhost'
dotnet build DataGateway.slnx
dotnet test DataGateway.slnx
```

No database or setting is needed for these tests. Node is an additional prerequisite for the complete Web suite.

### 2. Create the catalog

Run once: this step is not rerunnable.

```powershell
$instance = 'localhost'
sqlcmd -S $instance -E -b -Q "CREATE DATABASE [Catalog]"
sqlcmd -S $instance -E -b -Q "ALTER DATABASE [Catalog] SET RECOVERY SIMPLE"
sqlcmd -S $instance -E -b -i db/catalog-ddl.sql -v CatalogDatabase=Catalog
sqlcmd -S $instance -E -b -i db/catalog-views.sql -v CatalogDatabase=Catalog
```

`db/catalog-views.sql` can be rerun. For an existing catalog, run `sqlcmd -S $instance -E -b -i db/catalog-row-cap.sql -v CatalogDatabase=Catalog` before starting this version. The migration adds the source cap and refreshes its audit trigger and view.

### 3. Create the read-only login

Run [db/gateway-reader.sql](../db/gateway-reader.sql) once per source database. Set `$sourceDatabase` to the database containing your view; use `Catalog` for a view inside `Catalog`.

```powershell
$instance = 'localhost'
$sourceDatabase = 'Catalog'
$secure = Read-Host 'Read-only login password' -AsSecureString
$env:ReaderPassword = [System.Net.NetworkCredential]::new('', $secure).Password.Replace("'", "''")
try {
    sqlcmd -S $instance -E -b -i db/gateway-reader.sql -v CatalogDatabase=Catalog SourceDatabase=$sourceDatabase
    if ($LASTEXITCODE -ne 0) { throw 'Creating the read-only login failed.' }
} finally {
    Remove-Item Env:ReaderPassword
}
```

sqlcmd reads the scripting variable from the environment, so the password never sits on a command line or in history. `db_datareader` can read every view in that database; the catalog is the boundary of what callers see.

### 4. Register a view

Create your view first. Copy [db/catalog-seed-example.sql](../db/catalog-seed-example.sql) to a file of your own and edit:

- `name`: the stable key callers use; `object_name`: `schema.view`; `title`.
- `description`: what it is for, when to use it, and how it differs from similar sources; `grain`: what one row is.
- `query_timeout_seconds` sets the source query deadline and command timeout. This required integer defaults to 30 and accepts 1 through 86400. It is detail-only and absent from wire answers. Existing catalogs use the idempotent `db/catalog-query-timeout.sql` or `db/postgresql/catalog-query-timeout.sql` migration; audit history is preserved.
- `row_cap` sets the maximum rows per query for this source. It is required, defaults to 500 in the database, and accepts integers from 1 to 2147483646.
- One entry per column: `column_name` as in the view, `ordinal`, `title`, `description`, `data_type` (text, number, date or bool), `unit`, `magnitude` (none, thousands, millions, billions or NULL), `display_format`, and the five flags (`can_select`, `can_filter`, `can_sort`, `can_group`, `can_aggregate`).

The synthetic example registers `sales_by_line`, backed by `dbo.vSalesByLine`, with `country` and `line_total`. It creates no view and ships no data. Replace its names and metadata with your own.

The source connection names the source's server and database, and `gateway_reader` with its password, using the Server, Database, User ID and Password keywords. It is at most 1000 bytes and one line. A local server with a self-signed certificate needs TrustServerCertificate set to true. DataGateway uses this string exactly as written and sets no connect timeout or retry count. See [connection behaviour](CONFIGURATION.md#connection-behaviour) for driver defaults and overrides.

Read the connection securely; reject line breaks and double apostrophes for sqlcmd substitution. Replace the path in the following call with your edited copy. Never add `-e`, which echoes SQL.

```powershell
$instance = 'localhost'
$secure = Read-Host 'Source connection' -AsSecureString
$sourceConnection = [System.Net.NetworkCredential]::new('', $secure).Password
if ($sourceConnection.Contains("`n") -or $sourceConnection.Contains("`r")) { Remove-Variable sourceConnection; throw 'The source connection must be one line.' }
$env:SourceConnectionString = $sourceConnection.Replace("'", "''")
Remove-Variable sourceConnection
try {
    sqlcmd -S $instance -d Catalog -E -b -r 1 -i <your copy> -v CatalogDatabase=Catalog
    if ($LASTEXITCODE -ne 0) { throw 'Registering the view failed.' }
} finally {
    Remove-Item Env:SourceConnectionString
}
```

Keep the pattern's rules so the audit history stays useful:

- MERGE on natural keys: source name, then source and column name.
- Update only changed rows, so a rerun audits only real changes.
- Never delete and reinsert.
- No TRUNCATE or bulk load: they skip the audit triggers.
- Read new ids with `SCOPE_IDENTITY()`, never `@@IDENTITY`.
- No bare `OUTPUT`: tables with triggers require `INTO`.

A view column without a catalog row is refused as unknown. A column with every flag off is described but never queryable. `is_active = 0` hides a source.

### 5. Set the catalog connection

It explicitly names the server and a database, and `gateway_reader` with its password. `Catalog` is the setup default; another explicitly named SQL Server database is accepted. A missing or blank database is refused; the login's default database is not a fallback. For another name, use it consistently in the create/recovery commands, `-d`, `CatalogDatabase` script variable, and this connection; keep `SourceDatabase` set to the database containing your view. The logical key stays `ConnectionStrings:Catalog`. Supply `CatalogDatabase` on every affected script invocation; the scripts define no default. Choose one method. Session only, with nothing written to disk:

```powershell
$instance = 'localhost'
$secure = Read-Host 'Catalog connection' -AsSecureString
$env:DATAGATEWAY_CATALOG_CONNECTION = [System.Net.NetworkCredential]::new('', $secure).Password
```

Or user secrets:

```powershell
$instance = 'localhost'
$secure = Read-Host 'Catalog connection' -AsSecureString
$catalog = [System.Net.NetworkCredential]::new('', $secure).Password
dotnet user-secrets set DATAGATEWAY_CATALOG_CONNECTION $catalog --project DataGateway.Web
Remove-Variable catalog
```

User secrets store it in your user profile's secret store as plain text, as designed. See [catalog connection](CONFIGURATION.md#catalog-connection) for input precedence and startup checks.

### 6. Start DataGateway

```powershell
$instance = 'localhost'
cd DataGateway.Web
dotnet run
```

Or run `dotnet run --project DataGateway.Web` from the repository root. Either way, `dotnet run` starts it in its project folder, where its `appsettings.json` is. Running the built program from any other folder stops with exit 2, because it finds no log settings there.

It listens on `http://localhost:5000`; Ctrl+C stops it. Failure lines go to `logs/` under that folder; the [configuration guide](CONFIGURATION.md#error-log) explains the error log.

To host it on IIS for a Windows domain, with Claude Desktop connecting through a packaged proxy, see [IIS.md](IIS.md).

For the API page, run `dotnet run -- --environment Development` in that folder, then open `http://localhost:5000/scalar/`. See [configuration](CONFIGURATION.md#api-page) for deployment details.

### 7. Call it

From a second window:

```powershell
$instance = 'localhost'
Invoke-RestMethod -Method Post -Uri http://localhost:5000/sources/list -UseDefaultCredentials -ContentType 'application/json' -Body '{}'
curl.exe --negotiate -u : -H "Content-Type: application/json" -d "{}" http://localhost:5000/sources/list
```

In .NET use `HttpClientHandler { UseDefaultCredentials = true }`.

### 8. Run the demo

From the repository root:

```powershell
$instance = 'localhost'
dotnet run --project DataGateway.Demo -- http://localhost:5000
```

## Replace the database engine

Follow the [engine replacement guide](CONFIGURATION.md#replace-the-database-engine) for the project-reference, namespace and registration edits, the setup each adapter needs, and the existing replacement proof.

## Interface

Every route is `POST`, JSON in and out, and needs Windows sign-in over HTTP/1.1. Answers are `application/json; charset=utf-8`, with members in the order shown. Source and column names match without regard to case; answers use the catalog's spelling.

List takes exactly `{}` (surrounding whitespace is allowed). Describe takes exactly one member, `source`. Query takes exactly `source` and `query`.

### List

<!-- example name=list kind=request route=/sources/list -->
```json
{}
```

<!-- example name=list kind=answer status=200 -->
```json
{
  "sources": [
    {
      "name": "sales_by_line",
      "title": "Sales by invoice line",
      "description": "One row per invoice line, with the customer country and the line total. Use it for revenue by country. Unlike an invoice summary, an invoice appears once per line, so sum line_total to get invoice totals.",
      "grain": "one row per invoice line",
      "rowCap": 500
    }
  ]
}
```

### Describe

<!-- example name=describe kind=request route=/sources/describe -->
```json
{
  "source": "sales_by_line"
}
```

<!-- example name=describe kind=answer status=200 -->
```json
{
  "source": {
    "name": "sales_by_line",
    "title": "Sales by invoice line",
    "description": "One row per invoice line, with the customer country and the line total. Use it for revenue by country. Unlike an invoice summary, an invoice appears once per line, so sum line_total to get invoice totals.",
    "grain": "one row per invoice line",
    "rowCap": 500
  },
  "querySchema": {
    "columns": [
      {
        "name": "country",
        "title": "Country",
        "description": "Country of the customer on the invoice.",
        "dataType": "text",
        "unit": null,
        "magnitude": null,
        "displayFormat": null,
        "canSelect": true,
        "canFilter": true,
        "canSort": true,
        "canGroup": true,
        "canAggregate": false
      },
      {
        "name": "line_total",
        "title": "Line total",
        "description": "Price times quantity for the line, in US dollars.",
        "dataType": "number",
        "unit": "USD",
        "magnitude": "none",
        "displayFormat": "0.00",
        "canSelect": true,
        "canFilter": true,
        "canSort": true,
        "canGroup": false,
        "canAggregate": true
      }
    ],
    "limit": {
      "minimum": 1,
      "maximum": 500
    }
  }
}
```

`querySchema` describes the source and how to query it: every column with its meaning and flags, then a rule for each query clause. It guides a query; DataGateway's validator decides. Its `limit.maximum` equals the source's `rowCap`, read from catalog `row_cap`. The example shows only `columns` and `limit`; the real answer also has select, filters, group_by, aggregates and order_by.

### Query

<!-- example name=query kind=request route=/query -->
```json
{
  "source": "sales_by_line",
  "query": {
    "group_by": [
      "country"
    ],
    "aggregates": [
      {
        "fn": "sum",
        "col": "line_total"
      }
    ],
    "order_by": [
      {
        "col": "sum_line_total",
        "dir": "desc"
      }
    ],
    "limit": 5
  }
}
```

<!-- example name=query kind=answer status=200 -->
```json
{
  "source": "sales_by_line",
  "columns": [
    {
      "name": "country",
      "dataType": "text"
    },
    {
      "name": "sum_line_total",
      "dataType": "number"
    }
  ],
  "rows": [
    {
      "country": "USA",
      "sum_line_total": 523.06
    },
    {
      "country": "Canada",
      "sum_line_total": 310.50
    },
    {
      "country": "Mexico",
      "sum_line_total": 84.25
    }
  ],
  "rowCount": 3,
  "more": false,
  "hint": "Complete result set."
}
```

The query vocabulary:

- `select`: canSelect columns. Leave it out to get every selectable column. Not allowed with grouping.
- `filters`: conditions that must all hold. A condition is `{col, op, value}`. Nested `{"and":[...]}` and `{"or":[...]}` groups are allowed. Operators: eq, neq, lt, lte, gt, gte, contains, like, in, not_in, between, is_null and is_not_null; querySchema says which apply to which column. The only wildcard in a `like` pattern is `%`; `_` and `[` match themselves. The value shapes are those in the InvalidValue message in the error table below.
- `group_by`: canGroup columns.
- `aggregates`: `{fn, col}` with fn count, sum, avg, min or max. The output is named `<fn>_<col>`.
- `order_by`: `{col, dir}` with dir asc or desc. With grouping, only group_by columns or aggregate outputs, and the aggregated column must be canSort.
- `limit`: 1 up to the cap.

An answer holds at most `limit` rows, or the cap without one. `more: true` means rows were left out, with or without `limit`; `more: false` means none were. Every answer ends with `hint`: `The result set was capped at 1 row.` when `more` is true and `rowCount` is 1, or `The result set was capped at N rows.` for every other capped count (N is `rowCount`). A capped query without `order_by` appends `Rows are in no particular order.` With `more: false`, the hint is `Nothing found that matched your query.` when no source rows matched, including an ungrouped aggregate that still returns one row with 0 or null; otherwise, with an `in` filter (including one inside nested groups), it is `All matching rows returned. in values that matched no row are left out: compare the values you asked for with the rows.` If that query also has an aggregate, grouped or ungrouped, it is `All matching rows aggregated. in values that matched no row are left out and an aggregate hides them: check the in values first.` Without an `in` filter it is `Complete result set.` A `not_in` filter does not count as an `in` filter. Aggregate values are unchanged. Midnight dates come back as `yyyy-MM-dd`, ready to paste into a filter.

A request combining the vocabulary:

<!-- example name=vocabulary kind=request route=/query -->
```json
{
  "source": "sales_by_line",
  "query": {
    "select": [
      "country",
      "line_total"
    ],
    "filters": [
      {
        "col": "country",
        "op": "in",
        "value": [
          "USA",
          "Canada"
        ]
      },
      {
        "or": [
          {
            "col": "line_total",
            "op": "gte",
            "value": 100
          },
          {
            "col": "line_total",
            "op": "is_null"
          }
        ]
      }
    ],
    "order_by": [
      {
        "col": "line_total",
        "dir": "desc"
      }
    ],
    "limit": 10
  }
}
```

## MCP

The same server exposes `/mcp` using streamable HTTP in stateless mode, with POST only.
Windows sign-in works exactly as for the routes; anonymous callers get the challenge
shown under Errors. The local endpoint is `http://localhost:5000/mcp`.

A client sends Windows credentials (HTTP Negotiate over HTTP/1.1). In .NET, use the
MCP SDK's `HttpClientTransport` over an `HttpClient` built on
`HttpClientHandler { UseDefaultCredentials = true }`.
Claude Code (version 2.1.312, observed) does not sign in with Windows, and Claude
Desktop is not verified; both reach the server through the local proxy described at the
end of this section.

<!-- tool-table -->
| Tool | Arguments | Route |
| --- | --- | --- |
| `list_sources` | none | `POST /sources/list` |
| `describe_source` | `source` | `POST /sources/describe` |
| `query_source` | `source`, `query` | `POST /query` |

A tool's arguments are its route's request body. Its result is one text block holding
exactly the JSON the route answers, including the examples above. A failure sets
`isError`, and the text is the route's error body with the same code, path, message
and incident ID. For `UnexpectedError`, a second text block asks the model to tell the user, who reports the incident ID to the operator.

**Cancelling a call.** The endpoint is stateless, so a tool call is cancelled by ending its HTTP request: closing the connection, a client-side timeout, or, with the MCP SDK's `HttpClientTransport`, cancelling the token passed to `CallToolAsync`, which ends the request. DataGateway forwards cancellation to the catalog reader or source command. Provider interruption need not be immediate. A disconnected caller receives no error body. If a failure exists after departure, its one failure event is Warning and can carry `QueryCancelled` (source query) or `CatalogCancelled` (catalog read); departure before a failure exists need not log an event. A `notifications/cancelled` message cannot reach a running call: each POST is served on its own, so the message is accepted with HTTP 202 and changes nothing, and the call ends by itself or at the deadline using the source's catalog `query_timeout_seconds` value (30 seconds by default).

A message over the server's body limit gets HTTP 400 `StructuralLimitExceeded` before
any tool runs. Unknown tool names and malformed MCP messages are answered by the
MCP SDK as JSON-RPC errors and write no DataGateway log line.

MCP `serverInfo.name` is the gateway registration name; `serverInfo.version` comes from the DataGateway library assembly version. The library version changes with every change a caller can see. The first number rises when a valid documented call stops working or its documented meaning changes; the second for anything else a caller can see (tool text, instructions, `querySchema` rules, a response member, an annotation, an error code or message); the third for a fix with nothing visible to a caller. Catalog rows are data, not the server, and do not change it.



**The local proxy.** `DataGateway.Proxy` is a stdio MCP server for clients that cannot sign in with Windows. It forwards the tool list and tool calls to the server's `/mcp` unchanged, signed in as the Windows user it runs as, and answers the client's initialize with the server's own instructions and `serverInfo`. Start it with the server's `/mcp` URL as its only argument, for example `DataGateway.Proxy https://host/mcp`; a client's configuration puts that URL in `args`. It connects once, at start. If the server cannot be reached then, it exits with code 1 and says why on stderr; a missing or malformed argument exits with code 2. If the server stops later, a tool call returns an `isError` result with the code `ServiceUnreachable` (path `$`, a message, and an `incidentId` that names the proxy's own log entry); if the server answers a tool call with an HTTP error instead, such as a refused Windows sign-in, the code is `ServiceRefused` and the message names the status, distinct from `SourceUnavailable`, which a running server returns when a database is down; the next call works once the server is back. A `notifications/cancelled` from the client ends the forwarded request, which stops the server's query. In server mode stdout carries only the protocol; logs go to stderr and to `logs/datagateway-proxy-<date>.log` beside the program. To hand it to Claude Desktop users as an extension they install by opening one file, see [the package guide](../DataGateway.Proxy/mcpb/README.md).

For Claude Desktop, register the installed apphost executable with `DataGateway.Proxy --register http://localhost:5000/mcp --name sales-data`. Quit Claude Desktop from its tray icon first, not just its window; start it again after registration. Run the executable itself, not `dotnet DataGateway.Proxy.dll`. Registration checks the gateway's tool list using your Windows sign-in and refuses an unreachable gateway or a name that would exceed the 64-character bridged tool-name limit. The name must contain only ASCII letters, digits, underscores and hyphens.

Register the server in Claude Desktop or Claude Code under a name that identifies your data, such as `sales-data` for sales data. A deferred tool list may show only names before a tool is loaded, so a name that says what data is available helps the client find it. Hosts can show tools as `mcp__sales-data__<tool>`; this registration name belongs to the client and is separate from `serverInfo.name` and `serverInfo.title`. Use the same kind of descriptive name whether the client connects directly or through the proxy. Desktop's remote bridge can use `mcp__remote-devices__<key>__<tool>`; registration requires `21 + key.Length + 2 + longestTool.Length <= 64`.

Registration merges the entry into every eligible Claude Desktop config it finds, using the absolute path of this executable and the supplied URL. It preserves unrelated settings, removes old names pointing to this same executable and the same URL (an entry for another URL is another gateway and is kept; a URL spelled differently, such as http versus https or a trailing slash, counts as another URL, so remove such an entry with `--unregister`), and backs up changed existing files beside the originals before replacing them. A name owned by another program is refused; choose another `--name` or explicitly unregister that name first. Company policy can disable local MCP servers. Registration does not configure Claude Code.

To remove only the named entry from every discovered Claude Desktop config, quit Desktop first and run `DataGateway.Proxy --unregister --name sales-data`. This also removes that name when it belongs to a different program. Repeating an unchanged registration or removing an absent name leaves the files untouched. CLI status is printed once per file; exit 0 means all writes or skips succeeded, exit 1 reports a refusal or failure, and exit 2 means invalid arguments.

For manual setup as a fallback, quit Claude Desktop and add this entry to your client's `mcpServers` configuration:

```text
{
  "mcpServers": {
    "sales-data": {
      "command": "DataGateway.Proxy",
      "args": ["http://localhost:5000/mcp"]
    }
  }
}
```

Replace `sales-data` with a name for your own data and the URL with your server's `/mcp` endpoint. Put the installed `DataGateway.Proxy` executable on the client's PATH, or set `command` to its absolute path.

For Claude Code, register the same proxy with the same data-specific name:

```text
claude mcp add sales-data -- DataGateway.Proxy http://localhost:5000/mcp
```

If the executable is not on PATH, replace `DataGateway.Proxy` with its installed absolute path.

## Errors

The body is `code`, `path`, `message`, `incidentId`, in that order. The message says how to fix the request. `incidentId` is 32 lowercase hex digits, the same value as on the failure's log line; report it to the DataGateway administrator.

In this example host's policy, the plain sign-in challenge has three members, comes with `WWW-Authenticate: Negotiate` and writes no log line. A refused handshake adds `incidentId`. `InternalError` is a known DataGateway-side fault; `UnexpectedError` is anything else. For both, the details are only in the log.

In this example host's policy, after sign-in, an unknown path or a wrong method answers the framework's empty 404 or 405, not an error body.

<!-- example name=refusal kind=request route=/query -->
```json
{
  "source": "sales_by_line",
  "query": {
    "group_by": [
      "line_total"
    ]
  }
}
```

<!-- example name=refusal kind=answer status=400 -->
```json
{
  "code": "ColumnOperationNotAllowed",
  "path": "$.query.group_by[0]",
  "message": "The column at path cannot be grouped. Group by a column whose canGroup is true.",
  "incidentId": "9f1c2e7a4b6d4f0e8a3b5c7d9e1f2a4b"
}
```

<!-- example name=unknown-source kind=request route=/sources/describe -->
```json
{
  "source": "sales"
}
```

<!-- example name=unknown-source kind=answer status=404 -->
```json
{
  "code": "UnknownSource",
  "path": "$.source",
  "message": "Choose an active source. Available sources: sales_by_line.",
  "incidentId": "9f1c2e7a4b6d4f0e8a3b5c7d9e1f2a4b"
}
```

<!-- example name=challenge kind=answer status=401 route=/sources/list signin=none -->
```json
{
  "code": "NotAuthenticated",
  "path": "$",
  "message": "Sign in with Windows to call DataGateway: send the request with Windows credentials (HTTP Negotiate)."
}
```

Catalog invocation exceptions currently map to `CatalogUnavailable`; cancellation maps internally to `CatalogCancelled`, redacted to `InternalError` while the caller remains connected. The legacy catalog `QueryTimeout` status/path cells below remain declared vocabulary. A disconnected caller receives no error body. The Windows-specific `NotAuthenticated` text and Negotiate challenge above are this example host's policy.

<!-- error-table -->
| Code | Status | When | path |
| --- | --- | --- | --- |
| InvalidJson | 400 | Body is not JSON at `$`; query is not an object or has an unknown member at `$.query`; a repeated member or wrong JSON type at `$.query.<member>`. | `$`, `$.query`, `$.query.limit` |
| InvalidStructure | 400 | Wrong envelope members or member types, or a clause has an invalid structure. | `$`, `$.source`, `$.query.select` |
| UnknownColumn | 400 | Unknown column at its position in any clause: select[i], group_by[i], a filter's .col (keeping nested and/or segments), aggregates[i].col or order_by[i].col. | `$.query.select[0]` |
| ColumnOperationNotAllowed | 400 | A column flag refuses its operation, at the column's position in any clause, including nested filters, aggregates and sorts. | `$.query.group_by[0]` |
| InvalidOperator | 400 | Filter operator is not allowed for the column. | `$.query.filters[0].op` |
| InvalidAggregateFunction | 400 | Aggregate function is not count, sum, avg, min or max. | `$.query.aggregates[0].fn` |
| InvalidSortDirection | 400 | Sort direction is not asc or desc. | `$.query.order_by[0].dir` |
| InvalidValue | 400 | The filter value at path does not fit its operator or the column's data type. Give one value for eq, neq, lt, lte, gt, gte, contains and like; a non-empty list for in and not_in; two values, low then high, for between; and no value for is_null and is_not_null. Text takes a JSON string, number a JSON number, bool true or false, and date a string yyyy-MM-dd. | `$.query.filters[0].value` |
| InvalidClauseCombination | 400 | Explicit select cannot be combined with grouping. | `$.query.select` |
| InvalidOrderTarget | 400 | Grouped ordering must name a group column or an aggregate output. | `$.query.order_by[0].col` |
| DuplicateReference | 400 | The same column is repeated, ignoring case. | `$.query.select[1]` |
| InvalidLimit | 400 | Limit is not a positive integer. | `$.query.limit` |
| StructuralLimitExceeded | 400 | Body, query text, limit or in-list exceeds its cap. Also covers too many filter conditions (at that condition) and too deep nesting (at that group). | `$`, `$.query`, `$.query.limit`, `$.query.filters[0].value` |
| UnknownSource | 404 | Source is unknown or inactive. The message lists the active names. | `$.source` |
| NotAuthenticated | 401 | Windows sign-in is required or the handshake was refused. The plain challenge has no incidentId. | `$` |
| CatalogUnavailable | 503 | Catalog cannot be reached or read. | `$`, `$.source` |
| SourceUnavailable | 503 | Source cannot be reached or read. | `$.source` |
| QueryTimeout | 504 | Source query timeout; the catalog cells are legacy declared vocabulary, not currently emitted catalog failures. | `$`, `$.source`, `$.query` |
| InternalError | 500 | Known DataGateway-side fault. Fixed message: DataGateway could not complete the request because of a fault on its side, not in the request. Contact the DataGateway administrator. Tell the user, give the incidentId and stop; do not retry. Details only in the log. | `$` |
| UnexpectedError | 500 | Unexpected exception. Fixed message: An unexpected error stopped DataGateway from completing the request. Tell the user, give the incidentId and stop; do not retry. Details only in the log. | `$` |

## The demo

The demo lists sources, describes every listed source in order, then uses the chosen source for detail, equality filter, group with count (ordered by descending count when permitted), and a request without a limit to check the cap. It then checks unknown source, unknown column, a non-groupable column, a limit over the cap, and the anonymous challenge. A catalog of N sources gives N + 10 steps.

It discovers names through list and picks columns from describe's flags. Its made-up names are checked against what list returned. It checks statuses, content types, exact member names and order, types, row counts, limit and more, filtered values, descending counts, refusal codes, paths, messages, incident IDs, and the plain challenge with Negotiate.

Skips are printed with their reason when a capability or non-null filter value is absent. An empty catalog, unlisted choice, failed chosen describe, or source with no selectable column stops the run as a failure. A failed describe of another source is recorded and the run continues.

`--source <name>` chooses the source, ignoring case; otherwise the first listed source is used. Choose a small one: the cap step reads up to the cap plus one row, and the group step aggregates the whole view.

Exit codes: 0 all passed; 1 a check failed, the run stopped or DataGateway was unreachable; 2 bad arguments. The URL must be an absolute http or https root URL, with no credentials, path, query or fragment.

It reads no connection string and no environment variable. It prints only the URL, requests it sends, DataGateway's answers (your rows included), and check results. Bodies are cut at 2,000 characters. It only reads and never retries.

## Configuration

[DataGateway.Web/CONFIGURATION.md](CONFIGURATION.md) owns every setting, its default and allowed range, precedence and startup validation, the limits, the error log, sign-in details and the API page.

## Layout

- `DataGateway.Core`: pure functions with no I/O.
- `DataGateway.Data`: the public engine contract and shared catalog/query policies.
- `DataGateway.Data.SqlServer`: SQL Server query compilation, source connection validation and source execution.
- `DataGateway.Data.PostgreSql`: PostgreSQL query compilation, native parameter binding and source execution.
- `DataGateway.Data.Sqlite`: the tests-only replacement proof; see its [recorded limits](../DataGateway.Data.Sqlite/README.md#proof-boundary).
- `DataGateway.Web`: example ASP.NET Core host, app-owned catalog readers, Windows sign-in and logging providers.
- `DataGateway`: reusable registration, routes, MCP endpoint and failure logging.
- `DataGateway.Demo`: a console client that checks the wire answers.
- `DataGateway.Proxy`: the local stdio proxy to `/mcp`.
- `db/`: the DDL, catalog views, the reader login and the example seed.
- `tests/`: tests that run with no setup.
- `LICENSE` and `NOTICE`: licence and attribution.

## Libraries

[SqlKata.JsonQuery](https://github.com/mkonstan/SqlKata.JsonQuery) builds queries; the SQL Server engine uses [Dapper.Data](https://github.com/mkonstan/Dapper.Data) to run them.

## Licence

Apache License 2.0: [LICENSE](../LICENSE), [NOTICE](../NOTICE).

Designed and directed by Maxim Konstantinovski. Built with AI models (Anthropic's Claude and OpenAI's GPT) under his direction and review.

The sealed example catalog readers accept `(db, int catalogTimeoutSeconds = 15)` for their own reads; the host uses 15 seconds independently of each source query timeout.

## Catalog MCP metadata

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
