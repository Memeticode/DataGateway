using System.Text.Json.Serialization;

namespace DataGateway.Core.Catalog;

/// <summary>One expected failure: the public code, the JSON path it is about and a message that says what to
/// change, plus log-only diagnostics.</summary>
/// <remarks>Every step of a request (parsing, catalog, query, source) reports through this one shape, so the host
/// answers and logs every failure the same way (<see cref="Errors.ErrorEnvelope"/>).</remarks>
/// <param name="Code">The failure code: a public gateway code, a query library refusal or <c>InvalidCatalog</c>.</param>
/// <param name="Path">The JSON path of the part of the request or catalog at fault, rooted at <c>$</c>.</param>
/// <param name="Message">The correction the caller or data steward reads.</param>
public sealed record CatalogFailure(string Code, string Path, string Message)
{
    // Log-only diagnostics; only the edge may project and check the driver's text.
    /// <summary>The driver exception behind a source failure, for the log; never serialized.</summary>
    [JsonIgnore] public Exception? DriverException { get; init; }
    /// <summary>The catalog id of the source involved, for the log.</summary>
    [JsonIgnore] public int? SourceId { get; init; }
    /// <summary>The name of the source involved, for the log.</summary>
    [JsonIgnore] public string? SourceName { get; init; }
    /// <summary>The type name of the exception behind the failure, for the log.</summary>
    [JsonIgnore] public string? ExceptionType { get; init; }
    /// <summary>The provider's error number, where the source's engine recognizes one, for the log.</summary>
    [JsonIgnore] public int? SqlNumber { get; init; }
    /// <summary>The request step that failed, such as <c>request</c>, <c>catalog</c>, <c>connect</c> or
    /// <c>command</c>, for the log.</summary>
    [JsonIgnore] public string? Step { get; init; }

    /// <summary>A log-safe rendering of the failure.</summary>
    /// <remarks>Replaces the record's generated text, which would print <see cref="DriverException"/> and its
    /// driver message wherever the failure is logged or formatted.</remarks>
    /// <returns>The code, path, step, exception type, error number and message.</returns>
    public override string ToString() =>
        $"CatalogFailure {{ Code = {Code}, Path = {Path}, Step = {Step}, ExceptionType = {ExceptionType}, SqlNumber = {SqlNumber}, Message = {Message} }}";
}

/// <summary>A catalog operation returns either its value or a failure, never both.</summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class CatalogResult<T> where T : class
{
    /// <summary>The value of a successful result; null for a failure.</summary>
    private readonly T? value;

    /// <summary>Holds exactly one of the two; the factories guarantee which.</summary>
    /// <param name="value">The value, for a success.</param>
    /// <param name="failure">The failure, for a rejection.</param>
    private CatalogResult(T? value, CatalogFailure? failure) => (this.value, Failure) = (value, failure);

    /// <summary>The value of a successful result.</summary>
    /// <remarks>Throws on a failed result: reading the value of a failure is a bug, not a state to handle.</remarks>
    /// <exception cref="InvalidOperationException">The result is a failure.</exception>
    public T Value => value ?? throw new InvalidOperationException("A failed catalog result has no value.");
    /// <summary>The failure, or null for a success.</summary>
    public CatalogFailure? Failure { get; }
    /// <summary>Whether the result carries a value.</summary>
    public bool IsSuccess => Failure is null;

    /// <summary>A successful result.</summary>
    /// <param name="value">The value; must not be null.</param>
    /// <returns>The result carrying <paramref name="value"/>.</returns>
    public static CatalogResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
    }

    /// <summary>A failed result.</summary>
    /// <param name="failure">The failure; must not be null.</param>
    /// <returns>The result carrying <paramref name="failure"/>.</returns>
    public static CatalogResult<T> Reject(CatalogFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(null, failure);
    }
}
