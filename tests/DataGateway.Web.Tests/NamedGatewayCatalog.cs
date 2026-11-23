using System.Collections.Concurrent;
using System.Globalization;
using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Data.Catalog;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DataGateway.Web.Tests;

/// <summary>The shared state behind one test gateway's catalog: it creates a <see cref="NamedGatewayCatalog"/> per
/// request scope, counts and records every catalog read, and holds the hooks a test uses to delay, change or fail
/// those reads.</summary>
/// <remarks>The state outlives the per-request catalogs, so a test can prove how many catalogs the gateway created,
/// which scope each came from, and that a refused or cached request read nothing.</remarks>
internal sealed class NamedGatewayState(SqliteFixture fixture)
{
    /// <summary>The catalog database: the fixture's SQLite file.</summary>
    internal DbContext<SqliteConnection> Db { get; } = new(fixture.Connection);

    /// <summary>Every catalog created, with the id of the scoped dependency it was created with.</summary>
    internal readonly ConcurrentQueue<(Guid Catalog, Guid Scope)> Instances = new();
    /// <summary>Every catalog read in order: <c>list</c>, <c>detail</c> (with the source name) or <c>metadata</c>.</summary>
    internal readonly ConcurrentQueue<(NamedGatewayCatalog Catalog, string Operation, string? Source)> Reads = new();
    /// <summary>Every source name a detail read asked for.</summary>
    internal readonly ConcurrentQueue<string> Names = new();
    /// <summary>The cancellation token each list or detail read received.</summary>
    internal readonly ConcurrentQueue<CancellationToken> Tokens = new();
    /// <summary>The call counts, updated atomically.</summary>
    private int factories, lists, details, metadataReads;
    /// <summary>How many times the MCP metadata was read.</summary>
    internal int MetadataReads => Volatile.Read(ref metadataReads);
    /// <summary>The MCP metadata the catalog returns; the shared seed unless a test changes it.</summary>
    internal McpMetadata Metadata { get; set; } = McpMetadataFixture.Seed;
    /// <summary>When set, replaces the metadata read, so a test can delay or fail it.</summary>
    internal Func<CancellationToken, Task<McpMetadata>>? ReadMetadata { get; set; }
    /// <summary>Counts and records one metadata read.</summary>
    internal void MetadataCall(NamedGatewayCatalog catalog)
    {
        Interlocked.Increment(ref metadataReads);
        Reads.Enqueue((catalog, "metadata", null));
    }
    /// <summary>How many times the catalog factory ran.</summary>
    internal int Factories => Volatile.Read(ref factories);
    /// <summary>How many list reads started.</summary>
    internal int Lists => Volatile.Read(ref lists);
    /// <summary>How many detail reads started.</summary>
    internal int Details => Volatile.Read(ref details);
    /// <summary>Factory, list and detail counts together, so a test can assert a request did no catalog work.</summary>
    internal (int, int, int) Counts => (Factories, Lists, Details);
    /// <summary>When set, replaces the factory, so a test can return null or throw.</summary>
    internal Func<IServiceProvider, NamedGatewayCatalog?>? FactoryOverride { get; set; }
    /// <summary>Runs before each list query, so a test can hold the read in flight.</summary>
    internal Func<CancellationToken, Task>? BeforeList { get; set; }
    /// <summary>Runs before each detail query, so a test can hold the read in flight.</summary>
    internal Func<CancellationToken, Task>? BeforeDetail { get; set; }
    /// <summary>Rewrites the list rows, so a test can feed the gateway an invalid catalog.</summary>
    internal Func<IReadOnlyList<SourceListRow>, IReadOnlyList<SourceListRow>>? ChangeList { get; set; }
    /// <summary>Rewrites the detail, so a test can feed the gateway an invalid or missing source.</summary>
    internal Func<CatalogDetail?, CatalogDetail?>? ChangeDetail { get; set; }
    /// <summary>The engine provider name a source's detail names; <c>sqlite</c> unless a test changes it.</summary>
    internal Func<string, string> Provider { get; set; } = _ => "sqlite";
    /// <summary>Makes the list read return a null task, a broken catalog the gateway must survive.</summary>
    internal bool NullListTask { get; set; }
    /// <summary>Makes the detail read return a null task, a broken catalog the gateway must survive.</summary>
    internal bool NullDetailTask { get; set; }

    /// <summary>The catalog factory registered with the gateway: counts the call and creates a catalog bound to the
    /// request's scoped <see cref="NamedGatewayDependency"/>.</summary>
    internal NamedGatewayCatalog Create(IServiceProvider services)
    {
        Interlocked.Increment(ref factories);
        if (FactoryOverride is { } custom)
            return custom(services)!;
        return new NamedGatewayCatalog(this, services.GetRequiredService<NamedGatewayDependency>().Id);
    }

    /// <summary>Counts one list read and keeps its token.</summary>
    internal void ListCall(CancellationToken token)
    {
        Interlocked.Increment(ref lists);
        Tokens.Enqueue(token);
    }

    /// <summary>Counts one detail read and keeps the name and token.</summary>
    internal void DetailCall(string name, CancellationToken token)
    {
        Interlocked.Increment(ref details);
        Names.Enqueue(name);
        Tokens.Enqueue(token);
    }
}

/// <summary>A test catalog that reads the fixture's <c>gateway_source</c> and <c>gateway_column</c> tables with
/// Dapper, as an application's catalog would, and reports every call to its <see cref="NamedGatewayState"/>.</summary>
internal sealed class NamedGatewayCatalog : CatalogService<SqliteConnection>
{
    /// <summary>Returns the state's MCP metadata, or the state's replacement read, after recording the call.</summary>
    public override Task<global::DataGateway.Core.Catalog.McpMetadata> QueryMcpMetadataAsync(CancellationToken cancellationToken)
    {
        state.MetadataCall(this);
        cancellationToken.ThrowIfCancellationRequested();
        return state.ReadMetadata is { } read ? read(cancellationToken) : Task.FromResult(state.Metadata);
    }

