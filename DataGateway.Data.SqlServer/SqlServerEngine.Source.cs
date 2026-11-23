using Microsoft.Data.SqlClient;

namespace DataGateway.Data.SqlServer;

public sealed partial class SqlServerEngine
{
    /// <summary>The message for a connection string the driver cannot parse; it never echoes the string.</summary>
    internal const string Unparseable = "The source connection string cannot be parsed.";

    /// <summary>Checks that the driver can parse the connection string and that it names a server.</summary>
    /// <remarks>The driver parses the text without connecting. A parse error is replaced by
    /// <see cref="Unparseable"/>, because the driver's own message can quote part of the
    /// string.</remarks>
    /// <param name="connection">The source connection string.</param>
    /// <exception cref="ArgumentException">The string cannot be parsed or names no server.</exception>
    public override void ValidateSourceConnection(string? connection)
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

    /// <summary>Recognizes a <see cref="SqlException"/>; error number -2 is the driver's command timeout.</summary>
    /// <param name="error">The exception.</param>
    /// <returns>The facts, with the SQL Server error number; null for any other exception.</returns>
    public override ProviderFailure? RecognizeFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error is SqlException sql ? new ProviderFailure(sql.Number == -2, sql.Number) : null;
    }
}
