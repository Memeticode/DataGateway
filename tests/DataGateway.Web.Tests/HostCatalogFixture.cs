using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Data.Catalog;
using DataGateway.Web.Catalog;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace DataGateway.Web.Tests;

// A fresh typed application catalog per invocation. All data and runtime fault ownership is explicit.
/// <summary>A synthetic application catalog: the catalog service the host resolves, with its list, detail and MCP
/// metadata reads answered by test delegates instead of SQL Server.</summary>
/// <remarks>Its connection factory throws, so a code path that bypasses the overridden reads and tries to open the
/// real catalog database fails the test loudly instead of reaching a server.</remarks>
/// <param name="list">Answers the source list read.</param>
/// <param name="detail">Answers the detail read for one source name; <see langword="null"/> means not found.</param>
/// <param name="metadata">Answers the MCP metadata read; when omitted, the read returns <see cref="McpMetadataFixture.Seed"/>.</param>
internal sealed class HostCatalogFixture(
    Func<CancellationToken, Task<IReadOnlyList<SourceListRow>>> list,
    Func<string, CancellationToken, Task<CatalogDetail?>> detail,
    Func<CancellationToken, Task<McpMetadata>>? metadata = null)
    : CatalogService<SqlConnection>(DbContext<SqlConnection>.FromFactory(() =>
        throw new InvalidOperationException("The synthetic catalog must not open SQL Server.")))
{
    /// <summary>Returns the MCP metadata from the test delegate, or the seed metadata when none was given.</summary>
    public override Task<global::DataGateway.Core.Catalog.McpMetadata> QueryMcpMetadataAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return metadata is { } read ? read(cancellationToken) : Task.FromResult(McpMetadataFixture.Seed);
    }

    /// <summary>Returns the source list from the test delegate.</summary>
    public override Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(CancellationToken cancellationToken) => list(cancellationToken);
    /// <summary>Returns one source's detail from the test delegate.</summary>
    public override Task<CatalogDetail?> QueryCatalogDetailAsync(string sourceName, CancellationToken cancellationToken) => detail(sourceName, cancellationToken);
}

/// <summary>The catalog a test host runs on: registers a fresh <see cref="HostCatalogFixture"/> per resolution and
/// lets the test change what each read returns, inject faults and count reads.</summary>
/// <remarks>
/// <para>TERM armed: the state after the test calls <see cref="Arm"/>. Before it, reads use the healthy
/// <see cref="StartupList"/> and <see cref="StartupDetail"/>, so the host can start and warm up; after it,
/// <see cref="List"/>, <see cref="Detail"/> and <see cref="FactoryFailure"/> take effect. This keeps a fault meant for
/// a request from failing host startup instead.</para>
/// <para>The service is transient, so each resolution goes through <see cref="Create"/> and is counted. It follows
/// the host's own registration, which creates a new catalog each time one is resolved. Each test catalog is built
/// fresh around the shared test state, so every resolution can be counted and faults can be switched on after
/// startup.</para>
/// </remarks>
internal sealed class HostCatalogState
{
    /// <summary>The MCP metadata each fresh catalog returns when <see cref="ReadMetadata"/> is not set.</summary>
    internal McpMetadata Metadata { get; set; } = McpMetadataFixture.Seed;
    /// <summary>Replaces the MCP metadata read, for example with one that throws; applies whether or not the state is armed.</summary>
    internal Func<CancellationToken, Task<McpMetadata>>? ReadMetadata { get; set; }
    /// <summary>Whether <see cref="Arm"/> has been called.</summary>
    internal bool Armed { get; private set; }
    /// <summary>How many catalogs were created, and how many list and detail reads they served. Fields, so the counters
    /// can be incremented atomically.</summary>
    internal int Factories, Lists, Details;
    /// <summary>The list read used before arming, and after it when <see cref="List"/> is not set; healthy by default.</summary>
    internal Func<CancellationToken, Task<IReadOnlyList<SourceListRow>>> StartupList { get; set; } =
        _ => Task.FromResult<IReadOnlyList<SourceListRow>>([HealthyList()]);
    /// <summary>The detail read used before arming, and after it when <see cref="Detail"/> is not set; healthy by default.</summary>
    internal Func<string, CancellationToken, Task<CatalogDetail?>> StartupDetail { get; set; } =
        (name, _) => Task.FromResult<CatalogDetail?>(HealthyDetail(name));
    /// <summary>The list read used once the state is armed.</summary>
    internal Func<CancellationToken, Task<IReadOnlyList<SourceListRow>>>? List { get; set; }
    /// <summary>The detail read used once the state is armed.</summary>
    internal Func<string, CancellationToken, Task<CatalogDetail?>>? Detail { get; set; }
    /// <summary>Once armed, makes creating the catalog itself throw this exception, to model a catalog that cannot be built.</summary>
    internal Func<Exception>? FactoryFailure { get; set; }
    /// <summary>A snapshot of the three counters, for a single equality assertion.</summary>
    internal (int Factories, int Lists, int Details) Counts => (Factories, Lists, Details);
    /// <summary>Switches from the startup reads to the test's reads and faults.</summary>
    internal void Arm() => Armed = true;

