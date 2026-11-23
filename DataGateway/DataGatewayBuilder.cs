using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using DataGateway.Mcp;
using SqlKata.JsonQuery;
using DataGateway.Data;
using DataGateway.Data.Catalog;
using DataGateway.Data.Queries;

namespace DataGateway;

/// <summary>Configures one named gateway during AddDataGateway. Registration freezes this builder.</summary>
/// <remarks>Setters only record values; the checks run once, when registration completes, so the last value
/// is the one checked and a failed configuration registers nothing. Every method returns this builder.</remarks>
public sealed class DataGatewayBuilder
{
    /// <summary>The gateway name, quoted in every refusal.</summary>
    private readonly string name;
    /// <summary>The registered engines by name, unique ignoring case.</summary>
    private readonly Dictionary<string, IDatabaseEngine> engines = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Builds a request's catalog adapter; null until <see cref="UseCatalog{TConnection}"/> is called.</summary>
    private Func<IServiceProvider, IReadOnlyDictionary<string, QueryRunner>, GatewayCatalog>? catalog;
    /// <summary>Set once AddDataGateway has run the configuration; every setter then refuses.</summary>
    private bool frozen;
    /// <summary>The MCP tool name prefix as supplied, checked at registration.</summary>
    private string? mcpNamePrefix;
    /// <summary>The MCP tool description prefix as supplied, checked at registration.</summary>
    private string? mcpDescriptionPrefix;
    /// <summary>The query limits as supplied, checked at registration.</summary>
    private GatewayLimits limits = new();
    /// <summary>The host policy as supplied, checked at registration; null when none is set.</summary>
    private GatewayHostPolicy? hostPolicy;

    /// <summary>Starts an empty configuration for one gateway.</summary>
    /// <remarks>Internal: only AddDataGateway creates a builder, so it can freeze and complete it.</remarks>
    /// <param name="name">The gateway name, already checked by AddDataGateway.</param>
    internal DataGatewayBuilder(string name) => this.name = name;

    /// <summary>Sets optional MCP tool name and description prefixes. The last call wins.</summary>
    /// <remarks>Blank values are unset. Registration validates nonblank values as supplied: names use
    /// 1-24 lowercase ASCII letters, digits or underscores, start with a letter and never end with an underscore.
    /// Descriptions are one line of at most 80 UTF-8 bytes ending with a period. Registration freezes this setting.</remarks>
    /// <param name="namePrefix">Joined to each tool name with an underscore, and applied to every tool name the
    /// catalog's MCP text mentions; null or blank for none.</param>
    /// <param name="descriptionPrefix">Put before each tool description, separated by one space; null or blank for
    /// none.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="InvalidOperationException">Registration is already complete.</exception>
    public DataGatewayBuilder UseMcpTools(string? namePrefix = null, string? descriptionPrefix = null)
    {
        CheckMutable();
        mcpNamePrefix = namePrefix;
        mcpDescriptionPrefix = descriptionPrefix;
        return this;
    }

