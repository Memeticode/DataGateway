using System.Collections.Concurrent;
using DataGateway.Core.Catalog;

namespace DataGateway.Data.SqlServer;

/// <summary>The SQL Server store-type check: parses each catalog <c>store_type</c>, compares it with the type the
/// source's view declares, and says which store types a source's filter values may bind by.</summary>
/// <remarks>
/// <para>Why check at all: a store type the view contradicts would bind values as the wrong type, which can cut text or
/// change results. So a column binds by its store type only when the view agrees, or when the catalog says
/// <c>store_type_override</c>; anything else binds as before (preview.6) and is reported.</para>
/// <para>When: on the first query of a source in this process, not at host start, so a source that is down never stops
/// the host. The view's types are read once per source (connection and object name) and kept for the life of the
/// process; concurrent first queries share that one read. The catalog side is compared on every query, so a corrected
/// catalog takes effect at once.</para>
/// <para>How long: the whole read, connect included, is bounded by one deadline of the lesser of
/// <see cref="LookupTimeoutSeconds"/> and the source's own query timeout, so the first query waits at most that long
/// beyond its own run. The deadline belongs to the shared read, not to any caller; a read that outlives it counts as
/// failed even if the driver ignores the cancellation.</para>
/// <para>A failed read is kept only for <see cref="RetryInterval"/>, counted from when it began (a read that failed at
/// its deadline is retried sooner after its failure): long enough that a down source is not asked again
/// on every request, short enough that it recovers within a minute of coming back. Until a read succeeds, every column
/// of that source binds as before. Each problem is reported once per process. A failed read is reported on its first
/// failure, then not again for that source until a read succeeds, or after <see cref="FailureReportInterval"/>: a view
/// that stays unreadable would otherwise log an Error every 30 seconds. A source object that is not one of the connected
/// database's own (a name with a database or server part, or a synonym for a remote or missing object) is not retried
/// at all in this process: waiting cannot fix it.</para>
/// </remarks>
internal sealed class SqlServerStoreTypes(
    Func<SourceContract, CancellationToken, Task<IReadOnlyList<SqlServerColumnType>>> read, TimeProvider time,
    int lookupTimeoutSeconds = SqlServerStoreTypes.LookupTimeoutSeconds)
{
    /// <summary>How long a failed read stands, timed from when that read began, before the next query of that source
    /// tries again.</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>How often, at most, a source whose reads keep failing has that failure logged again.</summary>
    internal static readonly TimeSpan FailureReportInterval = TimeSpan.FromHours(1);

    /// <summary>The longest the metadata query may run: the source's own query timeout when shorter.</summary>
    internal const int LookupTimeoutSeconds = 15;

    private const string Fix = "Run the fill tool (SqlServerStoreTypeScript.CreateAsync) or correct store_type.";
    private static readonly IReadOnlyDictionary<string, StoreType> None =
        new Dictionary<string, StoreType>(StringComparer.OrdinalIgnoreCase).AsReadOnly();

    private readonly Lock gate = new();
    private readonly Dictionary<(string Connection, string Object), Lookup> lookups = new();
    private readonly Dictionary<(string Connection, string Object), DateTimeOffset> failureReported = new();
    private readonly ConcurrentDictionary<string, StoreType?> parsed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> reported = new(StringComparer.Ordinal);

    /// <summary>Production reader: the engine's metadata query on the source's own connection.</summary>
    internal static SqlServerStoreTypes ForSources(TimeProvider time) => new((source, token) =>
        SqlServerColumnTypes.ReadAsync(source.ConnectionString, source.ObjectName,
            Math.Min(LookupTimeoutSeconds, source.QueryTimeoutSeconds), token), time);

    /// <summary>The deadline of one source's whole read: the lesser of the lookup limit and the source's query timeout.</summary>
    internal TimeSpan Deadline(SourceContract source) =>
        TimeSpan.FromSeconds(Math.Min(lookupTimeoutSeconds, source.QueryTimeoutSeconds));

    internal async Task<IReadOnlyDictionary<string, StoreType>> ResolveAsync(SourceContract source,
        Action<StoreTypeFinding> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(report);
        var name = source.Source.Name;
        var catalog = new Dictionary<string, (CatalogStoreType Cell, StoreType Type)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, cell) in source.StoreTypes)
        {
            if (Parse(cell.Text) is { } type) catalog.Add(column, (cell, type));
            else Once(report, $"unreadable|{name}|{column}|{cell.Text}", new(StoreTypeSeverity.Warning, name, column,
                $"Source '{name}', column '{column}': store_type is not a SQL Server type DataGateway can read, so the " +
                $"column binds as if store_type were empty. {Fix}"));
        }
        if (catalog.Count == 0) return None;

        var lookup = Current(source);
        IReadOnlyList<SqlServerColumnType> view;
        try
        {
            view = await lookup.Columns.WaitAsync(cancellationToken).ConfigureAwait(false);
            ReadSucceeded(source);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller has gone; the query run that follows reports the cancellation itself.
            return None;
        }
        catch (Exception error)
        {
            if (error is UnresolvedObjectException ? lookup.TryClaimReport() : ClaimFailureReport(source))
                Report(report, new(StoreTypeSeverity.Error, name, null, error is UnresolvedObjectException
                    ? $"Source '{name}': DataGateway could not read the column types of its view ({ErrorName(error)}), so " +
                      "every column binds as if store_type were empty. It does not try again until the host restarts. " +
                      "Point object_name at a view in the source's own database (schema.view or view), or set store_type " +
                      "to NULL."
                    : $"Source '{name}': DataGateway could not read the column types of its view ({ErrorName(error)}), so " +
                      $"every column binds as if store_type were empty. It tries again on a query at least " +
                      $"{(int)RetryInterval.TotalSeconds} seconds after this read began, and logs this again only after a read succeeds, or after " +
                      $"{(int)FailureReportInterval.TotalMinutes} minutes. Check that the view exists and that the source " +
                      "login may see its definition (VIEW DEFINITION)."));
            return None;
        }

        var bound = new Dictionary<string, StoreType>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, (cell, type)) in catalog)
        {
            var key = $"{name}|{column}|{type.Spelling}|{cell.Override}";
            var (actual, ambiguous) = SqlServerColumnTypes.Match(view, column);
            if (ambiguous)
            {
                // Like a mismatch, and an override cannot settle it: there is no knowing which column it overrides.
                Once(report, "ambiguous|" + key, new(StoreTypeSeverity.Error, name, column,
                    $"Source '{name}', column '{column}': the catalog's store_type is {type.Spelling} but the view has more " +
                    "than one column of that name in different letter case and none spelled exactly the same, so the column " +
                    "binds as if store_type were empty. Spell column_name in the catalog exactly as the view does."));
                continue;
            }
            if (actual is null)
            {
                Once(report, "missing|" + key, new(StoreTypeSeverity.Error, name, column,
                    $"Source '{name}', column '{column}': the catalog's store_type is {type.Spelling} but the view has no " +
                    $"such column, so the column binds as if store_type were empty. {Fix}"));
                continue;
            }
            var declaredType = actual.StoreType;
            var databaseSpelling = declaredType?.Spelling ?? actual.Spelling;
            if (declaredType is not null && Same(type, declaredType))
                bound.Add(column, type with { Utf8Collation = actual.Utf8Collation });
            else if (cell.Override)
            {
                Once(report, $"override|{key}|{databaseSpelling}", new(StoreTypeSeverity.Warning, name, column,
                    $"Source '{name}', column '{column}': the catalog's store_type is {type.Spelling} but the view's " +
                    $"column is {databaseSpelling}; store_type_override is set, so filter values bind as {type.Spelling}. " +
                    $"{Fix}"));
                bound.Add(column, type with { Utf8Collation = actual.Utf8Collation });
            }
            else
                Once(report, $"mismatch|{key}|{databaseSpelling}", new(StoreTypeSeverity.Error, name, column,
                    $"Source '{name}', column '{column}': the catalog's store_type is {type.Spelling} but the view's " +
                    $"column is {databaseSpelling}, so the column binds as if store_type were empty. {Fix}"));
        }
        return bound.AsReadOnly();
    }

    /// <summary>True when two store types describe the same SQL Server type, with SQL Server's defaults filled in: a
    /// bare <c>decimal</c> is <c>decimal(18,0)</c>, <c>datetime2</c> is <c>datetime2(7)</c>, <c>float</c> is
    /// <c>float(53)</c>, and <c>float(1)</c> to <c>float(24)</c> are <c>real</c>. Names SQL Server treats as one type
    /// are equal: <c>decimal</c> and <c>numeric</c> (at the same precision and scale), and <c>rowversion</c> and
    /// <c>timestamp</c>, the name sys.types reports for every rowversion column.</summary>
    internal static bool Same(StoreType catalog, StoreType database) => Normal(catalog) == Normal(database);

    private static (string Name, int? Length, bool Max, int? Precision, int? Scale) Normal(StoreType type) => type.Name switch
    {
        "decimal" or "numeric" => ("decimal", null, false, type.Precision ?? 18, type.Scale ?? 0),
        "rowversion" or "timestamp" => ("rowversion", null, false, null, null),
        "datetime2" or "time" or "datetimeoffset" => (type.Name, null, false, type.Precision ?? 7, null),
        "float" when type.Precision is <= 24 => ("real", null, false, null, null),
        "float" => (type.Name, null, false, 53, null),
        _ => (type.Name, type.Length, type.IsMax, type.Precision, type.Scale)
    };

    // A failed read is logged on its first failure, then again only after a read of that source has succeeded, or once
    // FailureReportInterval has passed; concurrent callers of one failed read claim the report once between them.
    private bool ClaimFailureReport(SourceContract source)
    {
        var key = (source.ConnectionString, source.ObjectName);
        var now = time.GetUtcNow();
        lock (gate)
        {
            if (failureReported.TryGetValue(key, out var last) && now - last < FailureReportInterval) return false;
            failureReported[key] = now;
            return true;
        }
    }

    private void ReadSucceeded(SourceContract source)
    {
        lock (gate) failureReported.Remove((source.ConnectionString, source.ObjectName));
    }

    private StoreType? Parse(string text) => parsed.GetOrAdd(text, static value => StoreType.TryParse(value, out var type) ? type : null);

    // One read per source, shared by concurrent first queries; a failed read is replaced once RetryInterval has passed.
    private Lookup Current(SourceContract source)
    {
        var key = (source.ConnectionString, source.ObjectName);
        lock (gate)
        {
            if (lookups.TryGetValue(key, out var lookup) && !lookup.Expired(time.GetUtcNow()))
                return lookup;
            var fresh = new Lookup(Read(source), time.GetUtcNow());
            lookups[key] = fresh;
            return fresh;
        }
    }

    // Runs without the caller's token: the read is shared, so one caller leaving must not cancel it for the others. Its
    // own deadline cancels the read, and the wait also ends at the deadline, in case a driver ignores the cancellation.
    private async Task<IReadOnlyList<SqlServerColumnType>> Read(SourceContract source)
    {
        await Task.Yield();
        var deadline = Deadline(source);
        using var expiry = new CancellationTokenSource(deadline, time);
        IReadOnlyList<SqlServerColumnType> columns;
        try { columns = await read(source, expiry.Token).WaitAsync(deadline, time).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException || (error is OperationCanceledException && expiry.IsCancellationRequested))
        {
            // Whichever timer fired first, tell the read to stop: the wait has already ended.
            expiry.Cancel();
            throw new TimeoutException();
        }
        if (columns.Count == 0) throw new ViewNotVisibleException();
        return columns;
    }

    private void Once(Action<StoreTypeFinding> report, string key, StoreTypeFinding finding)
    {
        if (reported.TryAdd(key, 0)) Report(report, finding);
    }

    // A reporter that throws never changes the query: the finding is lost, the query goes on.
    private static void Report(Action<StoreTypeFinding> report, StoreTypeFinding finding)
    {
        try { report(finding); }
        catch (Exception) { }
    }

    private static string ErrorName(Exception error) =>
        error is ViewNotVisibleException ? "the view was not found, or its definition is not visible to the login"
        : SqlServerColumnTypes.Describe(error);

    private sealed class ViewNotVisibleException() : Exception("The view has no visible columns.");

    private sealed class Lookup(Task<IReadOnlyList<SqlServerColumnType>> columns, DateTimeOffset started)
    {
        private int claimed;
        internal Task<IReadOnlyList<SqlServerColumnType>> Columns { get; } = columns;
        // An object that is not the connected database's own stays failed for the life of the process.
        internal bool Expired(DateTimeOffset now) =>
            Columns.IsCompleted && !Columns.IsCompletedSuccessfully &&
            Columns.Exception?.InnerException is not UnresolvedObjectException && now - started >= RetryInterval;
        internal bool TryClaimReport() => Interlocked.Exchange(ref claimed, 1) == 0;
    }
}
