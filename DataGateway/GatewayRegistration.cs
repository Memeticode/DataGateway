using DataGateway.Core.Catalog;
using DataGateway.Mcp;
using DataGateway.Data.Queries;
using SqlKata.JsonQuery;

namespace DataGateway;

internal sealed record GatewayHostPolicy(CatalogFailure Failure, string Description, string ResponseDescription);

internal sealed class GatewayRegistration(string name,
    Func<IServiceProvider, IReadOnlyDictionary<string, QueryRunner>, GatewayCatalog> catalog,
    IReadOnlyDictionary<string, QueryRunner> runners)
{
    internal string DocumentName { get; } = "datagateway-" + Guid.NewGuid().ToString("N");
    internal string Name { get; } = name;
    internal McpToolSettings McpTools { get; init; } = new(null, null);
    internal JsonQueryLimits Limits { get; init; } = JsonQueryLimits.Default with { MaxLimit = null };
    internal GatewayHostPolicy? HostPolicy { get; init; }
    internal GatewayCatalog CreateCatalog(IServiceProvider services) => catalog(services, runners);
}

internal sealed class GatewayRegistry
{
    private readonly IReadOnlyDictionary<string, GatewayRegistration> registrations;
    private readonly HashSet<string> mapped = new(StringComparer.Ordinal);
    private readonly HashSet<string> pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> pageRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> browsePages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> browsePageRoutes = new(StringComparer.OrdinalIgnoreCase);
    internal object MapLock { get; } = new();

    public GatewayRegistry(IEnumerable<GatewayRegistration> registrations) =>
        this.registrations = registrations.ToDictionary(registration => registration.Name, StringComparer.Ordinal);

    internal GatewayRegistration? Find(string name) => registrations.GetValueOrDefault(name);
    internal bool IsMapped(string name) => mapped.Contains(name);
    internal void RecordMapping(string name) => mapped.Add(name);

    internal bool IsPageMapped(string name) => pages.Contains(name);
    internal void RecordPageMapping(string name) => pages.Add(name);

    internal bool IsBrowsePageMapped(string name) => browsePages.Contains(name);
    internal void RecordBrowsePageMapping(string name) => browsePages.Add(name);

    internal void RecordPageRoute(string name, string path) => RecordRoute(pageRoutes, "API page", name, path);
    internal void RecordBrowsePageRoute(string name, string path) => RecordRoute(browsePageRoutes, "Browse page", name, path);

    // Two gateways' pages on one effective route would make every request for it ambiguous.
    private void RecordRoute(Dictionary<string, string> routes, string page, string name, string path)
    {
        lock (MapLock)
        {
            var key = path.TrimEnd('/');
            if (routes.TryGetValue(key, out var owner) && !StringComparer.Ordinal.Equals(owner, name))
                throw new InvalidOperationException($"Gateway '{name}'. {page} route '{path}' is already mapped by gateway '{owner}'.");
            routes[key] = name;
        }
    }
}
