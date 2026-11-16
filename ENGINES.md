# Writing a database engine

Engines compile and execute source queries. Catalog readers belong to the app; see [catalog readers](CATALOG-READERS.md). Use the [README walkthrough](README.md#add-it-to-a-new-app) for a complete composition and [configuration](CONFIGURATION.md) for aliases and limits.

## Implement the five members

`DataGateway.Data.IDatabaseEngine` has exactly five members:

| Member | Full signature |
| --- | --- |
| ProviderName | `string ProviderName { get; }` |
| Compile | `CompiledQuery Compile(ValidatedQuery query)` |
| ValidateSourceConnection | `void ValidateSourceConnection(string? connection)` |
| RecognizeFailure | `ProviderFailure? RecognizeFailure(Exception error)` |
| ReadSourceAsync | `Task<QueryRows> ReadSourceAsync(string connection, CompiledQuery query, int commandTimeoutSeconds, Action opened, Func<IAsyncEnumerable<IDictionary<string, object?>>, Task<QueryRows>> consume, CancellationToken cancellationToken)` |

`ProviderFailure(bool IsTimeout, int? Number)` carries provider timeout and error-number facts. Return null from recognition for an unrecognized exception.

Prefer a concrete class deriving from `DatabaseEngine<TConnection>` and implementing `IDatabaseEngine`, with a `ProviderName` and a `Func<string, TConnection> createConnection` passed to the protected base constructor. `TConnection` must satisfy `DbConnection, new()`. The factory supplies a fresh closed connection for each call. The five protected abstract hooks are:

| Hook | Full signature |
| --- | --- |
| CreateCompiler | `Compiler CreateCompiler()` |
| MatchedRowCountExpression | `string MatchedRowCountExpression { get; }` |
| PrepareLikePattern | `(string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern)` |
| PrepareParameter | `void PrepareParameter(DbParameter parameter, object? value)` |
| ConvertCell | `object ConvertCell(object value, OutputColumn? column)` |

Also implement public abstract `void ValidateSourceConnection(string? connection)` and `ProviderFailure? RecognizeFailure(Exception error)`.

## Reuse compilation and row bounds

The base clones `ValidatedQuery` through `CloneQuery()`. It fetches `RowLimit.Fetch`, which is keep+1, and uses declared output columns. It prepares provider LIKE patterns and named parameter bindings. Only `%` is a wildcard in the public vocabulary; provider escaping must preserve literal underscore and bracket behavior. The base binds CLR integers directly. The provider prepares other parameter values and converts non-null cells according to output metadata.

Ungrouped aggregates include matched-row evidence. The expression must return a nonnegative CLR long under `CompiledQuery.MatchedRowCountColumn`. The bounded consumer removes that private field and records the empty-source meaning without changing aggregate values.

`CompiledQuery` carries SQL, named parameters, Keep, HasMatchedRowCount and Columns. `OutputColumn` supplies Name/DataType. `QueryRows` carries detached rows, More and NoMatchingRows. These metadata must agree with the compiled result.

The public scoped `ReadSourceAsync` takes a consumer. The base's internally selected capped path supplies its own bounded consumer. Direct `IDatabaseEngine` implementations also receive a bounded `KeepAsync` consumer from QueryRunner; that path is capped too. Deriving from the base reuses compilation, bounded reading and lifecycle behavior. Direct implementations must honor compatible metadata and the supplied consumer.

## Keep resource scope awaited

Await one consumer scope with single enumeration. Return detached ordered row dictionaries. Convert database nulls to null; reject duplicate column names. Forward the command timeout and cancellation token. Invoke `opened` once after successful open and before command creation. Complete only after attempted cleanup of every acquired resource. Primary exceptions take precedence over cleanup exceptions. Saved streams and enumerators must not escape the consumer callback or start work that outlives it; materialized rows survive.

`ValidateSourceConnection` performs local syntax and required-field checks without opening. Caller-safe ArgumentException/FormatException becomes `InvalidCatalog`. Execution preserves the supplied string. `RecognizeFailure` returns timeout/number facts or null. Expected failures before successful open become source unavailability unless local connection syntax requires InvalidCatalog. Post-open timeout is `QueryTimeout`. Requested cancellation is internally `QueryCancelled`; an uncancelled OperationCanceledException is unexpected. These rules use the existing error vocabulary, including its client redaction. Provider interruption need not be immediate.

## Read the shipped implementations

| Engine | ProviderName | Status and local requirements |
| --- | --- | --- |
| [SqlServerEngine](DataGateway.Data.SqlServer/SqlServerEngine.cs) | `sqlserver` | Supported. [Source validation](DataGateway.Data.SqlServer/SqlServerEngine.Source.cs) requires a server. The example host separately requires an explicit catalog database. |
| [PostgreSqlEngine](DataGateway.Data.PostgreSql/PostgreSqlEngine.cs) | `postgresql` | Supported. Requires explicit host/database/username. |
| [SqliteEngine](DataGateway.Data.Sqlite/SqliteEngine.cs) | `sqlite` | Tests-only. Requires a file data source, ReadOnly and pooling disabled. See the [finite proof boundary](DataGateway.Data.Sqlite/README.md#proof-boundary). |

Register distinct aliases to select variants per source. No new public error-code design or runtime discovery is needed.
