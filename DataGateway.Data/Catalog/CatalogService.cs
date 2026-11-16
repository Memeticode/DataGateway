using System.Data.Common;
using Dapper.Data;
using DataGateway.Core.Catalog;

namespace DataGateway.Data.Catalog;

/// <summary>Application-owned catalog queries over an injected Dapper.Data context.</summary>
/// <remarks>
/// The application owns the supplied context and returns buffered, detached catalog results.
/// Implementations observe cancellation and forward the supplied token to asynchronous work.
/// Cancellation propagates as OperationCanceledException. Returned data awaits catalog validation.
/// </remarks>
public abstract class CatalogService<TConnection> where TConnection : DbConnection, new()
{
    protected CatalogService(DbContext<TConnection> db)
    {
        ArgumentNullException.ThrowIfNull(db);
        Db = db;
    }

    protected DbContext<TConnection> Db { get; }

    /// <summary>Returns list metadata, activity and row caps.</summary>
    public abstract Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(
        CancellationToken cancellationToken);

    /// <summary>Returns a source's complete detail, or null when detail is absent.</summary>
    /// <remarks>The application receives sourceName verbatim and chooses its lookup comparison.</remarks>
    public abstract Task<CatalogDetail?> QueryCatalogDetailAsync(
        string sourceName, CancellationToken cancellationToken);
}