    /// <summary>Sets the application catalog factory; the last assignment wins during configuration.</summary>
    /// <remarks>The factory runs lazily at most once per HTTP invocation, with request services, after valid
    /// parsing requires a read on JSON routes; each stateless MCP POST also reads presentation metadata. Startup
    /// invokes it in a separate scope and validates MCP metadata and every active detail before
    /// the server starts, including unmapped gateways. The application owns created catalogs and supplied contexts;
    /// DI owns dependencies resolved from its scopes. Fresh wrappers isolate mutable state; applications returning
    /// a shared catalog are responsible for concurrent access to overrides and result objects.</remarks>
    /// <typeparam name="TConnection">The ADO.NET connection type the catalog reads with.</typeparam>
    /// <param name="factory">Creates the catalog service from the current scope's services. Returning null is a
    /// catalog failure.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Registration is already complete.</exception>
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
                (source, token) => instance.Value.QueryCatalogDetailAsync(source, token), runners,
                token => instance.Value.QueryMcpMetadataAsync(token));
        };
        return this;
    }

    /// <summary>Registers an application-owned engine under its ProviderName, matched ordinally ignoring case.</summary>
    /// <remarks>Each catalog detail names the engine that runs its source; see the overload that takes a name.</remarks>
    /// <param name="engine">The engine; the application owns it and its lifetime.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The name is blank or already registered, or registration is
    /// already complete.</exception>
    public DataGatewayBuilder AddEngine(IDatabaseEngine engine)
    {
        CheckMutable();
        ArgumentNullException.ThrowIfNull(engine);
        return AddEngine(engine, engine.ProviderName);
    }

    /// <summary>Registers an application-owned engine under an alias, preserving spelling and whitespace.</summary>
    /// <remarks>Names must be non-blank and unique ignoring case within this gateway. Startup refuses any
    /// active catalog detail whose provider has no matching engine. Distinct aliases support engine variants.</remarks>
    /// <param name="engine">The engine; the application owns it and its lifetime.</param>
    /// <param name="name">The name a catalog detail's provider uses to select this engine.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The name is blank or already registered, or registration is
    /// already complete.</exception>
    public DataGatewayBuilder AddEngine(IDatabaseEngine engine, string name)
    {
        CheckMutable();
        ArgumentNullException.ThrowIfNull(engine);
        if (string.IsNullOrWhiteSpace(name)) throw Refuse("Engine name must contain a non-whitespace character.");
        if (!engines.TryAdd(name, engine)) throw Refuse($"Engine '{name}' is already registered.");
        return this;
    }

    /// <summary>Sets all four limits for this gateway. The last assignment is validated during registration.</summary>
    /// <remarks>Unspecified properties receive their defaults. A null argument is refused immediately.
    /// Registration freezes this setting together with the other builder settings.</remarks>
    /// <param name="limits">The limits; see <see cref="GatewayLimits"/> for each default and range.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="limits"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Registration is already complete.</exception>
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
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">Registration is already complete.</exception>
    public DataGatewayBuilder UseHostPolicy(CatalogFailure failure, string description, string responseDescription)
    {
        CheckMutable();
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(responseDescription);
        hostPolicy = new(failure with { DriverException = null }, description, responseDescription);
        return this;
    }

    /// <summary>Checks the final host policy.</summary>
    /// <remarks>The failure is put through the public error envelope: a code the envelope would redact, or answer with
    /// another status, could not be the 401 that the host's refusal is answered and documented with.</remarks>
    /// <returns>The policy, or null when none was set.</returns>
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

    /// <summary>Ends configuration; any later setter call refuses.</summary>
    /// <remarks>Called even when the configure callback throws, so a builder that escaped the callback cannot change
    /// anything afterwards.</remarks>
    internal void Freeze() => frozen = true;

    /// <summary>Checks the final settings and produces the frozen registration.</summary>
    /// <returns>The registration AddDataGateway adds to the services.</returns>
    /// <exception cref="InvalidOperationException">A setting is missing or invalid; the message names the
    /// gateway.</exception>
    internal GatewayRegistration Complete()
    {
        if (catalog is null) throw Refuse("Configure a catalog with UseCatalog.");
        if (engines.Count == 0) throw Refuse("Register at least one engine with AddEngine.");
        var queryLimits = ReadLimits();
        return new GatewayRegistration(name, catalog, CreateRunners())
        {
            McpTools = ReadMcpTools(),
            Limits = queryLimits,
            HostPolicy = ReadHostPolicy()
        };
    }

    /// <summary>Checks the MCP prefixes; a blank prefix means none.</summary>
    /// <remarks>No trailing underscore: the prefix is joined to each tool name with one.</remarks>
    /// <returns>The prefixes as supplied, or null for each one that is blank.</returns>
    private McpToolSettings ReadMcpTools()
    {
        var namePrefix = string.IsNullOrWhiteSpace(mcpNamePrefix) ? null : mcpNamePrefix;
        if (namePrefix is not null && (!Regex.IsMatch(namePrefix, @"\A[a-z][a-z0-9_]{0,23}\z") || namePrefix.EndsWith('_')))
            throw Refuse("MCP namePrefix must be 1-24 lowercase ASCII letters, digits or underscores, start with a letter and not end with an underscore.");
        var descriptionPrefix = string.IsNullOrWhiteSpace(mcpDescriptionPrefix) ? null : mcpDescriptionPrefix;
        _ = ReadMcpLine(descriptionPrefix, "descriptionPrefix", 80);
        if (descriptionPrefix is not null && !descriptionPrefix.EndsWith('.'))
            throw Refuse("MCP descriptionPrefix must end with a period.");
        var settings = new McpToolSettings(namePrefix, descriptionPrefix);
        return settings;
    }

    /// <summary>Checks each limit's range and turns them into the query library's limits.</summary>
    /// <remarks>The filter depth's maximum is the query library's own ceiling. The row cap stays unset: each source
    /// supplies its own.</remarks>
    /// <returns>The limits for every source of this gateway.</returns>
    private JsonQueryLimits ReadLimits()
    {
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

    /// <summary>Refuses a limit outside its inclusive range.</summary>
    /// <param name="value">The supplied value.</param>
    /// <param name="field">The property name, for the message.</param>
    /// <param name="minimum">The smallest accepted value.</param>
    /// <param name="maximum">The largest accepted value.</param>
    private void CheckLimit(int value, string field, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
            throw Refuse(FormattableString.Invariant($"{field} must be an integer from {minimum} to {maximum}."));
    }

    /// <summary>Refuses a prefix that spans lines or is too long in UTF-8.</summary>
    /// <param name="value">The supplied text; null or blank passes.</param>
    /// <param name="field">The parameter name, for the message.</param>
    /// <param name="maxBytes">The largest accepted UTF-8 byte count.</param>
    /// <returns>The trimmed text, or null when blank.</returns>
    private string? ReadMcpLine(string? value, string field, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Contains('\n') || value.Contains('\r') || Encoding.UTF8.GetByteCount(value) > maxBytes)
            throw Refuse($"MCP {field} must be one line of at most {maxBytes} bytes (UTF-8).");
        return value.Trim();
    }

    /// <summary>Wraps each engine in a query runner, keyed by its registered name ignoring case.</summary>
    /// <returns>A read-only copy of the engine set.</returns>
    private IReadOnlyDictionary<string, QueryRunner> CreateRunners() =>
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, QueryRunner>(
            engines.ToDictionary(pair => pair.Key, pair => new QueryRunner(pair.Value), StringComparer.OrdinalIgnoreCase));

    /// <summary>Refuses a setter call once configuration has ended.</summary>
    private void CheckMutable()
    {
        if (frozen) throw Refuse("Registration is already complete.");
    }

    /// <summary>A configuration error that names the gateway.</summary>
    /// <param name="reason">What is wrong and how to fix it.</param>
    /// <returns>The exception to throw.</returns>
    private InvalidOperationException Refuse(string reason) => new($"Gateway '{name}'. {reason}");
}
