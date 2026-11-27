using System.Collections.ObjectModel;

namespace DataGateway.Core.Queries;

/// <summary>Adapter-produced SQL, named bindings, row facts and public output metadata. Server-side only.</summary>
public sealed class CompiledQuery
{
    public const string MatchedRowCountColumn = "__dg_matched_rows";

    public CompiledQuery(string sql, IReadOnlyDictionary<string, object> parameters, int keep,
        bool hasMatchedRowCount, IReadOnlyList<OutputColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keep);
        Sql = sql;
        Parameters = new ReadOnlyDictionary<string, object>(
            parameters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        Keep = keep;
        HasMatchedRowCount = hasMatchedRowCount;
        Columns = Array.AsReadOnly(columns.ToArray());
    }

    public int Keep { get; }
    public string Sql { get; }
    public IReadOnlyDictionary<string, object> Parameters { get; }

    /// <summary>For each parameter compared with a catalog column, that column's name; a parameter compared with no
    /// column (a row limit, for one) is absent.</summary>
    /// <remarks>Set by an engine whose compiler records it (DataGateway.Data.IColumnBindingCompiler), so
    /// <see cref="WithStoreTypes"/> can attach each column's store type once the engine has checked them. An init
    /// property, so every caller of the five-argument constructor keeps compiling and gets an empty map. Each key must
    /// name a parameter: anything else is a bug in the engine that compiled it, so it throws.</remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, string> ParameterColumns
    {
        get => parameterColumns;
        init => parameterColumns = Snapshot(value, nameof(ParameterColumns));
    }

    /// <summary>For each parameter whose column has a checked store type, that store type; a parameter without one is
    /// absent, and binds as the engine binds any value.</summary>
    /// <remarks>Init-only like <see cref="ParameterColumns"/>, with the same key rule.</remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, Catalog.StoreType> StoreTypes
    {
        get => storeTypes;
        init => storeTypes = Snapshot(value, nameof(StoreTypes));
    }

    /// <summary>This query with each parameter's store type taken from its column's entry in
    /// <paramref name="columnStoreTypes"/> (keyed by column name, ignoring case); parameters of other columns get none.</summary>
    /// <remarks>Replaces any store types already attached, so the map given is the whole answer.</remarks>
    public CompiledQuery WithStoreTypes(IReadOnlyDictionary<string, Catalog.StoreType> columnStoreTypes)
    {
        ArgumentNullException.ThrowIfNull(columnStoreTypes);
        var byColumn = new Dictionary<string, Catalog.StoreType>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, storeType) in columnStoreTypes) byColumn[column] = storeType;
        var bound = new Dictionary<string, Catalog.StoreType>(StringComparer.Ordinal);
        foreach (var (name, column) in ParameterColumns)
            if (byColumn.TryGetValue(column, out var storeType)) bound.Add(name, storeType);
        return new CompiledQuery(Sql, Parameters, Keep, HasMatchedRowCount, Columns)
        {
            ParameterColumns = ParameterColumns,
            StoreTypes = bound
        };
    }

    private ReadOnlyDictionary<string, T> Snapshot<T>(IReadOnlyDictionary<string, T> value, string property)
    {
        ArgumentNullException.ThrowIfNull(value, property);
        foreach (var name in value.Keys)
            if (!Parameters.ContainsKey(name))
                throw new ArgumentException($"{property} names '{name}', which is not a parameter of this query.", property);
        return new ReadOnlyDictionary<string, T>(value.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    private readonly IReadOnlyDictionary<string, string> parameterColumns =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly IReadOnlyDictionary<string, Catalog.StoreType> storeTypes =
        new ReadOnlyDictionary<string, Catalog.StoreType>(new Dictionary<string, Catalog.StoreType>(StringComparer.Ordinal));

    public bool HasMatchedRowCount { get; }
    public IReadOnlyList<OutputColumn> Columns { get; }
}
