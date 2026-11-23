using System.Text.Json;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Catalog;
using DataGateway.Data.Queries;
using DataGateway.Data.PostgreSql;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>Pins the SQL and parameters the PostgreSQL engine compiles from a validated query.</summary>
/// <remarks>Every value reaches the server as a parameter, identifiers are quoted with the catalog's spelling, and
/// aggregates and patterns use PostgreSQL's own functions and LIKE, with no casts. The source is synthetic and nothing
/// connects to a server.</remarks>
public sealed class PostgreSqlCompilationTests
{
    /// <summary>A synthetic PostgreSQL connection with no password, shared by the PostgreSQL tests.</summary>
    internal const string Connection = "Host=localhost;Database=dg_r5_test;Username=dg_r5_reader";
    /// <summary>A synthetic source over view <c>synthetic.items</c> with one column of each data type.</summary>
    /// <param name="cap">The source's row cap.</param>
    /// <param name="queryTimeoutSeconds">The source's query timeout.</param>
    internal static SourceContract Source(int cap = 5, int queryTimeoutSeconds = 30) => CatalogMapping.Map(new CatalogRows(
        new DataSourceRow("synthetic.items", Connection) { QueryTimeoutSeconds = queryTimeoutSeconds, Id=1, Name="synthetic_items", Title="Items",
            Description="Synthetic rows.", Grain="One item.", IsActive=true, RowCap=cap },
        new[] {("id","number"),("label","text"),("MixedCase","text"),("day","date"),("flag","bool"),("amount","number")}
            .Select((c,i) => new DataSourceColumnRow(c.Item1,i+1,c.Item1,"Column.",c.Item2,null,null,null,
                true,true,true,true,c.Item2!="bool")).ToArray())).Value;
    /// <summary>Validates <paramref name="json"/> against <see cref="Source"/>, failing the test with the refusal's
    /// message if it is refused.</summary>
    internal static ValidatedQuery Validate(string json, int cap=5)
    {
        var result=QueryBuilding.Validate(Source(cap),json,QueryLimits.ForRowCap(cap));
        Assert.True(result.IsSuccess, result.Failure?.Message);
        return result.Value;
    }
    /// <summary>Validates and compiles <paramref name="json"/> with a new PostgreSQL engine.</summary>
    internal static CompiledQuery Compile(string json,int cap=5) => new PostgreSqlEngine().Compile(Validate(json,cap));

    /// <summary>A small query compiles to its exact SQL, fetching one row past the limit, and compiling never
    /// changes the validated query, even when sixteen compiles run at once.</summary>
    /// <remarks>The engine compiles a clone of the query, so a shared validated query is safe to compile from several
    /// threads, and editing a clone afterwards leaves the next compile unchanged.</remarks>
    [Fact]
    public async Task Small_golden_and_concurrent_clone_independence()
    {
        var query=Validate("""{"select":["id"],"order_by":[{"col":"id","dir":"asc"}],"limit":2}""");
        var results=await Task.WhenAll(Enumerable.Range(0,16).Select(_=>Task.Run(()=>new PostgreSqlEngine().Compile(query))));
        foreach(var result in results)
        {
            Assert.Equal("SELECT \"id\" FROM \"synthetic\".\"items\" ORDER BY \"id\" LIMIT @p0", result.Sql);
            Assert.Equal(3,Assert.IsType<int>(Assert.Single(result.Parameters).Value));
            Assert.Equal("@p0",Assert.Single(result.Parameters).Key);
            Assert.Equal(2,result.Keep); Assert.False(result.HasMatchedRowCount);
        }
        query.CloneQuery().Where("id",999).Limit(99);
        Assert.Equal(results[0].Sql,new PostgreSqlEngine().Compile(query).Sql);
    }

    /// <summary>An ungrouped aggregate compiles to the native function plus a private matched-row count; a grouped
    /// one has no count.</summary>
    /// <remarks>The matched-row count tells an aggregate over no rows from one over real rows; a group cannot exist
    /// without rows, so grouped queries need none.</remarks>
    [Theory]
    [InlineData("count","COUNT")][InlineData("sum","SUM")][InlineData("avg","AVG")]
    [InlineData("min","MIN")][InlineData("max","MAX")]
    public void Native_aggregate_and_private_int64_count(string fn,string sql)
    {
        var result=Compile(JsonSerializer.Serialize(new {aggregates=new[]{new {fn,col="amount"}}}));
        Assert.Equal($"SELECT {sql}(\"amount\") AS \"{fn}_amount\", COUNT(*) AS \"__dg_matched_rows\" FROM \"synthetic\".\"items\" LIMIT @p0",result.Sql);
        Assert.True(result.HasMatchedRowCount); Assert.Equal(6,result.Parameters["@p0"]);
        var grouped=Compile(JsonSerializer.Serialize(new {group_by=new[]{"label"},aggregates=new[]{new {fn,col="amount"}},order_by=new[]{new {col=fn+"_amount",dir="desc"}}}));
        Assert.False(grouped.HasMatchedRowCount); Assert.Contains("GROUP BY \"label\" ORDER BY",grouped.Sql);
        Assert.DoesNotContain("CAST",result.Sql);
    }

