using DataGateway.Core.Catalog;
using DataGateway.Data.Queries;

namespace DataGateway.Operations;

/// <summary>What the query operation needs from the catalog: the source's contract and the runner of the engine that
/// serves it.</summary>
/// <param name="source">The source's contract.</param>
/// <param name="runner">The runner for the engine the source's detail names.</param>
internal sealed class QuerySourceContext(SourceContract source, QueryRunner runner)
{
    /// <summary>The source's contract: its columns, flags, row cap and query timeout.</summary>
    internal SourceContract Source { get; } = source;
    /// <summary>Compiles and runs queries on the source's engine.</summary>
    internal QueryRunner Runner { get; } = runner;
}
