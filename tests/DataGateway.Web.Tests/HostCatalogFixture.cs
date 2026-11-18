using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Data.Catalog;
using DataGateway.Web.Catalog;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace DataGateway.Web.Tests;

// A fresh typed application catalog per invocation. All data and runtime fault ownership is explicit.
internal sealed class HostCatalogFixture(
    Func<CancellationToken, Task<IReadOnlyList<SourceListRow>>> list,
    Func<string, CancellationToken, Task<CatalogDetail?>> detail,
    Func<CancellationToken, Task<McpMetadata>>? metadata = null)
    : CatalogService<SqlConnection>(DbContext<SqlConnection>.FromFactory(() =>
        throw new InvalidOperationException("The synthetic catalog must not open SQL Server.")))
{
    public override Task<global::DataGateway.Core.Catalog.McpMetadata> QueryMcpMetadataAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return metadata is { } read ? read(cancellationToken) : Task.FromResult(McpMetadataFixture.Seed);
    }

    public override Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(CancellationToken cancellationToken) => list(cancellationToken);
    public override Task<CatalogDetail?> QueryCatalogDetailAsync(string sourceName, CancellationToken cancellationToken) => detail(sourceName, cancellationToken);
}

internal sealed class HostCatalogState
{
    internal McpMetadata Metadata { get; set; } = McpMetadataFixture.Seed;
    internal Func<CancellationToken, Task<McpMetadata>>? ReadMetadata { get; set; }
    internal bool Armed { get; private set; }
    internal int Factories, Lists, Details;
    internal Func<CancellationToken, Task<IReadOnlyList<SourceListRow>>> StartupList { get; set; } =
        _ => Task.FromResult<IReadOnlyList<SourceListRow>>([HealthyList()]);
    internal Func<string, CancellationToken, Task<CatalogDetail?>> StartupDetail { get; set; } =
        (name, _) => Task.FromResult<CatalogDetail?>(HealthyDetail(name));
    internal Func<CancellationToken, Task<IReadOnlyList<SourceListRow>>>? List { get; set; }
    internal Func<string, CancellationToken, Task<CatalogDetail?>>? Detail { get; set; }
    internal Func<Exception>? FactoryFailure { get; set; }
    internal (int Factories, int Lists, int Details) Counts => (Factories, Lists, Details);
    internal void Arm() => Armed = true;

    internal void Use(IServiceCollection services) =>
        services.AddTransient<CatalogService<SqlConnection>>(_ => Create());

    internal HostCatalogFixture Create()
    {
        Interlocked.Increment(ref Factories);
        if (Armed && FactoryFailure is { } fail) throw fail();
        return new(ReadList, ReadDetail, token => ReadMetadata is { } read ? read(token) : Task.FromResult(Metadata));
    }

    private Task<IReadOnlyList<SourceListRow>> ReadList(CancellationToken token)
    {
        Interlocked.Increment(ref Lists);
        var read = Armed ? List ?? StartupList : StartupList;
        return read(token);
    }

    private Task<CatalogDetail?> ReadDetail(string name, CancellationToken token)
    {
        Interlocked.Increment(ref Details);
        var read = Armed ? Detail ?? StartupDetail : StartupDetail;
        return read(name, token);
    }

    internal static async Task<DataGateway.Data.Queries.QueryRunner> NamedRunner(IServiceProvider services, string source = "synthetic")
    {
        var registration = services.GetRequiredService<GatewayRegistry>().Find("DataGateway.Web")
            ?? throw new InvalidOperationException("Root gateway is missing.");
        var target = await registration.CreateCatalog(services).TargetAsync(source, CancellationToken.None);
        Xunit.Assert.True(target.IsSuccess, target.Failure?.Message);
        return target.Value.Runner;
    }

    internal static SourceListRow HealthyList(string name = "synthetic") =>
        new(name, "Synthetic", "Synthetic", "One row", true, 500);

    internal static CatalogDetail HealthyDetail(string name = "synthetic")
    {
        var rows = ErrorLogTests.Rows();
        foreach (var row in rows) row["name"] = name;
        return CatalogTableReader.MapDetail(rows, "sqlserver", true);
    }

    internal static HostCatalogState Raw(
        Func<CancellationToken, Task<IReadOnlyList<dynamic>>> list,
        Func<string, CancellationToken, Task<(IReadOnlyList<dynamic> Rows, IReadOnlyList<dynamic> Available)>> detail) => new()
    {
        List = async token => CatalogTableReader.MapList(await list(token)),
        Detail = async (name, token) =>
        {
            var batch = await detail(name, token);
            if (batch.Rows.Count != 0) return CatalogTableReader.MapDetail(batch.Rows, "sqlserver", true);
            CatalogTableReader.ValidateAvailable(batch.Available);
            return null;
        }
    };
}
