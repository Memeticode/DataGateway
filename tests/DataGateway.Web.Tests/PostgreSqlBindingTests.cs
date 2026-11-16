using System.Reflection;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.PostgreSql;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class PostgreSqlBindingTests
{
    [Fact]
    public void Parameters_keep_unknown_date_and_native_values()
    {
        object?[] values = ["", "\u03bb", "'; DROP TABLE x;--", DBNull.Value,
            new DateTime(2025, 1, 2), new DateOnly(2024, 12, 31), 3, 4L, 1.25m, true,
            null, 1.2500m, 9007199254740993L, false,
            new DateTime(2025, 1, 2, 18, 30, 0, DateTimeKind.Utc)];
        var bindings = values.Select((value, index) => (value, index))
            .ToDictionary(item => "@p" + item.index, item => item.value!);
        var query = new CompiledQuery("SELECT @p0", bindings, 3, false, []);
        using var command = new NpgsqlCommand();
        typeof(DatabaseEngine<NpgsqlConnection>)
            .GetMethod("BindParameters", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new PostgreSqlEngine(), [command, query]);
        Assert.Equal(values.Length, command.Parameters.Count);
        for (var i = 0; i < values.Length; i++)
        {
            var parameter = Assert.IsType<NpgsqlParameter>(command.Parameters[i]);
            var value = values[i];
            Assert.Equal("@p" + i, parameter.ParameterName);
            switch (value)
            {
                case null or DBNull:
                    Assert.Equal(NpgsqlDbType.Unknown, parameter.NpgsqlDbType);
                    Assert.Same(DBNull.Value, parameter.Value);
                    break;
                case string:
                    Assert.Equal(NpgsqlDbType.Unknown, parameter.NpgsqlDbType);
                    Assert.Same(value, parameter.Value);
                    break;
                case DateTime date:
                    Assert.Equal(NpgsqlDbType.Date, parameter.NpgsqlDbType);
                    Assert.Equal(DateOnly.FromDateTime(date), Assert.IsType<DateOnly>(parameter.Value));
                    break;
                case DateOnly date:
                    Assert.Equal(NpgsqlDbType.Date, parameter.NpgsqlDbType);
                    Assert.Equal(date, Assert.IsType<DateOnly>(parameter.Value));
                    break;
                default:
                    Assert.Same(value, parameter.Value);
                    break;
            }
        }
        Assert.Equal("", values[0]);
        Assert.IsType<DateTime>(values[4]);
        Assert.Equal(4, (decimal.GetBits((decimal)values[11]!)[3] >> 16) & 0xff);
    }
}
