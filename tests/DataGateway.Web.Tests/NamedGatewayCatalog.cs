using System.Collections.Concurrent;
using System.Globalization;
using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Data.Catalog;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DataGateway.Web.Tests;

internal sealed class NamedGatewayState(SqliteFixture fixture)
{
    internal DbContext<SqliteConnection> Db { get; } = new(fixture.Connection);

    internal readonly ConcurrentQueue<(Guid Catalog, Guid Scope)> Instances = new();
    internal readonly ConcurrentQueue<(NamedGatewayCatalog Catalog, string Operation, string? Source)> Reads = new();
    internal readonly ConcurrentQueue<string> Names = new();
    internal readonly ConcurrentQueue<CancellationToken> Tokens = new();
    private int factories, lists, details;
    internal int Factories => Volatile.Read(ref factories);
    internal int Lists => Volatile.Read(ref lists);
    internal int Details => Volatile.Read(ref details);
    internal (int, int, int) Counts => (Factories, Lists, Details);
    internal Func<IServiceProvider, NamedGatewayCatalog?>? FactoryOverride { get; set; }
    internal Func<CancellationToken, Task>? BeforeList { get; set; }
    internal Func<CancellationToken, Task>? BeforeDetail { get; set; }
    internal Func<IReadOnlyList<SourceListRow>, IReadOnlyList<SourceListRow>>? ChangeList { get; set; }
    internal Func<CatalogDetail?, CatalogDetail?>? ChangeDetail { get; set; }
    internal Func<string, string> Provider { get; set; } = _ => "sqlite";
    internal bool NullListTask { get; set; }
    internal bool NullDetailTask { get; set; }

    internal NamedGatewayCatalog Create(IServiceProvider services)
    {
        Interlocked.Increment(ref factories);
        if (FactoryOverride is { } custom)
            return custom(services)!;
        return new NamedGatewayCatalog(this, services.GetRequiredService<NamedGatewayDependency>().Id);
    }

    internal void ListCall(CancellationToken token)
    {
        Interlocked.Increment(ref lists);
        Tokens.Enqueue(token);
    }

    internal void DetailCall(string name, CancellationToken token)
    {
        Interlocked.Increment(ref details);
        Names.Enqueue(name);
        Tokens.Enqueue(token);
    }
}

internal sealed class NamedGatewayCatalog : CatalogService<SqliteConnection>
{
    private readonly NamedGatewayState state;
    internal Guid Id { get; } = Guid.NewGuid();

    internal NamedGatewayCatalog(NamedGatewayState state, Guid scope) : base(state.Db)
    {
        this.state = state;
        state.Instances.Enqueue((Id, scope));
    }

    public override Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(CancellationToken cancellationToken)
    {
        state.ListCall(cancellationToken);
        return state.NullListTask ? null! : List(cancellationToken);
    }

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

    public override Task<CatalogDetail?> QueryCatalogDetailAsync(string sourceName, CancellationToken cancellationToken)
    {
        state.DetailCall(sourceName, cancellationToken);
        return state.NullDetailTask ? null! : Detail(sourceName, cancellationToken);
    }

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

    private static string Text(IDictionary<string, object?> row, string name) => (string)row[name]!;
    private static int Number(IDictionary<string, object?> row, string name) => Convert.ToInt32(row[name], CultureInfo.InvariantCulture);
}

internal sealed class NamedGatewayDependency(NamedGatewayCleanup cleanup) : IAsyncDisposable
{
    internal Guid Id { get; } = Guid.NewGuid();

    public ValueTask DisposeAsync()
    {
        cleanup.Disposed.Enqueue(Id);
        return ValueTask.CompletedTask;
    }
}

internal sealed class NamedGatewayCleanup
{
    internal ConcurrentQueue<Guid> Disposed { get; } = new();
}
