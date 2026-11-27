using System.Data;
using System.Data.Common;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using Microsoft.Data.SqlClient;
using SqlKata.Compilers;

namespace DataGateway.Data.SqlServer;

/// <summary>SQL Server differences for shared source execution.</summary>
public sealed partial class SqlServerEngine : DatabaseEngine<SqlConnection>, IDatabaseEngine, IStoreTypeEngine
{
    public const string ProviderName = "sqlserver";
    string IDatabaseEngine.ProviderName => ProviderName;

    public SqlServerEngine() : base(text => new SqlConnection(text)) { }

    // One check per engine instance: the application registers one engine, so this is one per process.
    private readonly SqlServerStoreTypes storeTypes = SqlServerStoreTypes.ForSources(TimeProvider.System);

    Task<IReadOnlyDictionary<string, StoreType>> IStoreTypeEngine.ResolveStoreTypesAsync(SourceContract source,
        Action<StoreTypeFinding> report, CancellationToken cancellationToken) =>
        storeTypes.ResolveAsync(source, report, cancellationToken);

    protected override Compiler CreateCompiler() => new SqlServerColumnCompiler();

    protected override string MatchedRowCountExpression => "COUNT_BIG(*) AS [__dg_matched_rows]";

    // Replace [ first because escaping _ adds brackets. An empty escape keeps LIKE without ESCAPE.
    protected override (string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern) =>
        (pattern.Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal), "");

    // Without a store type a value binds as preview.6 did: text as the driver's nvarchar, a date as datetime2 (the
    // driver's default, datetime, cannot hold every date a caller can send), everything else by the driver's inference.
    protected override void PrepareParameter(DbParameter parameter, object? value)
    {
        if (value is DateTime) parameter.DbType = DbType.DateTime2;
        parameter.Value = value ?? DBNull.Value;
    }

    // With one, the value binds as its column's type (SqlServerParameters); a value that rule does not cover binds as
    // above.
    protected override void PrepareParameter(DbParameter parameter, object? value, StoreType storeType)
    {
        if (!SqlServerParameters.TryPrepare((SqlParameter)parameter, value, storeType)) PrepareParameter(parameter, value);
    }

    protected override object ConvertCell(object value, OutputColumn? column) => value;
}
