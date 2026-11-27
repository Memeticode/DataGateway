using DataGateway.Core.Catalog;

namespace DataGateway.Data;

/// <summary>An engine that binds filter values as their columns' catalog <c>store_type</c> and checks those types
/// against the source database before trusting them.</summary>
/// <remarks>
/// <para>store_type is written in the source database's own spelling, so only an engine for that database can read it:
/// <see cref="Queries.QueryRunner.BindStoreTypesAsync"/> asks only an engine that implements this, and every other
/// engine's sources bind as before without ever looking at the text. The SQL Server engine implements it.</para>
/// <para>The implementation owns its own caching: it is called for every query of a source that has store types, and
/// should read the database at most once per source per process.</para>
/// </remarks>
public interface IStoreTypeEngine
{
    /// <summary>The store types this source's filter values may bind by, keyed by column name ignoring case. A column
    /// that is absent binds as if its <c>store_type</c> were empty.</summary>
    /// <param name="source">The source contract, whose <see cref="SourceContract.StoreTypes"/> are the catalog's text.</param>
    /// <param name="report">Receives each problem found (unreadable text, a type the database contradicts, a failed
    /// lookup), for the host to log. The engine decides how often the same problem is reported.</param>
    /// <param name="cancellationToken">The caller's request token.</param>
    /// <remarks>Never throws for a source database problem: a failed lookup is reported, and the source then binds as
    /// if it had no store types.</remarks>
    Task<IReadOnlyDictionary<string, StoreType>> ResolveStoreTypesAsync(SourceContract source,
        Action<StoreTypeFinding> report, CancellationToken cancellationToken);
}

/// <summary>How serious a <see cref="StoreTypeFinding"/> is.</summary>
public enum StoreTypeSeverity
{
    /// <summary>The catalog's store type is used anyway (an override), or ignored as unreadable.</summary>
    Warning,

    /// <summary>The column, or the whole source, binds as if store_type were empty until the catalog or the database is fixed.</summary>
    Error
}

/// <summary>One store-type problem, worded for the person who maintains the catalog.</summary>
/// <param name="Severity">Warning or Error.</param>
/// <param name="SourceName">The source's catalog name.</param>
/// <param name="ColumnName">The column, or null for a problem with the whole source (a failed lookup).</param>
/// <param name="Message">One or two sentences that name the source, the column and both types where they apply, and the fix.</param>
public sealed record StoreTypeFinding(StoreTypeSeverity Severity, string SourceName, string? ColumnName, string Message);
