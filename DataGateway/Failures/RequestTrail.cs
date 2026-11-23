using DataGateway.Core.Queries;
using DataGateway.Core.Catalog;

namespace DataGateway.Failures;

/// <summary>What the current request has reached, kept per request for the catch-all's one line.</summary>
/// <remarks>Each step records what it reached as it goes, so an exception that escapes later can still be logged with
/// the body, the source and the compiled query that led to it. HTTP requests keep it as a request feature; each MCP
/// tool call keeps its own.</remarks>
internal sealed class RequestTrail
{
    /// <summary>The request body, once read in full.</summary>
    internal byte[]? Body { get; set; }
    /// <summary>The compiled SQL and parameters, once the query is compiled.</summary>
    internal CompiledQuery? Query { get; set; }
    /// <summary>The source, once its catalog detail is loaded.</summary>
    internal SourceContract? Source { get; set; }

    /// <summary>The request's trail; direct route tests receive an empty trail.</summary>
    /// <remarks>Created on first use and kept as a request feature, so every later step sees the same trail.</remarks>
    /// <param name="context">The request.</param>
    /// <returns>The request's trail.</returns>
    internal static RequestTrail Of(HttpContext context)
    {
        if (context.Features.Get<RequestTrail>() is { } trail) return trail;
        trail = new();
        context.Features.Set(trail);
        return trail;
    }
}
