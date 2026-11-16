using DataGateway.Core.Queries;
using DataGateway.Core.Catalog;

namespace DataGateway.Failures;

/// <summary>What the current request has reached, kept per request for the catch-all's one line.</summary>
internal sealed class RequestTrail
{
    internal byte[]? Body { get; set; }
    internal CompiledQuery? Query { get; set; }
    internal SourceContract? Source { get; set; }

    /// <summary>The request's trail; direct route tests receive an empty trail.</summary>
    internal static RequestTrail Of(HttpContext context)
    {
        if (context.Features.Get<RequestTrail>() is { } trail) return trail;
        trail = new();
        context.Features.Set(trail);
        return trail;
    }
}
