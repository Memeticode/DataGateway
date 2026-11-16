using System.Data.Common;
using Npgsql;
using DataGateway.Core.Queries;
using SqlKata.Compilers;

namespace DataGateway.Data.PostgreSql;

/// <summary>PostgreSQL compilation and scoped source execution.</summary>
public sealed partial class PostgreSqlEngine : DatabaseEngine<NpgsqlConnection>, IDatabaseEngine
{
    public const string ProviderName = "postgresql";
    string IDatabaseEngine.ProviderName => ProviderName;

    public PostgreSqlEngine() : base(text => new NpgsqlConnection(text)) { }

    protected override Compiler CreateCompiler() => new PostgresCompiler();

    protected override string MatchedRowCountExpression => "COUNT(*) AS \"__dg_matched_rows\"";

    protected override (string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern) =>
        (pattern.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal), "\\");

    protected override void PrepareParameter(DbParameter parameter, object? value) =>
        PostgreSqlParameters.Prepare((NpgsqlParameter)parameter, value);

    protected override object ConvertCell(object value, OutputColumn? column) => value;


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
