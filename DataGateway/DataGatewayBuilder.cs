using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using System.Data.Common;
using System.Text;
using SqlKata.JsonQuery;
using DataGateway.Data;
using DataGateway.Data.Catalog;
using DataGateway.Data.Queries;

namespace DataGateway;

/// <summary>Configures one named gateway during AddDataGateway. Registration freezes this builder.</summary>
public sealed class DataGatewayBuilder
{
    private readonly string name;
    private readonly Dictionary<string, IDatabaseEngine> engines = new(StringComparer.OrdinalIgnoreCase);
    private Func<IServiceProvider, IReadOnlyDictionary<string, QueryRunner>, GatewayCatalog>? catalog;
    private bool frozen;
    private string? mcpTitle;
    private string? mcpAbout;
    private GatewayLimits limits = new();
    private GatewayHostPolicy? hostPolicy;

    internal DataGatewayBuilder(string name) => this.name = name;

    /// <summary>Sets the MCP title and about line. The final assignment is validated during registration.</summary>
    /// <remarks>Blank values are unset. Nonblank input is checked before trimming, with UTF-8 limits of
    /// 80 bytes for title and 120 for about. CR and LF are refused in nonblank input.</remarks>
    public DataGatewayBuilder UseMcpServer(string? title, string? about)
    {
        CheckMutable();
        mcpTitle = title;
        mcpAbout = about;
        return this;
    }

    /// <summary>Sets the application catalog factory; the last assignment wins during configuration.</summary>
    /// <remarks>The factory runs lazily at most once per HTTP invocation, with request services, after valid
    /// parsing requires a read. Startup invokes it in a separate scope and validates every active detail before
    /// the server starts, including unmapped gateways. The application owns created catalogs and supplied contexts;
    /// DI owns dependencies resolved from its scopes. Fresh wrappers isolate mutable state; applications returning
    /// a shared catalog are responsible for concurrent access to overrides and result objects.</remarks>
    public DataGatewayBuilder UseCatalog<TConnection>(Func<IServiceProvider, CatalogService<TConnection>> factory)
        where TConnection : DbConnection, new()
    {
        CheckMutable();
        ArgumentNullException.ThrowIfNull(factory);
        catalog = (services, runners) =>
        {
            var instance = new Lazy<CatalogService<TConnection>>(() =>
                factory(services) ?? throw new InvalidOperationException());
            return new GatewayCatalog(token => instance.Value.QueryCatalogAsync(token),
                (source, token) => instance.Value.QueryCatalogDetailAsync(source, token), runners);
        };
        return this;
    }

    /// <summary>Registers an application-owned engine under its ProviderName, matched ordinally ignoring case.</summary>
    public DataGatewayBuilder AddEngine(IDatabaseEngine engine)
    {
        CheckMutable();
        ArgumentNullException.ThrowIfNull(engine);
        return AddEngine(engine, engine.ProviderName);
    }

    /// <summary>Registers an application-owned engine under an alias, preserving spelling and whitespace.</summary>
    /// <remarks>Names must be non-blank and unique ignoring case within this gateway. Startup refuses any
    /// active catalog detail whose provider has no matching engine. Distinct aliases support engine variants.</remarks>
    public DataGatewayBuilder AddEngine(IDatabaseEngine engine, string name)
    {
        CheckMutable();
        ArgumentNullException.ThrowIfNull(engine);
        if (string.IsNullOrWhiteSpace(name)) throw Refuse("Engine name must contain a non-whitespace character.");
        if (!engines.TryAdd(name, engine)) throw Refuse($"Engine '{name}' is already registered.");
        return this;
    }

    /// <summary>Sets all five limits for this gateway. The last assignment is validated during registration.</summary>
    /// <remarks>Unspecified properties receive their defaults. A null argument is refused immediately.
    /// Registration freezes this setting together with the other builder settings.</remarks>
    public DataGatewayBuilder UseLimits(GatewayLimits limits)
    {
        CheckMutable();
        ArgumentNullException.ThrowIfNull(limits);
        this.limits = limits;
        return this;
    }

