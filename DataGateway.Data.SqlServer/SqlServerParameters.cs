using System.Data;
using System.Text;
using DataGateway.Core.Catalog;
using Microsoft.Data.SqlClient;

namespace DataGateway.Data.SqlServer;

/// <summary>Binds a filter value as its column's catalog store type, by Entity Framework Core's rule with three
/// exceptions, or declines so the engine binds it as preview.6 did.</summary>
/// <remarks>
/// <para>Why: a parameter of another type than the column makes SQL Server convert one side. An <c>nvarchar</c> value
/// against a <c>varchar</c> column converts the column and, under SQL_ collations, scans it; a value bound as the
/// column's own type keeps the index. The size follows EF: the column's length when the value fits, else the type's
/// largest fixed size (8000 bytes, 4000 Unicode characters), else max, so a long value is never cut and few sizes
/// mean few plans. One departure: non-Unicode text over 4000 characters always goes as <c>varchar(max)</c>, never
/// <c>varchar(8000)</c>. Should the column really be <c>nvarchar</c> (a <c>store_type_override</c>, or a view changed
/// since the check), SQL Server would convert a <c>varchar(8000)</c> value to <c>nvarchar(4000)</c> and drop the rest,
/// so a longer filter value could match a shorter stored one.</para>
/// <para>The exceptions to EF: non-ASCII text against <c>varchar</c> or <c>char</c> keeps <c>nvarchar</c> (EF sends
/// <c>varchar</c>, and characters outside the column's code page arrive as '?'); a decimal keeps its own precision and
/// scale (EF trims it to the column's, which rounds the value); and a <c>char(n)</c> or <c>nchar(n)</c> column takes its
/// fixed-length type only for a value of exactly n characters (for <c>char</c>, of at most 4000, as above), since SQL
/// Server pads a shorter fixed-length value with blanks, which a LIKE pattern would then have to match.</para>
/// <para>Declining (returning false) is the answer for everything else: a value of another kind than the column, a
/// number or date outside the column type's range, a date the column's type cannot hold exactly (a time part on
/// <c>date</c>, seconds on <c>smalldatetime</c>, anything finer than a hundredth of a second on <c>datetime</c>), a type
/// the rule does not name, or a UTF-8 <c>varchar</c>.</para>
/// </remarks>
internal static class SqlServerParameters
{
    private const int AnsiFixedLimit = 8000;
    private const int UnicodeFixedLimit = 4000;
    private static readonly DateTime DateTimeMinimum = new(1753, 1, 1);
    private static readonly DateTime SmallDateTimeMinimum = new(1900, 1, 1);
    private static readonly DateTime SmallDateTimeMaximum = new(2079, 6, 6, 23, 59, 0);

    // datetime keeps time in 1/300 s steps; a .NET tick is 100 ns, so the instants both can hold exactly are the whole
    // hundredths of a second (3/300 s), 100,000 ticks apart. Days are whole multiples of that, so whole ticks will do.
    private const long DateTimeTickStep = TimeSpan.TicksPerSecond / 100;

    internal static bool TryPrepare(SqlParameter parameter, object? value, StoreType storeType)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(storeType);
        return value switch
        {
            string text => Text(parameter, text, storeType),
            decimal number => Integer(parameter, number, storeType),
            DateTime date => Date(parameter, date, storeType),
            bool flag when storeType.Name == "bit" => Set(parameter, SqlDbType.Bit, flag),
            _ => false
        };
    }

    private static bool Text(SqlParameter parameter, string text, StoreType storeType)
    {
        switch (storeType.Name)
        {
            case "varchar" or "char" when !storeType.Utf8Collation && Ascii.IsValid(text):
                return text.Length > UnicodeFixedLimit
                    ? Set(parameter, SqlDbType.VarChar, text, size: -1)
                    : Sized(parameter, text, storeType, SqlDbType.Char, SqlDbType.VarChar, AnsiFixedLimit);
            case "nvarchar" or "nchar":
                return Sized(parameter, text, storeType, SqlDbType.NChar, SqlDbType.NVarChar, UnicodeFixedLimit);
            default:
                return false;
        }
    }

    private static bool Sized(SqlParameter parameter, string text, StoreType storeType, SqlDbType fixedType,
        SqlDbType variableType, int fixedLimit)
    {
        var exact = storeType.IsFixedLength && text.Length == storeType.Length;
        parameter.SqlDbType = exact ? fixedType : variableType;
        parameter.Size = storeType.Length is { } length && text.Length <= length ? length
            : text.Length <= fixedLimit ? fixedLimit
            : -1;
        parameter.Value = text;
        return true;
    }

    // Only a whole number within the column type's range binds as that integer type; anything else stays a decimal.
    private static bool Integer(SqlParameter parameter, decimal number, StoreType storeType)
    {
        if (number != decimal.Truncate(number)) return false;
        return storeType.Name switch
        {
            "int" when number is >= int.MinValue and <= int.MaxValue => Set(parameter, SqlDbType.Int, (int)number),
            "bigint" when number is >= long.MinValue and <= long.MaxValue => Set(parameter, SqlDbType.BigInt, (long)number),
            "smallint" when number is >= short.MinValue and <= short.MaxValue => Set(parameter, SqlDbType.SmallInt, (short)number),
            "tinyint" when number is >= byte.MinValue and <= byte.MaxValue => Set(parameter, SqlDbType.TinyInt, (byte)number),
            _ => false
        };
    }

    // A date binds as a narrower type only when that type holds it exactly; otherwise it stays datetime2, as without a
    // store type. Converting would round or drop part of the value, and a rounded value can equal a stored one it does not.
    private static bool Date(SqlParameter parameter, DateTime date, StoreType storeType) => storeType.Name switch
    {
        "datetime" when date >= DateTimeMinimum && date.Ticks % DateTimeTickStep == 0 => Set(parameter, SqlDbType.DateTime, date),
        "smalldatetime" when date >= SmallDateTimeMinimum && date <= SmallDateTimeMaximum && date.Ticks % TimeSpan.TicksPerMinute == 0 =>
            Set(parameter, SqlDbType.SmallDateTime, date),
        "date" when date.TimeOfDay == TimeSpan.Zero => Set(parameter, SqlDbType.Date, date),
        "datetime2" => Set(parameter, SqlDbType.DateTime2, date),
        _ => false
    };

    private static bool Set(SqlParameter parameter, SqlDbType type, object value)
    {
        parameter.SqlDbType = type;
        parameter.Value = value;
        return true;
    }

    private static bool Set(SqlParameter parameter, SqlDbType type, string text, int size)
    {
        parameter.Size = size;
        return Set(parameter, type, text);
    }
}