    /// <summary>The shared state this catalog reports to.</summary>
    private readonly NamedGatewayState state;
    /// <summary>This instance's id, so tests can tell catalogs apart.</summary>
    internal Guid Id { get; } = Guid.NewGuid();

    /// <summary>Creates a catalog and records it with the scope it came from.</summary>
    internal NamedGatewayCatalog(NamedGatewayState state, Guid scope) : base(state.Db)
    {
        this.state = state;
        state.Instances.Enqueue((Id, scope));
    }

    /// <summary>Records the call, then lists the sources, or returns a null task when the state asks for one.</summary>
    public override Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(CancellationToken cancellationToken)
    {
        state.ListCall(cancellationToken);
        return state.NullListTask ? null! : List(cancellationToken);
    }

    /// <summary>Reads the source list rows and applies <see cref="NamedGatewayState.ChangeList"/>.</summary>
    private async Task<IReadOnlyList<SourceListRow>> List(CancellationToken token)
    {
        state.Reads.Enqueue((this, "list", null));
        if (state.BeforeList is { } before)
            await before(token);
        var rows = await Db.QueryAsync("SELECT name,title,description,grain,is_active,row_cap FROM gateway_source ORDER BY id LIMIT 100;", cancellationToken: token);
        IReadOnlyList<SourceListRow> result = rows.Select(raw =>
        {
            var row = (IDictionary<string, object?>)raw;
            return new SourceListRow(Text(row, "name"), Text(row, "title"), Text(row, "description"), Text(row, "grain"), Number(row, "is_active") != 0, Number(row, "row_cap"));
        }).ToArray();
        return state.ChangeList is { } change ? change(result) : result;
    }

    /// <summary>Records the call, then reads one source, or returns a null task when the state asks for one.</summary>
    public override Task<CatalogDetail?> QueryCatalogDetailAsync(string sourceName, CancellationToken cancellationToken)
    {
        state.DetailCall(sourceName, cancellationToken);
        return state.NullDetailTask ? null! : Detail(sourceName, cancellationToken);
    }

    /// <summary>Reads one source and its columns by name, ignoring case, and applies
    /// <see cref="NamedGatewayState.ChangeDetail"/>; null when no source matches.</summary>
    private async Task<CatalogDetail?> Detail(string name, CancellationToken token)
    {
        state.Reads.Enqueue((this, "detail", name));
        if (state.BeforeDetail is { } before)
            await before(token);
        var rows = await Db.QueryAsync("SELECT * FROM gateway_source WHERE name = @name COLLATE NOCASE LIMIT 1;", new { name }, cancellationToken: token);
        CatalogDetail? result = null;
        if (rows.Count > 0)
        {
            var row = (IDictionary<string, object?>)rows[0];
            var id = Number(row, "id");
            var columns = await Db.QueryAsync("SELECT * FROM gateway_column WHERE datasource_fk = @id ORDER BY ordinal LIMIT 100;", new { id }, cancellationToken: token);
            result = new CatalogDetail(state.Provider(name), new CatalogRows(new DataSourceRow(Text(row, "object_name"), Text(row, "connection_string")) { QueryTimeoutSeconds = Number(row, "query_timeout_seconds"), Id = id, Name = Text(row, "name"), Title = Text(row, "title"), Description = Text(row, "description"), Grain = Text(row, "grain"), IsActive = Number(row, "is_active") != 0, RowCap = Number(row, "row_cap") }, columns.Select(raw =>
            {
                var col = (IDictionary<string, object?>)raw;
                return new DataSourceColumnRow(Text(col, "column_name"), Number(col, "ordinal"), Text(col, "title"), Text(col, "description"), Text(col, "data_type"), col["unit"] as string, col["magnitude"] as string, col["display_format"] as string, Number(col, "can_select") != 0, Number(col, "can_filter") != 0, Number(col, "can_sort") != 0, Number(col, "can_group") != 0, Number(col, "can_aggregate") != 0);
            }).ToArray()));
        }

        return state.ChangeDetail is { } change ? change(result) : result;
    }

    /// <summary>Reads a text column.</summary>
    private static string Text(IDictionary<string, object?> row, string name) => (string)row[name]!;
    /// <summary>Reads a SQLite integer column as an <see cref="int"/>.</summary>
    private static int Number(IDictionary<string, object?> row, string name) => Convert.ToInt32(row[name], CultureInfo.InvariantCulture);
}

/// <summary>A request-scoped service the catalog factory depends on; it reports its disposal.</summary>
/// <remarks>Proves the gateway creates each catalog inside a request scope and that the scope is disposed.</remarks>
internal sealed class NamedGatewayDependency(NamedGatewayCleanup cleanup) : IAsyncDisposable
{
    /// <summary>This scope's id.</summary>
    internal Guid Id { get; } = Guid.NewGuid();

    /// <summary>Records the disposal.</summary>
    public ValueTask DisposeAsync()
    {
        cleanup.Disposed.Enqueue(Id);
        return ValueTask.CompletedTask;
    }
}

/// <summary>The host-wide record of disposed <see cref="NamedGatewayDependency"/> ids.</summary>
internal sealed class NamedGatewayCleanup
{
    /// <summary>The disposed ids in order.</summary>
    internal ConcurrentQueue<Guid> Disposed { get; } = new();
}
