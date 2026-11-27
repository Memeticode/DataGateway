using System.Globalization;
using System.Text;
using DataGateway.Core.Catalog;

namespace DataGateway.Data.SqlServer;

/// <summary>The engine of the catalog database a <see cref="SqlServerStoreTypeScript"/> is written for.</summary>
public enum CatalogScriptDialect
{
    /// <summary>A SQL Server catalog (db/catalog-ddl.sql): <c>[dbo].[DataSourceColumn]</c>.</summary>
    SqlServer,

    /// <summary>A PostgreSQL catalog (db/postgresql/catalog-ddl.sql): <c>datagateway.datasourcecolumn</c>.</summary>
    PostgreSql
}

/// <summary>The store-type fill tool: reads each SQL Server source's view column types and writes the UPDATE statements
/// that would set the catalog's <c>store_type</c> to them. It never runs them.</summary>
/// <remarks>
/// <para>Like Entity Framework's scaffolding, the output is for a person to review and run on the catalog, so nothing
/// here writes. A statement is written for each catalog column whose <c>store_type</c> is empty or differs from the
/// view's; a column with <c>store_type_override</c> set is never updated, only listed in a comment, as are columns the
/// view lacks, columns that match two view columns differing only in letter case and neither exactly, <c>varchar</c>
/// and <c>char</c> columns under a SQL_ collation (where a store type changes comparison rules), columns of a type
/// DataGateway does not bind by, and sources whose view could not be read. So a script without statements has nothing
/// to apply; its comments say what it could not vouch for.</para>
/// <para>Every value is a quoted literal, and comment text is limited to printable ASCII, so catalog text cannot end a
/// comment or a literal early. No column name outside the gateway's identifier grammar
/// (<see cref="CatalogMapping.IsIdentifier"/>) is ever written into a statement: a catalog column with such a name gets a
/// comment that names it only by ordinal (the gateway would refuse that source anyway, and its raw name is the one text
/// here no type parser has vetted), and a view's column names are only matched, never written.</para>
/// <para>A PostgreSQL script starts with <c>SET LOCAL standard_conforming_strings = on</c>, so its literals read the
/// same way whatever the server's setting: with the setting off, a backslash would escape the quote after it.</para>
/// </remarks>
public static class SqlServerStoreTypeScript
{
    /// <summary>Reads each source's view with <see cref="SqlServerColumnTypes.ReadAsync"/> and returns the script.</summary>
    /// <param name="sources">The catalog rows of SQL Server sources only (as a catalog reader returns them); a source of
    /// another engine would be read with the wrong metadata query.</param>
    /// <param name="catalog">The engine of the catalog database the script will run on.</param>
    /// <param name="timeoutSeconds">Each metadata query's command timeout, 1 or more.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    public static Task<string> CreateAsync(IEnumerable<CatalogRows> sources, CatalogScriptDialect catalog,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutSeconds);
        return CreateAsync(sources, catalog, (source, token) =>
            SqlServerColumnTypes.ReadAsync(source.ConnectionString, source.ObjectName, timeoutSeconds, token), cancellationToken);
    }

    internal static async Task<string> CreateAsync(IEnumerable<CatalogRows> sources, CatalogScriptDialect catalog,
        Func<DataSourceRow, CancellationToken, Task<IReadOnlyList<SqlServerColumnType>>> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(read);
        if (!Enum.IsDefined(catalog)) throw new ArgumentOutOfRangeException(nameof(catalog));
        var script = new StringBuilder();
        script.Append("-- DataGateway store_type fill script for a ")
            .Append(catalog == CatalogScriptDialect.SqlServer ? "SQL Server" : "PostgreSQL")
            .Append(" catalog. Review each statement, then run it on the catalog; this tool changes nothing itself.\n");
        if (catalog == CatalogScriptDialect.PostgreSql)
            script.Append("-- Run it in one transaction (BEGIN; ... COMMIT;): the SET LOCAL below makes every literal read the same way whatever the server's standard_conforming_strings, and ends with that transaction.\n")
                .Append("SET LOCAL standard_conforming_strings = on;\n");
        foreach (var rows in sources)
        {
            ArgumentNullException.ThrowIfNull(rows);
            var source = rows.Source;
            script.Append('\n').Append(Comment($"Source '{source.Name}' (id {source.Id}), view {source.ObjectName}:")).Append('\n');
            IReadOnlyList<SqlServerColumnType> view;
            try { view = await read(source, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                script.Append(Comment($"  The view's column types could not be read ({SqlServerColumnTypes.Describe(error)}); nothing is written for this source.")).Append('\n');
                continue;
            }
            if (view.Count == 0)
            {
                script.Append(Comment("  The view was not found, or its definition is not visible to the login; nothing is written for this source.")).Append('\n');
                continue;
            }
            foreach (var column in rows.Columns.OrderBy(column => column.Ordinal).ThenBy(column => column.ColumnName, StringComparer.Ordinal))
                Line(script, catalog, source, column, view);
        }
        return script.ToString();
    }

    private static void Line(StringBuilder script, CatalogScriptDialect catalog, DataSourceRow source,
        DataSourceColumnRow column, IReadOnlyList<SqlServerColumnType> view)
    {
        if (!CatalogMapping.IsIdentifier(column.ColumnName))
        {
            script.Append(Comment(Invariant($"  The column at ordinal {column.Ordinal} has a name that is not a bare ASCII SQL identifier; left unchanged. Correct its name in the catalog.")))
                .Append('\n');
            return;
        }
        var current = column.StoreType is null ? null : StoreType.TryParse(column.StoreType, out var parsed) ? parsed : null;
        var shown = column.StoreType is null ? "empty" : current?.Spelling ?? "unreadable";
        var (actual, ambiguous) = SqlServerColumnTypes.Match(view, column.ColumnName);
        if (ambiguous)
        {
            script.Append(Comment($"  Column '{column.ColumnName}' (store_type {shown}) matches more than one view column when letter case is ignored, and none exactly; left unchanged. Spell its name in the catalog exactly as the view does.")).Append('\n');
            return;
        }
        if (actual is null)
        {
            script.Append(Comment($"  Column '{column.ColumnName}' (store_type {shown}) is not in the view; left unchanged.")).Append('\n');
            return;
        }
        if (actual.StoreType is not { } database)
        {
            script.Append(Comment($"  Column '{column.ColumnName}': the view's type {actual.Spelling} is not one DataGateway binds by; left unchanged.")).Append('\n');
            return;
        }
        if (current is not null && SqlServerStoreTypes.Same(current, database)) return;
        if (column.StoreTypeOverride)
        {
            script.Append(Comment($"  Column '{column.ColumnName}' keeps store_type {shown} (store_type_override is set); the view's type is {database.Spelling}.")).Append('\n');
            return;
        }
        if (SqlCollated(database, actual))
        {
            script.Append(Comment($"  Column '{column.ColumnName}' (store_type {shown}) is {database.Spelling} under the SQL collation {actual.Collation}, where a store_type changes how filter values compare (STORE-TYPES.md, \"The visible differences\"); left unchanged. Its owner may set store_type to {database.Spelling} by hand.")).Append('\n');
            return;
        }
        script.Append(catalog == CatalogScriptDialect.SqlServer
            ? Invariant($"UPDATE [dbo].[DataSourceColumn] SET [store_type] = N{Literal(database.Spelling)} WHERE [datasource_fk] = {source.Id} AND [column_name] = {Literal(column.ColumnName)};")
            : Invariant($"UPDATE datagateway.datasourcecolumn SET store_type = {Literal(database.Spelling)} WHERE datasource_fk = {source.Id} AND column_name = {Literal(column.ColumnName)};"))
            .Append('\n');
    }

    // A varchar or char column under a SQL_ collation compares a non-Unicode value by other rules than a Unicode one, so
    // filling its store_type would change which rows some filters return; that is the owner's call, never the tool's.
    private static bool SqlCollated(StoreType database, SqlServerColumnType actual) =>
        database.Name is "varchar" or "char" && actual.Collation?.StartsWith("SQL_", StringComparison.OrdinalIgnoreCase) == true;

    // A standard SQL string literal: quotes doubled. Both catalogs read it the same way (PostgreSQL with
    // standard_conforming_strings on, which the script sets, where a backslash is an ordinary character).
    internal static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    // A line comment holds only printable ASCII, so no catalog text can end it and start a statement.
    internal static string Comment(string text)
    {
        var safe = new StringBuilder("-- ");
        foreach (var character in text) safe.Append(character is >= ' ' and <= '~' ? character : '?');
        return safe.ToString();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
