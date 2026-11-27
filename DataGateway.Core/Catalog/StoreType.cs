using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DataGateway.Core.Catalog;

/// <summary>A catalog column's parsed <c>store_type</c>: the source database's own type, such as <c>varchar(20)</c>,
/// <c>nvarchar(max)</c>, <c>char(10)</c>, <c>decimal(19,4)</c>, <c>datetime</c> or <c>int</c>.</summary>
/// <remarks>
/// <para>Parsed by the engine that binds by it, not by <see cref="CatalogMapping.Map"/>, which keeps the catalog text as
/// written (<see cref="CatalogStoreType"/>) because only the source's own engine knows its spelling: the SQL Server
/// engine parses each distinct text once per process and checks it against the view. Only <see cref="TryParse"/>
/// creates one, and every member
/// it sets is get-only, so a <c>with</c> copy cannot produce a type the parser would refuse.</para>
/// <para>The names are SQL Server's system types, compared ignoring case: the SQL Server engine is the one that binds
/// by store type. A name outside that list, or arguments that type does not take, is not a store type at all: the
/// parser refuses it, and the catalog column then binds as if its <c>store_type</c> were empty.</para>
/// </remarks>
public sealed record StoreType
{
    private StoreType(string name, int? length, bool isMax, int? precision, int? scale)
    {
        Name = name;
        Length = length;
        IsMax = isMax;
        Precision = precision;
        Scale = scale;
    }

    /// <summary>The base type name in lower case, such as <c>varchar</c>.</summary>
    public string Name { get; }

    /// <summary>The declared length of a character or binary type, such as 20 for <c>varchar(20)</c>; null for
    /// <c>max</c> and for types without a length.</summary>
    public int? Length { get; }

    /// <summary>True for <c>varchar(max)</c>, <c>nvarchar(max)</c> and <c>varbinary(max)</c>.</summary>
    public bool IsMax { get; }

    /// <summary>The declared precision: digits for <c>decimal</c> and <c>numeric</c>, mantissa bits for <c>float</c>,
    /// fractional-second digits for <c>datetime2</c>, <c>time</c> and <c>datetimeoffset</c>; null when not written.</summary>
    public int? Precision { get; }

    /// <summary>The declared scale of <c>decimal</c> or <c>numeric</c>; null when not written.</summary>
    public int? Scale { get; }

    /// <summary>The type written back in one canonical spelling, such as <c>varchar(20)</c>, <c>nvarchar(max)</c> or
    /// <c>decimal(19,4)</c>: lower-case name, no spaces, only the arguments that were written.</summary>
    /// <remarks>Safe to put in a log line or an SQL comment: the parser accepts only ASCII letters, digits and
    /// underscores in a name, and digits or <c>max</c> as arguments.</remarks>
    public string Spelling => IsMax ? Name + "(max)"
        : Length is { } length ? string.Create(CultureInfo.InvariantCulture, $"{Name}({length})")
        : Precision is { } precision && Scale is { } scale ? string.Create(CultureInfo.InvariantCulture, $"{Name}({precision},{scale})")
        : Precision is { } digits ? string.Create(CultureInfo.InvariantCulture, $"{Name}({digits})")
        : Name;

    /// <summary>True for the Unicode character types <c>nchar</c>, <c>nvarchar</c> and <c>ntext</c>.</summary>
    public bool IsUnicode => Name is "nchar" or "nvarchar" or "ntext";

    /// <summary>True for the fixed-length types <c>char</c>, <c>nchar</c> and <c>binary</c>.</summary>
    public bool IsFixedLength => Name is "char" or "nchar" or "binary";

    /// <summary>True when the column's collation stores <c>varchar</c> text as UTF-8 (a <c>_UTF8</c> collation).</summary>
    /// <remarks>Not part of <c>store_type</c>: a collation is not a type spelling. It is false from the parser and set by
    /// the check that compares the catalog with the source database's own column types; a UTF-8 <c>varchar</c> column
    /// binds text as before, as Entity Framework does (its issue 25798).</remarks>
    public bool Utf8Collation { get; init; }

