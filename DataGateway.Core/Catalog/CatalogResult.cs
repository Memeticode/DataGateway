using System.Text.Json.Serialization;

namespace DataGateway.Core.Catalog;

public sealed record CatalogFailure(string Code, string Path, string Message)
{
    // Log-only diagnostics; only the edge may project and check the driver's text.
    [JsonIgnore] public Exception? DriverException { get; init; }
    [JsonIgnore] public int? SourceId { get; init; }
    [JsonIgnore] public string? SourceName { get; init; }
    [JsonIgnore] public string? ExceptionType { get; init; }
    [JsonIgnore] public int? SqlNumber { get; init; }
    [JsonIgnore] public string? Step { get; init; }

    public override string ToString() =>
        $"CatalogFailure {{ Code = {Code}, Path = {Path}, Step = {Step}, ExceptionType = {ExceptionType}, SqlNumber = {SqlNumber}, Message = {Message} }}";
}

/// <summary>A catalog operation returns either its value or a failure, never both.</summary>
public sealed class CatalogResult<T> where T : class
{
    private readonly T? value;

    private CatalogResult(T? value, CatalogFailure? failure) => (this.value, Failure) = (value, failure);

    public T Value => value ?? throw new InvalidOperationException("A failed catalog result has no value.");
    public CatalogFailure? Failure { get; }
    public bool IsSuccess => Failure is null;

    public static CatalogResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
    }

    public static CatalogResult<T> Reject(CatalogFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(null, failure);
    }
}
