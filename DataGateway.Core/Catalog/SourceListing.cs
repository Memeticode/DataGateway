using DataGateway.Core.Errors;
using System.Text.Json;

namespace DataGateway.Core.Catalog;

/// <summary>List input contains no connection, object, identifier or audit data.</summary>
/// <param name="Name">The source name callers use.</param>
/// <param name="Title">A short human title.</param>
/// <param name="Description">What the source holds.</param>
/// <param name="Grain">What one row represents.</param>
/// <param name="IsActive">Whether the source is offered; inactive rows are left out of the list.</param>
/// <param name="RowCap">The most rows one query returns.</param>
public sealed record SourceListRow(string Name, string Title, string Description, string Grain, bool IsActive, int RowCap);

/// <summary>The answer to list sources.</summary>
/// <param name="Sources">The active sources, in name order.</param>
public sealed record SourceListResponse(IReadOnlyList<SourceMetadata> Sources);

/// <summary>Pure projection of catalog list rows to the public reasoning fields.</summary>
public static class SourceListing
{
    // One order for every list of source names: case-insensitive, then ordinal for a total order.
    /// <summary>The order of source names in every list and correction.</summary>
    private static readonly IComparer<string> NameOrder = Comparer<string>.Create((left, right) =>
    {
        var order = StringComparer.OrdinalIgnoreCase.Compare(left, right);
        return order != 0 ? order : StringComparer.Ordinal.Compare(left, right);
    });

    /// <summary>The refusal for a list body that is not valid JSON.</summary>
    public static CatalogFailure InvalidJson { get; } = new("InvalidJson", "$",
        "The request body is not valid JSON. Send an empty JSON object, {}: list sources takes no parameters.") { Step = "request" };

    /// <summary>The refusal for a list body, or null for an empty object.</summary>
    /// <param name="body">The parsed request body.</param>
    /// <returns>Null when the body is <c>{}</c>; otherwise the <c>InvalidStructure</c> refusal.</returns>
    public static CatalogFailure? CheckRequest(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && !body.EnumerateObject().Any() ? null : NotEmptyObject;

    /// <summary>The refusal for a list body other than an empty object.</summary>
    private static CatalogFailure NotEmptyObject { get; } = new("InvalidStructure", "$",
        "Send an empty JSON object, {}: list sources takes no parameters.") { Step = "request" };

    /// <summary>The <c>UnknownSource</c> refusal, naming the active sources the caller can choose from.</summary>
    /// <remarks>Listing the names lets a caller correct a misspelled source in one step instead of calling list
    /// sources again.</remarks>
    /// <param name="activeNames">The names of the active sources.</param>
    /// <returns>The refusal at <c>$.source</c>; when no source is active, it says so instead.</returns>
    public static CatalogFailure UnknownSource(IEnumerable<string> activeNames)
    {
        var names = activeNames.Order(NameOrder).ToArray();
        return new(DataGatewayError.UnknownSource.Code, "$.source", names.Length == 0
            ? "No source is active in the catalog. Ask the data steward to activate a source, then call list sources."
            : "Choose an active source. Available sources: " + string.Join(", ", names) + ".");
    }

    /// <summary>Builds the list answer: the active sources only, in name order.</summary>
    /// <param name="rows">The validated list rows.</param>
    /// <returns>The list answer.</returns>
    public static SourceListResponse Map(IEnumerable<SourceListRow> rows) => new(Array.AsReadOnly(rows
        .Where(row => row.IsActive)
        .OrderBy(row => row.Name, NameOrder)
        .Select(row => new SourceMetadata(row.Name, row.Title, row.Description, row.Grain, row.RowCap)).ToArray()));
}