    /// <summary>Reads a catalog <c>store_type</c> cell: a type name, then optionally <c>(n)</c>, <c>(max)</c> or
    /// <c>(p,s)</c>, with spaces allowed around each part.</summary>
    /// <returns>False, with <paramref name="storeType"/> null, for null, empty or unreadable text, an unknown name, or
    /// arguments outside the type's own range (such as <c>char(max)</c> or <c>nvarchar(8000)</c>).</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out StoreType? storeType)
    {
        storeType = null;
        if (text is null) return false;
        var match = Shape.Match(text);
        if (!match.Success) return false;
        var name = match.Groups["name"].Value.ToLowerInvariant();
        if (!Rules.TryGetValue(name, out var rule)) return false;

        var first = match.Groups["first"];
        var second = match.Groups["second"];
        if (!first.Success)
        {
            if (rule.Arguments is Arguments.Length) return false;
            storeType = new(name, null, false, null, null);
            return true;
        }
        if (rule.Arguments is Arguments.None) return false;

        if (first.Value.Equals("max", StringComparison.OrdinalIgnoreCase))
        {
            if (second.Success || !rule.AllowsMax) return false;
            storeType = new(name, null, true, null, null);
            return true;
        }
        if (!int.TryParse(first.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number < rule.Minimum || number > rule.Maximum)
            return false;

        switch (rule.Arguments)
        {
            case Arguments.Length when !second.Success:
                storeType = new(name, number, false, null, null);
                return true;
            case Arguments.Precision when !second.Success:
                storeType = new(name, null, false, number, null);
                return true;
            case Arguments.PrecisionAndScale:
                if (!second.Success)
                {
                    storeType = new(name, null, false, number, null);
                    return true;
                }
                if (!int.TryParse(second.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var scale) || scale > number)
                    return false;
                storeType = new(name, null, false, number, scale);
                return true;
            default:
                return false;
        }
    }

    private enum Arguments { None, Length, Precision, PrecisionAndScale }

    private sealed record Rule(Arguments Arguments, int Minimum = 0, int Maximum = 0, bool AllowsMax = false);

    // ASCII digits only ([0-9], not \d, which also matches other scripts' digits); at most 9 so int.TryParse cannot overflow.
    // \A and \z, not ^ and $: $ also matches before a final line feed.
    private static readonly Regex Shape = new(
        @"\A[ \t]*(?<name>[A-Za-z_][A-Za-z0-9_]*)[ \t]*(\([ \t]*(?<first>[0-9]{1,9}|[Mm][Aa][Xx])[ \t]*(,[ \t]*(?<second>[0-9]{1,9})[ \t]*)?\))?[ \t]*\z",
        RegexOptions.CultureInvariant);

    // SQL Server's system types and the arguments each takes. A character or binary type needs its length written: a bare
    // varchar means varchar(1) to SQL Server, which in a catalog is far more likely a slip than the intended type.
    private static readonly Dictionary<string, Rule> Rules = new(StringComparer.Ordinal)
    {
        ["char"] = new(Arguments.Length, 1, 8000),
        ["varchar"] = new(Arguments.Length, 1, 8000, AllowsMax: true),
        ["nchar"] = new(Arguments.Length, 1, 4000),
        ["nvarchar"] = new(Arguments.Length, 1, 4000, AllowsMax: true),
        ["binary"] = new(Arguments.Length, 1, 8000),
        ["varbinary"] = new(Arguments.Length, 1, 8000, AllowsMax: true),
        ["decimal"] = new(Arguments.PrecisionAndScale, 1, 38),
        ["numeric"] = new(Arguments.PrecisionAndScale, 1, 38),
        ["float"] = new(Arguments.Precision, 1, 53),
        ["datetime2"] = new(Arguments.Precision, 0, 7),
        ["datetimeoffset"] = new(Arguments.Precision, 0, 7),
        ["time"] = new(Arguments.Precision, 0, 7),
        ["bigint"] = new(Arguments.None),
        ["int"] = new(Arguments.None),
        ["smallint"] = new(Arguments.None),
        ["tinyint"] = new(Arguments.None),
        ["bit"] = new(Arguments.None),
        ["money"] = new(Arguments.None),
        ["smallmoney"] = new(Arguments.None),
        ["real"] = new(Arguments.None),
        ["date"] = new(Arguments.None),
        ["datetime"] = new(Arguments.None),
        ["smalldatetime"] = new(Arguments.None),
        ["uniqueidentifier"] = new(Arguments.None),
        ["text"] = new(Arguments.None),
        ["ntext"] = new(Arguments.None),
        ["image"] = new(Arguments.None),
        ["xml"] = new(Arguments.None),
        ["sql_variant"] = new(Arguments.None),
        ["hierarchyid"] = new(Arguments.None),
        ["geography"] = new(Arguments.None),
        ["geometry"] = new(Arguments.None),
        ["rowversion"] = new(Arguments.None),
        ["timestamp"] = new(Arguments.None),
    };
}
