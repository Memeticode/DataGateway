using DataGateway.Core.Catalog;
using DataGateway.Data.Queries;

namespace DataGateway.Operations;

internal sealed class QuerySourceContext(SourceContract source, QueryRunner runner)
{
    internal SourceContract Source { get; } = source;
    internal QueryRunner Runner { get; } = runner;
}
