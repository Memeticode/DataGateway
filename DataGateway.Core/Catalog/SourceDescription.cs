using System.Text.Json;
using SqlKata.JsonQuery;

namespace DataGateway.Core.Catalog;

public sealed record SourceDescribeResponse(
    SourceMetadata Source, QuerySchema QuerySchema);

/// <summary>Pure describe bricks: request body -> source name; contract -> describe response.</summary>
public static class SourceDescription
{
    public static CatalogFailure InvalidJson { get; } = new("InvalidJson", "$",
        "The request body is not valid JSON. Send {\"source\": \"<name>\"} with a name from list sources.")
        { Step = "request" };

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

    public static SourceDescribeResponse Map(SourceContract contract, JsonQueryLimits limits) =>
        new(contract.Source, QuerySchema.For(contract, limits));

    private static CatalogResult<string> InvalidObject() => CatalogResult<string>.Reject(new(
        "InvalidStructure", "$",
        "Send a JSON object with exactly one property, source: {\"source\": \"<name>\"} with a name from list sources.")
        { Step = "request" });
}
