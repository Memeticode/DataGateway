using Npgsql;
using NpgsqlTypes;

namespace DataGateway.Data.PostgreSql;

internal static class PostgreSqlParameters
{
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
