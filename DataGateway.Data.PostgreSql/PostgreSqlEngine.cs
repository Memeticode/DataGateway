using System.Data.Common;
using Npgsql;
using DataGateway.Core.Queries;
using SqlKata.Compilers;

namespace DataGateway.Data.PostgreSql;

/// <summary>PostgreSQL compilation and scoped source execution.</summary>
/// <remarks>Register it under <see cref="ProviderName"/>; a catalog detail whose provider is <c>postgresql</c> runs
/// here. Text parameters are sent untyped, so each column's own type decides how it compares
/// (<see cref="PostgreSqlParameters"/>).</remarks>
public sealed partial class PostgreSqlEngine : DatabaseEngine<NpgsqlConnection>, IDatabaseEngine
{
    /// <summary>The registration name catalog details use to select this engine.</summary>
    public const string ProviderName = "postgresql";
    /// <inheritdoc/>
    string IDatabaseEngine.ProviderName => ProviderName;

    /// <summary>Creates the engine; each call opens its own <see cref="NpgsqlConnection"/>.</summary>
    public PostgreSqlEngine() : base(text => new NpgsqlConnection(text)) { }

    /// <summary>A PostgreSQL compiler.</summary>
    /// <returns>A new compiler.</returns>
    protected override Compiler CreateCompiler() => new PostgresCompiler();

    /// <summary><c>COUNT(*)</c>, which PostgreSQL returns as <c>bigint</c>, a 64-bit integer.</summary>
    protected override string MatchedRowCountExpression => "COUNT(*) AS \"__dg_matched_rows\"";

    /// <summary>Escapes <c>\</c> and <c>_</c> with a backslash, so only <c>%</c> stays a wildcard.</summary>
    /// <param name="pattern">The caller's pattern.</param>
    /// <returns>The escaped pattern, with <c>\</c> as the escape character.</returns>
    protected override (string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern) =>
        (pattern.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal), "\\");

    /// <summary>Binds through <see cref="PostgreSqlParameters.Prepare"/>.</summary>
    /// <param name="parameter">The new parameter, an <see cref="NpgsqlParameter"/>.</param>
    /// <param name="value">The bound value.</param>
    protected override void PrepareParameter(DbParameter parameter, object? value) =>
        PostgreSqlParameters.Prepare((NpgsqlParameter)parameter, value);

    /// <summary>Returns cells unchanged: the driver's types are already the answer's.</summary>
    /// <param name="value">The cell.</param>
    /// <param name="column">The output column; unused.</param>
    /// <returns><paramref name="value"/>.</returns>
    protected override object ConvertCell(object value, OutputColumn? column) => value;


    /// <summary>Checks that the connection string parses and names a host, database and user explicitly.</summary>
    /// <remarks>Each must be in the string itself, so the driver never fills one in from the machine's environment
    /// or the account the gateway runs as. Messages never echo the string.</remarks>
    /// <param name="connection">The source connection string.</param>
    /// <exception cref="ArgumentException">The string is blank, cannot be parsed, or lacks a host, database or
    /// user.</exception>
    public override void ValidateSourceConnection(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection)) throw new ArgumentException("A source host is required.");
        NpgsqlConnectionStringBuilder value;
        try { value = new NpgsqlConnectionStringBuilder(connection); }
        catch (Exception error) when (error is ArgumentException or FormatException)
        { throw new ArgumentException("The source connection string cannot be parsed."); }
        if (string.IsNullOrWhiteSpace(value.Host)) throw new ArgumentException("A source host is required.");
        if (string.IsNullOrWhiteSpace(value.Database)) throw new ArgumentException("A source database is required.");
        if (string.IsNullOrWhiteSpace(value.Username)) throw new ArgumentException("A source user is required.");
    }

    /// <summary>Recognizes Npgsql's exceptions.</summary>
    /// <remarks>A server error is a timeout when its SQLSTATE is <c>57014</c> (query cancelled, which a statement
    /// timeout also raises); any other Npgsql error is a timeout when a <see cref="TimeoutException"/> lies
    /// inside it. PostgreSQL identifies errors by SQLSTATE text, not a number, so no number is reported.</remarks>
    /// <param name="error">The exception.</param>
    /// <returns>The facts; null for an exception that is not Npgsql's.</returns>
    public override ProviderFailure? RecognizeFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is PostgresException postgres) return new ProviderFailure(postgres.SqlState == "57014", null);
        if (error is not NpgsqlException) return null;
        for (var inner = error.InnerException; inner is not null; inner = inner.InnerException)
            if (inner is TimeoutException) return new ProviderFailure(true, null);
        return new ProviderFailure(false, null);
    }
}
