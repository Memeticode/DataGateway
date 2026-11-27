using System.Data;
using System.Globalization;
using DataGateway.Core.Catalog;
using Microsoft.Data.SqlClient;

namespace DataGateway.Data.SqlServer;

/// <summary>One column of a SQL Server view as the database declares it (sys.columns with its system type).</summary>
/// <param name="ColumnName">The column's name.</param>
/// <param name="TypeName">The system type's name, such as <c>varchar</c>; an alias type such as <c>sysname</c> is
/// reported as its base type.</param>
/// <param name="MaxLength">sys.columns.max_length: bytes, so twice the characters for nchar and nvarchar, and -1 for max.</param>
/// <param name="Precision">sys.columns.precision.</param>
/// <param name="Scale">sys.columns.scale (for datetime2, time and datetimeoffset, the fractional-second digits).</param>
/// <param name="Collation">sys.columns.collation_name, or null for a type without one.</param>
internal sealed record SqlServerColumnType(string ColumnName, string TypeName, int MaxLength, int Precision, int Scale,
    string? Collation)
{
    /// <summary>The type as a catalog <c>store_type</c> spells it, such as <c>nvarchar(40)</c>, <c>varchar(max)</c> or
    /// <c>decimal(19,4)</c>.</summary>
    public string Spelling => TypeName.ToLowerInvariant() switch
    {
        "char" or "varchar" or "binary" or "varbinary" => TypeName + Length(MaxLength),
        "nchar" or "nvarchar" => TypeName + Length(MaxLength < 0 ? MaxLength : MaxLength / 2),
        "decimal" or "numeric" => Invariant($"{TypeName}({Precision},{Scale})"),
        "datetime2" or "time" or "datetimeoffset" => Invariant($"{TypeName}({Scale})"),
        "float" => Invariant($"{TypeName}({Precision})"),
        _ => TypeName
    };

    /// <summary>True when the column's collation stores <c>varchar</c> text as UTF-8 (its name ends in <c>_UTF8</c>).</summary>
    public bool Utf8Collation => Collation?.EndsWith("_UTF8", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The parsed <see cref="Spelling"/>, or null for a type DataGateway does not read.</summary>
    public StoreType? StoreType => Core.Catalog.StoreType.TryParse(Spelling, out var parsed) ? parsed : null;

    private static string Length(int length) => length < 0 ? "(max)" : Invariant($"({length})");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The source object is not one of the connected database's own objects: its name has a database or server
/// part, or it is a synonym for an object on a linked server, in another database, or no longer there.</summary>
/// <remarks>Unlike an outage, waiting does not fix it, so the store-type check does not try it again in this process.</remarks>
internal sealed class UnresolvedObjectException() : Exception("The source object is not an object of the connected database.");

/// <summary>Reads a SQL Server view's declared column types: the one metadata query behind the store-type check
/// (<see cref="SqlServerStoreTypes"/>) and the fill script (<see cref="SqlServerStoreTypeScript"/>).</summary>
internal static class SqlServerColumnTypes
{
    // The object name resolves as the source query does: OBJECT_ID, a bare name in the login's default schema. A name with
    // a database or server part is refused before OBJECT_ID: the catalog views below are this database's, so an id from
    // another database would select an unrelated local object's columns. A synonym is followed to its base object only
    // when that object is in this database: no server part, and no database part or one naming this database. That is
    // decided by database id (DB_ID of the part against DB_ID()), never by comparing names: names compare under this
    // database's collation, which can equate two names (the same word with and without an accent, under an
    // accent-insensitive one) that the instance keeps as two databases. A part naming no database (DB_ID null) is refused too. A refused
    // name or synonym makes the first result 'unresolved', and no column is read. An alias type (sysname, or a
    // user-defined one) is reported as its system type; a CLR type (hierarchyid, geometry, geography) shares a
    // system_type_id, so it keeps its own name.
    internal const string Sql =
        "DECLARE @id int, @status nvarchar(16) = N'object', @base nvarchar(1035);\r\n" +
        "IF PARSENAME(@object, 4) IS NOT NULL OR PARSENAME(@object, 3) IS NOT NULL\r\n" +
        "    SET @status = N'unresolved';\r\n" +
        "ELSE\r\n" +
        "BEGIN\r\n" +
        "    SET @id = OBJECT_ID(@object);\r\n" +
        "    SET @base = (SELECT [base_object_name] FROM sys.synonyms WHERE [object_id] = @id);\r\n" +
        "END;\r\n" +
        "IF @base IS NOT NULL\r\n" +
        "    IF PARSENAME(@base, 4) IS NOT NULL OR (PARSENAME(@base, 3) IS NOT NULL AND ISNULL(DB_ID(PARSENAME(@base, 3)), 0) <> DB_ID())\r\n" +
        "        SELECT @id = NULL, @status = N'unresolved';\r\n" +
        "    ELSE\r\n" +
        "        SELECT @id = OBJECT_ID(@base), @status = CASE WHEN OBJECT_ID(@base) IS NULL THEN N'unresolved' ELSE N'synonym' END;\r\n" +
        "SELECT @status AS [status];\r\n" +
        "SELECT c.[name] AS [column_name],\r\n" +
        "       CASE WHEN t.[is_assembly_type] = 0 AND t.[user_type_id] <> t.[system_type_id] THEN b.[name] ELSE t.[name] END AS [type_name],\r\n" +
        "       c.[max_length], c.[precision], c.[scale], c.[collation_name]\r\n" +
        "FROM sys.columns AS c\r\n" +
        "JOIN sys.types AS t ON t.[user_type_id] = c.[user_type_id]\r\n" +
        "LEFT JOIN sys.types AS b ON b.[user_type_id] = t.[system_type_id]\r\n" +
        "WHERE c.[object_id] = @id\r\n" +
        "ORDER BY c.[column_id];";

    /// <summary>Reads the declared types of every column of <paramref name="objectName"/> (or of the local object a
    /// synonym of that name points to) on the source <paramref name="connection"/>, in column order.</summary>
    /// <param name="connection">The source's connection string; it is used as given and never logged.</param>
    /// <param name="objectName">The source's catalog object_name: <c>schema.view</c> or <c>view</c>.</param>
    /// <param name="timeoutSeconds">The command timeout, 1 or more.</param>
    /// <param name="cancellationToken">Stops the read, connect included.</param>
    /// <returns>The columns; empty when the object does not exist or the login may not see its definition, which SQL
    /// Server does not tell apart.</returns>
    /// <exception cref="UnresolvedObjectException">The object name has a database or server part, or the object is a
    /// synonym for a remote or missing object.</exception>
    internal static async Task<IReadOnlyList<SqlServerColumnType>> ReadAsync(string connection, string objectName,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutSeconds);
        SqlServerEngine.CheckSourceConnection(connection);
        await using var session = new SqlConnection(connection);
        await session.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = session.CreateCommand();
        command.CommandText = Sql;
        command.CommandTimeout = timeoutSeconds;
        command.Parameters.Add(new SqlParameter("@object", SqlDbType.NVarChar, 776) { Value = objectName });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The metadata query returned no status.");
        if (reader.GetString(0) == "unresolved") throw new UnresolvedObjectException();
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<SqlServerColumnType>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            columns.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        return columns.AsReadOnly();
    }

    /// <summary>The view column a catalog column name means: the check and the fill tool both find columns here.</summary>
    /// <remarks>Names compare ignoring case, as the catalog's do. On a case-sensitive database a view can hold two columns
    /// that differ only in case (<c>stamp</c> and <c>STAMP</c>); then the one spelled exactly like the catalog's is meant,
    /// and with no such column the name is ambiguous and no column is chosen: taking whichever came first could lend the
    /// catalog column another column's type.</remarks>
    /// <returns>The column, or null with <c>Ambiguous</c> false when the view has none of that name, or null with
    /// <c>Ambiguous</c> true when several match ignoring case and not exactly one matches exactly.</returns>
    internal static (SqlServerColumnType? Column, bool Ambiguous) Match(IReadOnlyList<SqlServerColumnType> view, string name)
    {
        SqlServerColumnType? only = null, exact = null;
        int matches = 0, exactMatches = 0;
        foreach (var column in view)
        {
            if (!string.Equals(column.ColumnName, name, StringComparison.OrdinalIgnoreCase)) continue;
            only = column;
            matches++;
            if (!string.Equals(column.ColumnName, name, StringComparison.Ordinal)) continue;
            exact = column;
            exactMatches++;
        }
        return matches <= 1 ? (only, false) : exactMatches == 1 ? (exact, false) : (null, true);
    }

    /// <summary>Why a read failed, in words safe for a log line or an SQL comment: never the exception's own message,
    /// which can name servers or logins.</summary>
    internal static string Describe(Exception error) => error switch
    {
        UnresolvedObjectException => "the source object is named with a database or server part, or is a synonym for an " +
            "object on another server, in another database, or no longer there",
        TimeoutException or OperationCanceledException => "it took longer than the metadata read's time limit",
        _ => error.GetType().Name
    };
}
