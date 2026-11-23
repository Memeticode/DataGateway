using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace DataGateway.Page;

/// <summary>Renames the API document's generated response properties to the names DataGateway's replies use.</summary>
/// <remarks>Microsoft.AspNetCore.OpenApi builds schemas with the host's JSON settings and has no per-document options,
/// while every reply is written with GatewayJson. Naming is what a generated client binds to, so names follow the wire.
/// A host's number handling only changes how loosely a number is described; every reply still matches the schema.
/// The generator walks into nested schemas by the host's property names, so renames are collected while it builds the
/// schemas and applied once the document is complete. Out of scope: a host converter or resolver modifier for
/// DataGateway's own response types, and a host transformer that adds schemas to this document after DataGateway's;
/// the response types have no structs or polymorphism.</remarks>
internal static class GatewaySchemaNames
{
    /// <summary>The renames collected for each document under construction.</summary>
    /// <remarks>Weakly keyed, so a document that never reaches <see cref="Apply"/> does not keep its renames
    /// alive.</remarks>
    private static readonly ConditionalWeakTable<OpenApiDocument, List<Action>> Pending = new();

    /// <summary>Schema transformer: records how one object schema's properties are named on the wire.</summary>
    /// <param name="schema">The generated schema.</param>
    /// <param name="context">The generator's context, with the host's type info for the schema.</param>
    /// <param name="token">Unused.</param>
    /// <returns>A completed task.</returns>
    internal static Task Collect(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken token)
    {
        // Object schemas arrive both on their own and through a property (then JsonPropertyInfo is set); both count.
        if (context.Document is not { } document || context.JsonTypeInfo is not { Kind: JsonTypeInfoKind.Object } host
            || schema.Properties is not { Count: > 0 })
            return Task.CompletedTask;

        var wire = GatewayJson.Default.Options.GetTypeInfo(host.Type).Properties
            .Where(property => property.AttributeProvider is MemberInfo)
            .ToDictionary(property => (MemberInfo)property.AttributeProvider!, property => property.Name);
        var names = host.Properties
            .Where(property => property.AttributeProvider is MemberInfo member && wire.ContainsKey(member))
            .ToDictionary(property => property.Name, property => wire[(MemberInfo)property.AttributeProvider!], StringComparer.Ordinal);
        if (names.All(pair => pair.Key == pair.Value))
            return Task.CompletedTask;

        var pending = Pending.GetOrCreateValue(document);
        lock (pending) pending.Add(() => Rename(schema, names));
        return Task.CompletedTask;
    }

    /// <summary>Document transformer: applies the collected renames once every schema exists.</summary>
    /// <param name="document">The finished document.</param>
    internal static void Apply(OpenApiDocument document)
    {
        if (!Pending.TryGetValue(document, out var pending)) return;
        Pending.Remove(document);
        foreach (var rename in pending) rename();
    }

    /// <summary>Renames one schema's properties and required names to their wire names.</summary>
    /// <param name="schema">The schema.</param>
    /// <param name="names">Host property name to wire name; names not listed stay.</param>
    private static void Rename(OpenApiSchema schema, Dictionary<string, string> names)
    {
        var renamed = new Dictionary<string, IOpenApiSchema>(schema.Properties!.Count, StringComparer.Ordinal);
        foreach (var (name, value) in schema.Properties!)
            renamed[names.GetValueOrDefault(name, name)] = value;
        schema.Properties = renamed;
        if (schema.Required is { Count: > 0 } required)
            schema.Required = new HashSet<string>(required.Select(name => names.GetValueOrDefault(name, name)), StringComparer.Ordinal);
    }
}
