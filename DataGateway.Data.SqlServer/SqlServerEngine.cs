using System.Data;
using System.Data.Common;
using DataGateway.Core.Queries;
using Microsoft.Data.SqlClient;
using SqlKata.Compilers;

namespace DataGateway.Data.SqlServer;

/// <summary>SQL Server differences for shared source execution.</summary>
public sealed partial class SqlServerEngine : DatabaseEngine<SqlConnection>, IDatabaseEngine
{
    public const string ProviderName = "sqlserver";
    string IDatabaseEngine.ProviderName => ProviderName;

    public SqlServerEngine() : base(text => new SqlConnection(text)) { }

    protected override Compiler CreateCompiler() => new SqlServerCompiler { UseLegacyPagination = true };

    protected override string MatchedRowCountExpression => "COUNT_BIG(*) AS [__dg_matched_rows]";

    // Replace [ first because escaping _ adds brackets. An empty escape keeps LIKE without ESCAPE.
    protected override (string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern) =>
        (pattern.Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal), "");

    protected override void PrepareParameter(DbParameter parameter, object? value)
    {
        if (value is DateTime) parameter.DbType = DbType.DateTime2;
        parameter.Value = value ?? DBNull.Value;
    }

    protected override object ConvertCell(object value, OutputColumn? column) => value;
}
