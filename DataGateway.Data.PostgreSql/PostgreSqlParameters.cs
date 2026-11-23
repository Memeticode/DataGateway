using Npgsql;
using NpgsqlTypes;

namespace DataGateway.Data.PostgreSql;

/// <summary>How the PostgreSQL engine binds each kind of parameter value.</summary>
/// <remarks>Text is sent untyped, so PostgreSQL infers it from the column: a <c>citext</c> column then compares
/// ignoring case and a <c>varchar</c> column keeps case, as the database defines. Sending it typed as <c>text</c> would
/// lose the case-insensitive comparison of <c>citext</c> columns.</remarks>
internal static class PostgreSqlParameters
{
    /// <summary>Sets the parameter's value and type.</summary>
    /// <remarks>Null is sent untyped so PostgreSQL infers it. A <see cref="DateTime"/> is sent as a
    /// <c>date</c>, because catalog date columns are SQL dates and filters give only a day.</remarks>
    /// <param name="parameter">The new parameter.</param>
    /// <param name="value">The bound value.</param>
    internal static void Prepare(NpgsqlParameter parameter, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                parameter.NpgsqlDbType = NpgsqlDbType.Unknown;
                parameter.Value = DBNull.Value;
                break;
            case string text:
                // Let PostgreSQL infer the column type so citext comparisons keep their semantics.
                parameter.NpgsqlDbType = NpgsqlDbType.Unknown;
                parameter.Value = text;
                break;
            case DateTime date:
                parameter.NpgsqlDbType = NpgsqlDbType.Date;
                parameter.Value = DateOnly.FromDateTime(date);
                break;
            case DateOnly date:
                parameter.NpgsqlDbType = NpgsqlDbType.Date;
                parameter.Value = date;
                break;
            default:
                parameter.Value = value;
                break;
        }
    }
}
