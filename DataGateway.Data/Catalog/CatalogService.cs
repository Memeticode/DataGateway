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
/// <typeparam name="TConnection">The catalog database's connection type.</typeparam>
public abstract class CatalogService<TConnection> where TConnection : DbConnection, new()
{
    /// <summary>Takes the context the catalog queries run on.</summary>
    /// <param name="db">The application's catalog context; its factory supplies a fresh closed connection per
    /// scope.</param>
    protected CatalogService(DbContext<TConnection> db)
    {
        ArgumentNullException.ThrowIfNull(db);
        Db = db;
    }

    /// <summary>The catalog context the overrides query through.</summary>
    protected DbContext<TConnection> Db { get; }

    /// <summary>Returns the complete MCP server and tool text, detached from the reader.</summary>
    /// <param name="cancellationToken">Ends the read when the caller goes away.</param>
    /// <returns>The server title and about text, and the three tools' titles and descriptions.</returns>
    public abstract Task<McpMetadata> QueryMcpMetadataAsync(CancellationToken cancellationToken);

    /// <summary>Returns list metadata, activity and row caps.</summary>
    /// <remarks>Inactive rows may be included: every row is validated, but only active sources are listed.</remarks>
    /// <param name="cancellationToken">Ends the read when the caller goes away.</param>
    /// <returns>One row per catalog source.</returns>
    public abstract Task<IReadOnlyList<SourceListRow>> QueryCatalogAsync(
        CancellationToken cancellationToken);

    /// <summary>Returns a source's complete detail, or null when detail is absent.</summary>
    /// <remarks>The application receives sourceName verbatim and chooses its lookup comparison.</remarks>
    /// <param name="sourceName">The source name the caller asked for, as sent.</param>
    /// <param name="cancellationToken">Ends the read when the caller goes away.</param>
    /// <returns>The source's provider, source row and column rows; null when no such source exists.</returns>
    public abstract Task<CatalogDetail?> QueryCatalogDetailAsync(
        string sourceName, CancellationToken cancellationToken);
}
