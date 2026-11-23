using DataGateway.Core.Catalog;
using DataGateway.Mcp;
using DataGateway.Data.Queries;
using SqlKata.JsonQuery;

namespace DataGateway;

/// <summary>A gateway's host policy, as checked at registration: the failure that fills an empty 401 the host leaves,
/// and the two texts the API document uses to describe sign-in.</summary>
/// <param name="Failure">The failure answered for an unrecorded empty 401; its driver exception is already dropped.</param>
/// <param name="Description">The sentence appended to the API document's description.</param>
/// <param name="ResponseDescription">The full description of each operation's 401 response.</param>
internal sealed record GatewayHostPolicy(CatalogFailure Failure, string Description, string ResponseDescription);

/// <summary>One named gateway, frozen at registration: its catalog factory, engines, limits, MCP tool settings and
/// host policy.</summary>
/// <remarks>It is registered as a singleton and also attached as endpoint metadata to every group the gateway maps, so
/// the error boundary and the API document can tell which gateway owns the endpoint a request reached.</remarks>
/// <param name="name">The gateway's ordinal, case-sensitive name.</param>
/// <param name="catalog">Builds one request's catalog adapter from services and the engine runners.</param>
/// <param name="runners">One query runner per registered engine name, matched ignoring case.</param>
internal sealed class GatewayRegistration(string name,
    Func<IServiceProvider, IReadOnlyDictionary<string, QueryRunner>, GatewayCatalog> catalog,
    IReadOnlyDictionary<string, QueryRunner> runners)
{
    /// <summary>The key of this gateway's OpenAPI document, unique per registration.</summary>
    /// <remarks>Random, not the gateway name, so gateways and a host's own OpenAPI documents never share a key. It
    /// never reaches a caller: the page serves the document at its own route, never under this name.</remarks>
    internal string DocumentName { get; } = "datagateway-" + Guid.NewGuid().ToString("N");
    /// <summary>The gateway's name: its identity in mapping, logging scopes and the MCP server info.</summary>
    internal string Name { get; } = name;
    /// <summary>The MCP tool name and description prefixes; none unless the host set them.</summary>
    internal McpToolSettings McpTools { get; init; } = new(null, null);
    /// <summary>The query limits for every source of this gateway.</summary>
    /// <remarks>The row cap is left unset here: it belongs to each source, and each operation fills it from the
    /// source's <c>RowCap</c> before it validates a query.</remarks>
    internal JsonQueryLimits Limits { get; init; } = JsonQueryLimits.Default with { MaxLimit = null };
    /// <summary>The host policy, or null when the host set none.</summary>
    internal GatewayHostPolicy? HostPolicy { get; init; }
    /// <summary>Creates a fresh catalog adapter for one request, MCP call or startup check.</summary>
    /// <param name="services">The scope's services, handed to the application's catalog factory.</param>
    /// <returns>An adapter that invokes the factory lazily, on its first read.</returns>
    internal GatewayCatalog CreateCatalog(IServiceProvider services) => catalog(services, runners);
}

/// <summary>Every registered gateway by name, and which of them have been mapped.</summary>
/// <remarks>One per service provider, so each gateway's API and page can be mapped once per provider, even through
/// different parent groups.</remarks>
internal sealed class GatewayRegistry
{
    /// <summary>The registrations by their ordinal name.</summary>
    private readonly IReadOnlyDictionary<string, GatewayRegistration> registrations;
    /// <summary>The names whose API endpoints have been mapped.</summary>
    private readonly HashSet<string> mapped = new(StringComparer.Ordinal);
    /// <summary>The names whose API page has been mapped.</summary>
    private readonly HashSet<string> pages = new(StringComparer.Ordinal);
    /// <summary>Which gateway owns each API document route, keyed without its trailing slash and ignoring case.</summary>
    /// <remarks>Two gateways whose documents resolve to the same route would make one page show the other's
    /// document.</remarks>
    private readonly Dictionary<string, string> pageRoutes = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Serializes checking and recording a mapping, so two concurrent maps of one gateway cannot both
    /// succeed.</summary>
    internal object MapLock { get; } = new();

    /// <summary>Indexes the registrations the service provider holds.</summary>
    /// <param name="registrations">Every <see cref="GatewayRegistration"/> registered by AddDataGateway.</param>
    public GatewayRegistry(IEnumerable<GatewayRegistration> registrations) =>
        this.registrations = registrations.ToDictionary(registration => registration.Name, StringComparer.Ordinal);

    /// <summary>Finds a gateway by its exact name.</summary>
    /// <param name="name">The ordinal, case-sensitive gateway name.</param>
    /// <returns>The registration, or null when no gateway has that name.</returns>
    internal GatewayRegistration? Find(string name) => registrations.GetValueOrDefault(name);
    /// <summary>Whether the gateway's API endpoints are already mapped.</summary>
    /// <param name="name">The gateway name.</param>
    internal bool IsMapped(string name) => mapped.Contains(name);
    /// <summary>Records that the gateway's API endpoints are mapped. Call under <see cref="MapLock"/>.</summary>
    /// <param name="name">The gateway name.</param>
    internal void RecordMapping(string name) => mapped.Add(name);

    /// <summary>Whether the gateway's API page is already mapped.</summary>
    /// <param name="name">The gateway name.</param>
    internal bool IsPageMapped(string name) => pages.Contains(name);
    /// <summary>Records that the gateway's API page is mapped. Call under <see cref="MapLock"/>.</summary>
    /// <param name="name">The gateway name.</param>
    internal void RecordPageMapping(string name) => pages.Add(name);

    /// <summary>Claims an API document route for a gateway, refusing one another gateway already owns.</summary>
    /// <remarks>Called when the document endpoint is built, once its full route (parent groups included) is known;
    /// at map time only the gateway's own prefix is.</remarks>
    /// <param name="name">The gateway that maps the document.</param>
    /// <param name="path">The document endpoint's complete route pattern.</param>
    /// <exception cref="InvalidOperationException">Another gateway already maps a document at that route.</exception>
    internal void RecordPageRoute(string name, string path)
    {
        lock (MapLock)
        {
            var key = path.TrimEnd('/');
            if (pageRoutes.TryGetValue(key, out var owner) && !StringComparer.Ordinal.Equals(owner, name))
                throw new InvalidOperationException($"Gateway '{name}'. API page route '{path}' is already mapped by gateway '{owner}'.");
            pageRoutes[key] = name;
        }
    }
}
