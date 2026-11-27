using Microsoft.Data.SqlClient;

namespace DataGateway.Data.SqlServer;

public sealed partial class SqlServerEngine
{
    internal const string Unparseable = "The source connection string cannot be parsed.";

    public override void ValidateSourceConnection(string? connection) => CheckSourceConnection(connection);

    // Static so the metadata read (SqlServerColumnTypes) checks a source string the same way without an engine instance.
    internal static void CheckSourceConnection(string? connection)
    {
        SqlConnection probe;
        try { probe = new SqlConnection(connection); }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            throw new ArgumentException(Unparseable);
        }
        using (probe)
        {
            if (string.IsNullOrEmpty(probe.DataSource)) throw new ArgumentException("A source server is required.");
        }
    }

    public override ProviderFailure? RecognizeFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error is SqlException sql ? new ProviderFailure(sql.Number == -2, sql.Number) : null;
    }
}
