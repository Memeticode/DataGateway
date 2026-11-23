using System.Data;
using System.Data.Common;
using DataGateway.Core.Queries;
using Microsoft.Data.SqlClient;
using SqlKata.Compilers;

namespace DataGateway.Data.SqlServer;

/// <summary>SQL Server differences for shared source execution.</summary>
/// <remarks>Register it under <see cref="ProviderName"/>; a catalog detail whose provider is <c>sqlserver</c> runs
/// here. Connection checks and failure recognition are in <c>SqlServerEngine.Source.cs</c>.</remarks>
public sealed partial class SqlServerEngine : DatabaseEngine<SqlConnection>, IDatabaseEngine
{
    /// <summary>The registration name catalog details use to select this engine.</summary>
    public const string ProviderName = "sqlserver";
    /// <inheritdoc/>
    string IDatabaseEngine.ProviderName => ProviderName;

    /// <summary>Creates the engine; each call opens its own <see cref="SqlConnection"/>.</summary>
    public SqlServerEngine() : base(text => new SqlConnection(text)) { }

    /// <summary>A SQL Server compiler with legacy pagination, which writes the row limit as <c>TOP (n)</c>.</summary>
    /// <remarks>The gateway only ever limits rows and never skips any, so <c>TOP (n)</c> is the simplest correct
    /// form: one parameter and no <c>ORDER BY</c>. <c>OFFSET</c>/<c>FETCH</c> would add a dummy
    /// <c>ORDER BY (SELECT 0)</c> and an offset parameter for no benefit.</remarks>
    /// <returns>A new compiler.</returns>
    protected override Compiler CreateCompiler() => new SqlServerCompiler { UseLegacyPagination = true };

    /// <summary><c>COUNT_BIG</c>, so the count arrives as a 64-bit integer, as the matched-row check
    /// requires.</summary>
    protected override string MatchedRowCountExpression => "COUNT_BIG(*) AS [__dg_matched_rows]";

    // Replace [ first because escaping _ adds brackets. An empty escape keeps LIKE without ESCAPE.
    /// <summary>Escapes <c>[</c> and <c>_</c> with brackets, so only <c>%</c> stays a wildcard.</summary>
    /// <param name="pattern">The caller's pattern.</param>
    /// <returns>The escaped pattern, with no escape character.</returns>
    protected override (string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern) =>
        (pattern.Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal), "");

    /// <summary>Binds a <see cref="DateTime"/> as <c>datetime2</c> and null as SQL null; other values as they
    /// are.</summary>
    /// <param name="parameter">The new parameter.</param>
    /// <param name="value">The bound value.</param>
    protected override void PrepareParameter(DbParameter parameter, object? value)
    {
        if (value is DateTime) parameter.DbType = DbType.DateTime2;
        parameter.Value = value ?? DBNull.Value;
    }

    /// <summary>Returns cells unchanged: the driver's types are already the answer's.</summary>
    /// <param name="value">The cell.</param>
    /// <param name="column">The output column; unused.</param>
    /// <returns><paramref name="value"/>.</returns>
    protected override object ConvertCell(object value, OutputColumn? column) => value;
}