    /// <summary>Sets this gateway's fallback policy failure and its two API document texts.</summary>
    /// <remarks>The final assignment is validated when registration completes: the failure must map to its own
    /// client-visible 401 code and both texts must be nonblank. Nonblank text is preserved exactly. Null inputs
    /// fail immediately. The builder freezes even when registration fails. Access enforcement and ending a refused
    /// request belong to the host. Supply caller-safe Message, Path and Step values and an exception type name
    /// for ExceptionType; the raw driver exception is discarded.</remarks>
    /// <param name="failure">The fallback failure for an unrecorded empty 401.</param>
    /// <param name="description">The sentence appended to the page document description.</param>
    /// <param name="responseDescription">The complete description of each operation's 401 response.</param>
    /// <returns>The same builder.</returns>
    public DataGatewayBuilder UseHostPolicy(CatalogFailure failure, string description, string responseDescription)
    {
        CheckMutable();
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(responseDescription);
        hostPolicy = new(failure with { DriverException = null }, description, responseDescription);
        return this;
    }

    private GatewayHostPolicy? ReadHostPolicy()
    {
        if (hostPolicy is null)
            return null;
        var reply = ErrorEnvelope.From(hostPolicy.Failure, null);
        if (reply.Status != StatusCodes.Status401Unauthorized || reply.Body.Code != hostPolicy.Failure.Code)
            throw Refuse("Policy failure must have a client-visible 401 code.");
        if (string.IsNullOrWhiteSpace(hostPolicy.Description))
            throw Refuse("Policy description must contain a non-whitespace character.");
        if (string.IsNullOrWhiteSpace(hostPolicy.ResponseDescription))
            throw Refuse("Policy response description must contain a non-whitespace character.");
        return hostPolicy;
    }

    internal void Freeze() => frozen = true;

    internal GatewayRegistration Complete()
    {
        if (catalog is null) throw Refuse("Configure a catalog with UseCatalog.");
        if (engines.Count == 0) throw Refuse("Register at least one engine with AddEngine.");
        var title = ReadMcpLine(mcpTitle, "title", 80);
        var about = ReadMcpLine(mcpAbout, "about", 120);
        var queryLimits = ReadLimits();
        return new GatewayRegistration(name, catalog, CreateRunners(limits.CommandTimeoutSeconds), title, about)
        {
            Limits = queryLimits,
            HostPolicy = ReadHostPolicy()
        };
    }

    private JsonQueryLimits ReadLimits()
    {
        CheckLimit(limits.CommandTimeoutSeconds, nameof(GatewayLimits.CommandTimeoutSeconds), 1, int.MaxValue);
        CheckLimit(limits.MaxJsonLength, nameof(GatewayLimits.MaxJsonLength), 1, int.MaxValue);
        CheckLimit(limits.MaxFilterDepth, nameof(GatewayLimits.MaxFilterDepth), 0, JsonQueryLimits.None.MaxFilterDepth);
        CheckLimit(limits.MaxFilterConditions, nameof(GatewayLimits.MaxFilterConditions), 1, int.MaxValue);
        CheckLimit(limits.MaxInValues, nameof(GatewayLimits.MaxInValues), 1, int.MaxValue);
        return JsonQueryLimits.Default with
        {
            MaxLimit = null,
            MaxJsonLength = limits.MaxJsonLength,
            MaxFilterDepth = limits.MaxFilterDepth,
            MaxFilterConditions = limits.MaxFilterConditions,
            MaxInValues = limits.MaxInValues
        };
    }

    private void CheckLimit(int value, string field, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
            throw Refuse(FormattableString.Invariant($"{field} must be an integer from {minimum} to {maximum}."));
    }

    private string? ReadMcpLine(string? value, string field, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Contains('\n') || value.Contains('\r') || Encoding.UTF8.GetByteCount(value) > maxBytes)
            throw Refuse($"MCP {field} must be one line of at most {maxBytes} bytes (UTF-8).");
        return value.Trim();
    }

    private IReadOnlyDictionary<string, QueryRunner> CreateRunners(int commandTimeoutSeconds) =>
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, QueryRunner>(
            engines.ToDictionary(pair => pair.Key, pair => new QueryRunner(pair.Value, commandTimeoutSeconds), StringComparer.OrdinalIgnoreCase));

    private void CheckMutable()
    {
        if (frozen) throw Refuse("Registration is already complete.");
    }

    private InvalidOperationException Refuse(string reason) => new($"Gateway '{name}'. {reason}");
}
