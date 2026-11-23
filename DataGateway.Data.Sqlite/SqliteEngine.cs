using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using DataGateway.Core.Queries;
using SqlKata.Compilers;

namespace DataGateway.Data.Sqlite;

/// <summary>Tests-only replacement proof using public engine contracts.</summary>
/// <remarks>A SQLite engine built only from the public engine contracts, to show another database can plug in.
/// It also backs an example app. It is not a product engine: sources must be read-only files.</remarks>
public sealed partial class SqliteEngine : DatabaseEngine<SqliteConnection>, IDatabaseEngine
{
    /// <summary>The registration name catalog details use to select this engine.</summary>
    public const string ProviderName = "sqlite";
    /// <inheritdoc/>
    string IDatabaseEngine.ProviderName => ProviderName;

    /// <summary>Creates the engine; each call opens its own <see cref="SqliteConnection"/>.</summary>
    public SqliteEngine() : this(text => new SqliteConnection(text)) { }
    /// <summary>Creates the engine with a custom connection factory.</summary>
    /// <remarks>A seam for tests, which wrap real connections to watch reads, failures and cleanup.</remarks>
    /// <param name="createConnection">Returns a fresh, closed connection for a connection string; the engine owns
    /// it.</param>
    public SqliteEngine(Func<string, SqliteConnection> createConnection) : base(createConnection)
    {
    }

    /// <summary>A SQLite compiler.</summary>
    /// <returns>A new compiler.</returns>
    protected override Compiler CreateCompiler() => new SqliteCompiler();

    /// <summary><c>COUNT(*)</c>, which SQLite returns as a 64-bit integer.</summary>
    protected override string MatchedRowCountExpression => "COUNT(*) AS \"__dg_matched_rows\"";

    /// <summary>Escapes <c>\</c> and <c>_</c> with a backslash, so only <c>%</c> stays a wildcard.</summary>
    /// <param name="pattern">The caller's pattern.</param>
    /// <returns>The escaped pattern, with <c>\</c> as the escape character.</returns>
    protected override (string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern) =>
        (pattern.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal), "\\");

    /// <summary>Binds a <see cref="bool"/> as the integer 1 or 0, and null as SQL null; other values as they
    /// are.</summary>
    /// <remarks>SQLite has no boolean type: it stores booleans as 0 and 1.</remarks>
    /// <param name="parameter">The new parameter.</param>
    /// <param name="value">The bound value.</param>
    protected override void PrepareParameter(DbParameter parameter, object? value)
    {
        if (value is bool flag)
        {
            parameter.DbType = DbType.Int64;
            parameter.Value = flag ? 1L : 0L;
        }
        else parameter.Value = value ?? DBNull.Value;
    }

    /// <summary>Turns 0 and 1 into <see langword="false"/> and <see langword="true"/> for an output column declared
    /// <c>bool</c>; returns any other cell unchanged.</summary>
    /// <remarks>Only declared <c>bool</c> columns convert, so a count or a number that happens to be 0 or 1 stays a
    /// number.</remarks>
    /// <param name="value">The cell.</param>
    /// <param name="column">The output column; null for an undeclared column.</param>
    /// <returns>The converted value.</returns>
    /// <exception cref="InvalidCastException">A <c>bool</c> cell holds something other than 0 or 1.</exception>
    protected override object ConvertCell(object value, OutputColumn? column) =>
        column?.DataType == "bool"
            ? value switch
            {
                0L => false, 1L => true,
                _ => throw new InvalidCastException("The source boolean cell is invalid.")
            }
            : value;

    /// <summary>Whether a connection names a file (not <c>:memory:</c>), opens it read-only and turns pooling
    /// off.</summary>
    /// <param name="value">The parsed connection string.</param>
    /// <returns><see langword="true"/> when all three hold.</returns>
    private static bool Required(SqliteConnectionStringBuilder value) =>
        !string.IsNullOrWhiteSpace(value.DataSource) &&
        !string.Equals(value.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase) &&
        value.Mode == SqliteOpenMode.ReadOnly && !value.Pooling;


    /// <summary>Checks that the connection string parses and passes <see cref="Required"/>.</summary>
    /// <param name="connection">The source connection string.</param>
    /// <exception cref="ArgumentException">The string is blank, cannot be parsed, or is not a read-only, unpooled
    /// file.</exception>
    public override void ValidateSourceConnection(string? connection)
    {
        const string required = "A read-only SQLite file with pooling disabled is required.";
        if (string.IsNullOrWhiteSpace(connection)) throw new ArgumentException(required);
        SqliteConnectionStringBuilder value;
        try { value = new SqliteConnectionStringBuilder(connection); }
        catch (Exception error) when (error is ArgumentException or FormatException)
        { throw new ArgumentException("The source connection string cannot be parsed."); }
        if (!Required(value)) throw new ArgumentException(required);
    }

    /// <summary>Recognizes a <see cref="SqliteException"/>, with its SQLite error code; SQLite reports no command
    /// timeout.</summary>
    /// <param name="error">The exception.</param>
    /// <returns>The facts; null for any other exception.</returns>
    public override ProviderFailure? RecognizeFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error is SqliteException sqlite ? new ProviderFailure(false, sqlite.SqliteErrorCode) : null;
    }
}
