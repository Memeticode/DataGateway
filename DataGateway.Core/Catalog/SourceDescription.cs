using System.Text.Json;
using SqlKata.JsonQuery;

namespace DataGateway.Core.Catalog;

/// <summary>The answer to describe source: the source's public description and its query schema.</summary>
/// <param name="Source">The source's public description.</param>
/// <param name="QuerySchema">The columns and the rules for writing a query.</param>
public sealed record SourceDescribeResponse(
    SourceMetadata Source, QuerySchema QuerySchema);

/// <summary>Pure describe bricks: request body -> source name; contract -> describe response.</summary>
/// <remarks>Request refusals carry step <c>request</c> and a message that shows the body to send.</remarks>
public static class SourceDescription
{
    /// <summary>The refusal for a describe body that is not valid JSON.</summary>
    public static CatalogFailure InvalidJson { get; } = new("InvalidJson", "$",
        "The request body is not valid JSON. Send {\"source\": \"<name>\"} with a name from list sources.")
        { Step = "request" };

    /// <summary>Reads the source name from a describe body, which must be exactly <c>{"source": "&lt;name&gt;"}</c>.</summary>
    /// <remarks>Any other member, or a second <c>source</c>, is refused rather than ignored, so a caller who
    /// misspells or adds a parameter learns it at once. The name is returned as sent; the catalog reader chooses
    /// how to match it.</remarks>
    /// <param name="body">The parsed request body.</param>
    /// <returns>The source name, or an <c>InvalidStructure</c> or <c>InvalidJson</c> refusal.</returns>
    public static CatalogResult<string> ParseRequest(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return InvalidObject();
        var count = 0;
        JsonElement value = default;
        var namedSource = false;
        foreach (var property in body.EnumerateObject())
        {
            count++;
            namedSource = property.NameEquals("source");
            value = property.Value;
        }
        if (count != 1 || !namedSource) return InvalidObject();
        if (value.ValueKind != JsonValueKind.String)
            return CatalogResult<string>.Reject(new("InvalidStructure", "$.source",
                "source must be a JSON string: the name of a source from list sources.") { Step = "request" });
        try { return CatalogResult<string>.Success(value.GetString()!); }
        catch (InvalidOperationException) { return CatalogResult<string>.Reject(InvalidJson); }
    }

    /// <summary>Builds the describe answer for one source.</summary>
    /// <param name="contract">The source contract.</param>
    /// <param name="limits">The limits the source's queries run under, with its row cap.</param>
    /// <returns>The describe answer.</returns>
    public static SourceDescribeResponse Map(SourceContract contract, JsonQueryLimits limits) =>
        new(contract.Source, QuerySchema.For(contract, limits));

    /// <summary>The refusal for a body that is not an object with exactly one property, <c>source</c>.</summary>
    /// <returns>The <c>InvalidStructure</c> refusal at the root.</returns>
    private static CatalogResult<string> InvalidObject() => CatalogResult<string>.Reject(new(
        "InvalidStructure", "$",
        "Send a JSON object with exactly one property, source: {\"source\": \"<name>\"} with a name from list sources.")
        { Step = "request" });
}