    /// <summary>Column names take the catalog's spelling, nested and/or filters keep their grouping, and every value,
    /// including an injection attempt, is a numbered parameter; only boolean constants appear inline.</summary>
    [Fact]
    public void Canonical_names_nested_predicates_and_markers()
    {
        var result=Compile("""{"select":["mixedcase"],"filters":[{"or":[{"col":"id","op":"gte","value":2},{"and":[{"col":"label","op":"in","value":["","\u03bb","x'; DROP TABLE items;--"]},{"col":"flag","op":"eq","value":true}]}]},{"col":"day","op":"between","value":["2024-12-31","2025-01-01"]}],"order_by":[{"col":"id","dir":"asc"}],"limit":2}""");
        Assert.Equal("SELECT \"MixedCase\" FROM \"synthetic\".\"items\" WHERE ((\"id\" >= @p0) OR ((\"label\" IN (@p1, @p2, @p3)) AND (\"flag\" = true))) AND \"day\" BETWEEN @p4 AND @p5 ORDER BY \"id\" LIMIT @p6",result.Sql);
        Assert.Equal(new[]{"@p0","@p1","@p2","@p3","@p4","@p5","@p6"},result.Parameters.Keys);
        Assert.Equal(2m,result.Parameters["@p0"]); Assert.Equal("",result.Parameters["@p1"]);
        Assert.Equal("\u03bb",result.Parameters["@p2"]);Assert.Equal("x'; DROP TABLE items;--",result.Parameters["@p3"]);
        Assert.Equal(new DateTime(2024,12,31),result.Parameters["@p4"]);
        Assert.Equal(new DateTime(2025,1,1),result.Parameters["@p5"]); Assert.Equal(3,result.Parameters["@p6"]);
    }

    [Theory]
    [InlineData("eq","=")][InlineData("neq","!=")][InlineData("lt","<")][InlineData("lte","<=")][InlineData("gt",">")][InlineData("gte",">=")]
    public void Numeric_predicates(string op,string sql)
    {
        var result=Compile(JsonSerializer.Serialize(new{select=new[]{"id"},filters=new[]{new{col="id",op,value=2}}}));
        Assert.Contains("\"id\" "+sql+" @p0",result.Sql);Assert.Equal(2m,result.Parameters["@p0"]);
    }

    [Theory]
    [InlineData("is_null","IS NULL")][InlineData("is_not_null","IS NOT NULL")]
    public void Null_predicates(string op,string sql) => Assert.Contains(sql,Compile(JsonSerializer.Serialize(new{filters=new[]{new{col="id",op}}})).Sql);

    [Theory]
    [InlineData("in","IN")][InlineData("not_in","NOT IN")]
    public void Membership(string op,string sql)
    {
        var result=Compile(JsonSerializer.Serialize(new{filters=new[]{new{col="label",op,value=new[]{"AbC","abc"}}}}));
        Assert.Contains(sql+" (@p0, @p1)",result.Sql);Assert.Equal("AbC",result.Parameters["@p0"]);Assert.Equal("abc",result.Parameters["@p1"]);
    }

    /// <summary>Contains and like escape PostgreSQL's LIKE metacharacters with a backslash, so only <c>%</c> is a
    /// wildcard, and the same query compiles the same pattern every time.</summary>
    /// <remarks>Plain <c>like</c>, never <c>ilike</c>: the column's own type and collation decide case, as they do
    /// for equality.</remarks>
    [Theory]
    [InlineData("contains",@"A%_[]\*'z",@"%A\%\_\[]\\*'z%")]
    [InlineData("like",@"A%_[]\*'z",@"A%\_[]\\*'z")]
    [InlineData("contains","", "%%")][InlineData("like","", "")]
    [InlineData("contains","%",@"%\%%")][InlineData("like","_",@"\_")]
    [InlineData("contains","[",@"%\[%")][InlineData("contains","]","%]%")]
    [InlineData("contains",@"\",@"%\\%")][InlineData("like",@"\",@"\\")]
    [InlineData("contains","*","%*%")][InlineData("contains","'","%'%")]
    public void Pattern_bytes_and_native_LIKE(string op,string input,string expected)
    {
        var query=Validate(JsonSerializer.Serialize(new{select=new[]{"id"},filters=new[]{new{or=new[]{new{col="label",op,value=input}}}}}));
        for(var i=0;i<3;i++)
        {
            var result=new PostgreSqlEngine().Compile(query);
            Assert.Equal(expected,result.Parameters["@p0"]);
            Assert.Contains("like @p0 ESCAPE '\\'",result.Sql);
            Assert.DoesNotContain("ilike",result.Sql);Assert.DoesNotContain("CAST",result.Sql);
        }
    }

    /// <summary>An impossible or loosely written date is refused by validation, before any SQL exists.</summary>
    [Theory]
    [InlineData("2025-02-30")][InlineData("2025-1-01")]
    public void Invalid_dates_rejected_before_compile(string date) => Assert.False(QueryBuilding.Validate(Source(),JsonSerializer.Serialize(new{filters=new[]{new{col="day",op="eq",value=date}}}),QueryLimits.ForRowCap(5)).IsSuccess);
}
