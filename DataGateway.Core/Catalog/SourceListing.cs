using DataGateway.Core.Errors;
using System.Text.Json;

namespace DataGateway.Core.Catalog;

/// <summary>List input contains no connection, object, identifier or audit data.</summary>
public sealed record SourceListRow(string Name, string Title, string Description, string Grain, bool IsActive, int RowCap);

public sealed record SourceListResponse(IReadOnlyList<SourceMetadata> Sources);

/// <summary>Pure projection of catalog list rows to the public reasoning fields.</summary>
public static class SourceListing
{
    // One order for every list of source names: case-insensitive, then ordinal for a total order.
    private static readonly IComparer<string> NameOrder = Comparer<string>.Create((left, right) =>
    {
        var order = StringComparer.OrdinalIgnoreCase.Compare(left, right);
        return order != 0 ? order : StringComparer.Ordinal.Compare(left, right);
    });

    public static CatalogFailure InvalidJson { get; } = new("InvalidJson", "$",
        "The request body is not valid JSON. Send an empty JSON object, {}: list sources takes no parameters.") { Step = "request" };

    /// <summary>The refusal for a list body, or null for an empty object.</summary>
    public static CatalogFailure? CheckRequest(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && !body.EnumerateObject().Any() ? null : NotEmptyObject;

    private static CatalogFailure NotEmptyObject { get; } = new("InvalidStructure", "$",
        "Send an empty JSON object, {}: list sources takes no parameters.") { Step = "request" };

    public static CatalogFailure UnknownSource(IEnumerable<string> activeNames)
    {
        var names = activeNames.Order(NameOrder).ToArray();
        return new(DataGatewayError.UnknownSource.Code, "$.source", names.Length == 0
            ? "No source is active in the catalog. Ask the data steward to activate a source, then call list sources."
            : "Choose an active source. Available sources: " + string.Join(", ", names) + ".");
    }

    public static SourceListResponse Map(IEnumerable<SourceListRow> rows) => new(Array.AsReadOnly(rows
        .Where(row => row.IsActive)
        .OrderBy(row => row.Name, NameOrder)
        .Select(row => new SourceMetadata(row.Name, row.Title, row.Description, row.Grain, row.RowCap)).ToArray()));
}