    /// <summary>Registers this state as the host's SQL Server application catalog.</summary>
    /// <param name="services">The host's service collection.</param>
    internal void Use(IServiceCollection services) =>
        services.AddTransient<CatalogService<SqlConnection>>(_ => Create());

    /// <summary>Counts and creates one catalog, or throws <see cref="FactoryFailure"/> when armed.</summary>
    /// <returns>A catalog whose reads route back to this state.</returns>
    internal HostCatalogFixture Create()
    {
        Interlocked.Increment(ref Factories);
        if (Armed && FactoryFailure is { } fail) throw fail();
        return new(ReadList, ReadDetail, token => ReadMetadata is { } read ? read(token) : Task.FromResult(Metadata));
    }

    /// <summary>Counts one list read and answers it with the read the armed state selects.</summary>
    private Task<IReadOnlyList<SourceListRow>> ReadList(CancellationToken token)
    {
        Interlocked.Increment(ref Lists);
        var read = Armed ? List ?? StartupList : StartupList;
        return read(token);
    }

    /// <summary>Counts one detail read and answers it with the read the armed state selects.</summary>
    private Task<CatalogDetail?> ReadDetail(string name, CancellationToken token)
    {
        Interlocked.Increment(ref Details);
        var read = Armed ? Detail ?? StartupDetail : StartupDetail;
        return read(name, token);
    }

    /// <summary>Resolves the query runner the root gateway uses for one source, the same way a request does.</summary>
    /// <param name="services">The host's services.</param>
    /// <param name="source">The source name to target.</param>
    /// <returns>The runner; the test fails if the source cannot be targeted.</returns>
    internal static async Task<DataGateway.Data.Queries.QueryRunner> NamedRunner(IServiceProvider services, string source = "synthetic")
    {
        var registration = services.GetRequiredService<GatewayRegistry>().Find("DataGateway.Web")
            ?? throw new InvalidOperationException("Root gateway is missing.");
        var target = await registration.CreateCatalog(services).TargetAsync(source, CancellationToken.None);
        Xunit.Assert.True(target.IsSuccess, target.Failure?.Message);
        return target.Value.Runner;
    }

    /// <summary>A list row for one healthy, available source.</summary>
    /// <param name="name">The source name.</param>
    internal static SourceListRow HealthyList(string name = "synthetic") =>
        new(name, "Synthetic", "Synthetic", "One row", true, 500);

    /// <summary>A healthy SQL Server source detail, mapped from the synthetic catalog rows by the production mapper.</summary>
    /// <param name="name">The source name written into every row.</param>
    internal static CatalogDetail HealthyDetail(string name = "synthetic")
    {
        var rows = ErrorLogTests.Rows();
        foreach (var row in rows) row["name"] = name;
        return CatalogTableReader.MapDetail(rows, "sqlserver", true);
    }

    /// <summary>A state whose armed reads return raw catalog rows, mapped and validated by the production
    /// <c>CatalogTableReader</c>, so a test can exercise malformed catalog data end to end.</summary>
    /// <param name="list">Returns the raw list rows.</param>
    /// <param name="detail">Returns the raw detail rows and, when no detail row matches, the rows of available names,
    /// which must be well formed before the read reports the source as not found.</param>
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
