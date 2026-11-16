using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using DataGateway.Core.Queries;
using SqlKata.Compilers;

namespace DataGateway.Data.Sqlite;

/// <summary>Tests-only replacement proof using public engine contracts.</summary>
public sealed partial class SqliteEngine : DatabaseEngine<SqliteConnection>, IDatabaseEngine
{
    public const string ProviderName = "sqlite";
    string IDatabaseEngine.ProviderName => ProviderName;

    public SqliteEngine() : this(text => new SqliteConnection(text)) { }
    public SqliteEngine(Func<string, SqliteConnection> createConnection) : base(createConnection)
    {
    }

    protected override Compiler CreateCompiler() => new SqliteCompiler();

    protected override string MatchedRowCountExpression => "COUNT(*) AS \"__dg_matched_rows\"";

    protected override (string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern) =>
        (pattern.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal), "\\");

    protected override void PrepareParameter(DbParameter parameter, object? value)
    {
        if (value is bool flag)
        {
            parameter.DbType = DbType.Int64;
            parameter.Value = flag ? 1L : 0L;
        }
        else parameter.Value = value ?? DBNull.Value;
    }

    protected override object ConvertCell(object value, OutputColumn? column) =>
        column?.DataType == "bool"
            ? value switch
            {
                0L => false, 1L => true,
                _ => throw new InvalidCastException("The source boolean cell is invalid.")
            }
            : value;

    private static bool Required(SqliteConnectionStringBuilder value) =>
        !string.IsNullOrWhiteSpace(value.DataSource) &&
        !string.Equals(value.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase) &&
        value.Mode == SqliteOpenMode.ReadOnly && !value.Pooling;


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

    public override ProviderFailure? RecognizeFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error is SqliteException sqlite ? new ProviderFailure(false, sqlite.SqliteErrorCode) : null;
    }
}
