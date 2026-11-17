# Library configuration

Start with the [complete new-app walkthrough](README.md#add-it-to-a-new-app). This guide describes the public library. The [example host settings](DataGateway.Web/CONFIGURATION.md) belong to DataGateway.Web. The app owns access policy, catalog storage and logging providers.

## Register a gateway

`AddDataGateway(this IServiceCollection services, string name, Action<DataGatewayBuilder> configure)` returns the original service collection. Gateway names must be nonblank. They are ordinal case-sensitive and preserve accepted whitespace. Configuration must supply a catalog and at least one engine. Failed configuration is not committed. Its builder freezes even on failure. Every builder method returns that builder.

| Builder method | Rule |
| --- | --- |
| `UseCatalog<TConnection>(Func<IServiceProvider, CatalogService<TConnection>> factory)` | Last assignment wins. `TConnection` must satisfy `DbConnection, new()`. Null fails immediately. |
| `AddEngine(IDatabaseEngine engine)` | Registers under `engine.ProviderName`. |
| `AddEngine(IDatabaseEngine engine, string name)` | Registers an alias for an engine variant. Names are nonblank, preserve accepted whitespace and are unique ignoring case. |
| `UseLimits(GatewayLimits limits)` | Replaces all five settings. Omitted properties use defaults. Null fails immediately; final ranges are checked at registration. |
| `UseMcpServer(string? title, string? about)` | Last assignment wins. Blank is unset. Nonblank raw values reject CR/LF and may not exceed 80 UTF-8 bytes for title or 120 for about. Validation precedes trimming. |
| `UseHostPolicy(CatalogFailure failure, string description, string responseDescription)` | Configures the fallback failure and page wording. It does not enforce access. |

`CatalogDetail.Provider` selects an engine alias ordinally ignoring case. Aliases are not trimmed. There is no runtime DLL discovery. Apps own catalog/context and engine instances; DI owns scoped dependencies. Shared instances require safe concurrent access and stable result objects.

The catalog factory is lazy. It receives scoped services at most once per HTTP invocation needing catalog data. Awaited startup uses a separate scope for each registration, including unmapped gateways. The app must return detached results. See [catalog readers](CATALOG-READERS.md).

MCP name and version use the registration name and DataGateway assembly version. About prefixes the fixed instructions with one space. Title also supplies the API page/document title; otherwise the gateway name is used.

The final host-policy failure must map to its own client-visible 401 code. Both description texts must be nonblank; accepted text is preserved. Null inputs fail immediately. `DriverException` is discarded. The host supplies caller-safe `Message`, `Path`, `Step` and `ExceptionType`, and attaches authentication and authorization itself.

## Set limits

| Property | Default | Inclusive range |
| --- | --- | --- |
| MaxJsonLength | 65536 characters | 1..2147483647 |
| MaxFilterDepth | 8 | 0..30 |
| MaxFilterConditions | 50 | 1..2147483647 |
| MaxInValues | 100 | 1..2147483647 |

JSON length counts query text in characters. MCP rebuilds compact JSON from its argument. Depth zero allows flat filters. Conditions count leaves. IN limits count raw array entries. Each source's `RowCap` is 1..2147483646 and appears in describe as `querySchema.limit.maximum`. Source command timeout is distinct from the app reader's catalog command timeout and provider connection/retry options. The library does not retry source queries.

## Map endpoints

`MapDataGateway(this IEndpointRouteBuilder endpoints, string name, string prefix)` returns a `RouteGroupBuilder` with POST `/sources/list`, `/sources/describe`, `/query` and `/mcp` endpoints.

`MapDataGatewayApiPage(this IEndpointRouteBuilder endpoints, string name, string prefix)` returns an independent `RouteGroupBuilder` for `/scalar/`, its redirect and assets, and `/openapi/v1.json`.

Both require a registered name. Each permits one successful mapping per gateway/service provider. Prefixes are route patterns. Empty and slash prefixes map at the root. Parent-group prefixes compose with them. Invalid prefixes do not consume a mapping. Duplicate effective document routes are refused ignoring case and trailing slashes when endpoints are constructed.

The host decides environments. Apply `RequireAuthorization` to both returned groups where wanted. Page policy covers the redirect, document and assets. Registration alone leaves access open.

For multiple gateways, register names `sales` and `inventory`, map APIs under `/sales` and `/inventory`, and map each page with its matching prefix. Catalogs, engines, limits, MCP identity and policy are per registration. Distinct API prefixes avoid route ambiguity.

## Place error handling

For selected gateway endpoints, call `UseRouting()`, then `UseDataGatewayErrors()`, then the host authentication/authorization middleware. This covers downstream middleware on selected API, MCP and page endpoints. Earlier middleware and unmatched routes remain host-owned.

For an outer host boundary, call `UseDataGatewayErrors(name)` before routing. The named registration supplies fallback error handling for unmatched paths, wrong methods and host endpoints. It does not create a failure merely because a route is missing. After routing, selected gateway metadata overrides the named fallback. Duplicate boundaries share one active boundary and finish once. Both overloads return the application pipeline builder.

`TryRecordDataGatewayRefusal(this HttpContext context, CatalogFailure failure)` accepts only the first provisional refusal in an active boundary. It returns false without one, after a record, after response start or after caller departure. Recording creates no response, incident or event. The host must finish with an otherwise empty 401, leaving content type and content length unset. Normal completion of a recorded refusal logs once and includes an incident. An unrecorded fallback challenge can have a body without an incident or failure event. Challenge headers belong to the host.

## Configure logging providers

The library uses `ILogger` category `DataGateway.Failure`, exposed by `DataGatewayExtensions.FailureLogCategory`. Its `Gateway` scope identifies the registration. Apps choose providers, sinks, retention and filters. Successful calls have no gateway failure event. Failure events are Error while connected and Warning after departure. No exception object is passed to `ILogger`.

| State field | Meaning |
| --- | --- |
| IncidentId | New 32-character lowercase hex incident ID for the attempted failure event. |
| Caller | Authenticated identity name, or anonymous; an authenticated identity without a name can be null. |
| Operation | Route operation or tool name; null when no operation is known. |
| Request | Bounded UTF-8 body text, with replacement characters for invalid bytes; MCP uses the argument JSON. |
| RequestBytes | Full byte count of the recorded body, even when Request is cut. |
| RequestCut | Whether recorded text was cut. |
| Sql | Compiled SQL when reached; otherwise null. |
| SqlParameters | Named parameter values when compiled SQL is present; otherwise null. |
| Code | Original internal failure code. |
| Path | Original failure path. |
| Message | Original diagnostic or caller-safe failure message. |
| ExceptionType | Exception type name when available. |
| SqlNumber | Recognized provider number when available. |
| Step | Reached phase, such as connect, command, catalog, request, query, sign-in or response; otherwise null. |
| ReturnedCode | Public envelope code, which can differ from Code after redaction. |
| SourceId | Optional reached catalog diagnostic ID; otherwise null. |
| SourceName | Reached source or reader failure's source name; otherwise null. |
| DriverMessage | Driver message on expected failures; otherwise null. |
| ExceptionText | Full exception text on unexpected failures only. |
| {OriginalFormat} | Fixed failure message template. |

Request fields are null when no complete body was recorded. Request text is bounded to `MaxJsonLength+1024` characters, capped at `int.MaxValue`, without splitting surrogate pairs. Full byte count and bounded character count are different measures. Diagnostic driver/exception text is preserved; providers and host policies must avoid exposing credentials. The source parser uses a safe replacement message for unparseable connection text. No catalog connection or source connection field is deliberately projected to the event.

Catalog invocation exceptions map to `CatalogUnavailable`. Cancellation is internally `CatalogCancelled`; while connected it is redacted by the public envelope to `InternalError`. Requested source cancellation is `QueryCancelled` internally. A disconnected caller gets no error body. Once a response has started, failure handling aborts the connection. An incomplete body read or an unrecorded challenge need not log. Cancellation is forwarded and does not promise immediate provider interruption or an event before a failure exists.

## Check registration, mapping and startup

Registration checks names, the final builder settings, a catalog and at least one engine. Mapping separately checks registration, route patterns and one mapping of each kind. Await host startup; it validates the list and every active detail's shape, mapped columns and registered provider, even for unmapped gateways. Missing or inactive required detail prevents startup. Startup does not check source connectivity. Source connection validation and execution happen on the query path. `HostCatalogs` supplies additional catalog connection checks for the example host.

Each source's catalog `query_timeout_seconds` controls its query deadline and provider command timeout, from connection open to the last kept row. This required integer defaults to 30 and accepts 1 through 86400. It is detail-only and absent from wire answers. The host refuses the retired `DataGateway:Limits:CommandTimeoutSeconds` setting; remove it and set the source column. For existing catalogs run `db/catalog-query-timeout.sql` or `db/postgresql/catalog-query-timeout.sql`. The example catalog constructors accept `(db, int catalogTimeoutSeconds = 15)` for their own reads, independently of source deadlines.
