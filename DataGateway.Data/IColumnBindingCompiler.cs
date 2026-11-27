using SqlKata;

namespace DataGateway.Data;

/// <summary>A SqlKata compiler that knows which column each filter value it bound is compared with.</summary>
/// <remarks>
/// <para><see cref="DatabaseEngine{TConnection}"/> asks for it after compiling, when its compiler implements this, to
/// carry each value's column store type (<see cref="DataGateway.Core.Queries.CompiledQuery.StoreTypes"/>) to the
/// typed parameter hook. Only the compiler sees which condition added which binding, so the mapping comes from it; an
/// engine whose compiler does not implement this binds every value without a store type, as before.</para>
/// </remarks>
public interface IColumnBindingCompiler
{
    /// <summary>For the result of this compiler's last compile, each named binding compared with a column, mapped to
    /// that column's name; bindings that belong to no column (row limits, for one) are absent.</summary>
    /// <param name="result">The <see cref="SqlResult"/> this compiler's last <c>Compile</c> call returned.</param>
    IReadOnlyDictionary<string, string> BindingColumns(SqlResult result);
}
