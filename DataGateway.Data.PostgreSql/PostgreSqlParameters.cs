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
            case decimal number when number == decimal.Truncate(number) && number is >= int.MinValue and <= int.MaxValue:
                // A numeric parameter makes PostgreSQL cast an integer column and scan it; an integer parameter keeps
                // the index and converts itself up against a numeric or float column.
                parameter.NpgsqlDbType = NpgsqlDbType.Integer;
                parameter.Value = (int)number;
                break;
            case decimal number when number == decimal.Truncate(number) && number is >= long.MinValue and <= long.MaxValue:
                parameter.NpgsqlDbType = NpgsqlDbType.Bigint;
                parameter.Value = (long)number;
                break;
            default:
                parameter.Value = value;
                break;
        }
    }
}
