using System.Text.Json.Nodes;
using DataGateway.Routes;
using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.OpenApi;
using SqlKata.JsonQuery;

namespace DataGateway.Page;

/// <summary>What the API document says about each operation: summary, request body schema and example, the 200
/// answer, and every error code it can return, grouped by HTTP status.</summary>
/// <remarks>The codes are listed by hand per operation; each one's status is read from the public error envelope,
/// so the document shows the status the wire actually answers with.</remarks>
internal static class OperationDocument
{
    /// <summary>The codes any operation returns for a body it cannot read or parse.</summary>
    private static readonly string[] Request = ["InvalidJson", "InvalidStructure", "StructuralLimitExceeded"];
    /// <summary>The codes any operation can return whatever it was sent.</summary>
    private static readonly string[] Always = [DataGatewayError.CatalogUnavailable.Code, DataGatewayError.QueryTimeout.Code, DataGatewayError.InternalError.Code, DataGatewayError.UnexpectedError.Code];
    /// <summary>The query operation's example body.</summary>
    internal const string QueryExample = """{"source":"sales_by_line","query":{"group_by":["country"],"aggregates":[{"fn":"sum","col":"line_total"}],"order_by":[{"col":"sum_line_total","dir":"desc"}],"limit":5}}""";

    /// <summary>One operation's document entry.</summary>
    /// <param name="Summary">The operation's short title.</param>
    /// <param name="Description">The operation's description.</param>
    /// <param name="Body">Builds a fresh request body schema.</param>
    /// <param name="Example">The example request body, as JSON text.</param>
    /// <param name="Answer">The 200 response's description.</param>
    /// <param name="Codes">Every error code the operation can return.</param>
    internal sealed record Entry(string Summary, string Description, Func<OpenApiSchema> Body,
        string Example, string Answer, string[] Codes);

    /// <summary>The entry for an operation.</summary>
    /// <param name="operation">The operation name: list, describe or query.</param>
    /// <returns>The operation's entry.</returns>
    /// <exception cref="InvalidOperationException">The operation has no entry.</exception>
    internal static Entry Of(string? operation) => operation switch
    {
        SourcesListRoute.Operation => new("List sources", "List available sources and their metadata.", ListBody,
            "{}", "Available sources with names, titles, descriptions, grains and row caps.", [.. Request, .. Always]),
        SourcesDescribeRoute.Operation => new("Describe source", "Describe one source: its columns and query rules in one querySchema.", DescribeBody,
            """{"source":"sales_by_line"}""", "Source metadata and one querySchema: every cataloged column with its flags, and the rules of each query clause.", [.. Request, DataGatewayError.UnknownSource.Code, .. Always]),
        QueryRoute.Operation => new("Query source", "Run a read-only query against a named source.", QueryBody,
            QueryExample, "Source name, output columns, rows, rowCount, more and hint. more is true when rows were left out, with or without limit. hint states whether the result set was capped, no source rows matched or the result is complete; capped queries without order_by also state that rows are in no particular order.",
            [.. Enum.GetNames<JsonQueryErrorCode>(), DataGatewayError.UnknownSource.Code, DataGatewayError.SourceUnavailable.Code, .. Always]),
        _ => throw new InvalidOperationException($"The API document has no entry for operation '{operation}'.")
    };

    /// <summary>The HTTP status a code is answered with.</summary>
    /// <remarks>A code that never reaches a caller cannot be documented.</remarks>
    /// <param name="code">An error code.</param>
    /// <returns>The status.</returns>
    /// <exception cref="InvalidOperationException">The envelope answers the code as another code.</exception>
    internal static int StatusOf(string code)
    {
        var reply = ErrorEnvelope.From(new CatalogFailure(code, "$", code), null);
        if (reply.Body.Code != code)
            throw new InvalidOperationException($"Code '{code}' is answered as {reply.Body.Code}; it cannot be documented.");
        return reply.Status;
    }

    /// <summary>An operation's error codes grouped by status.</summary>
    /// <param name="operation">The operation name.</param>
    /// <returns>Each status, lowest first, with its codes.</returns>
    internal static IReadOnlyList<(int Status, string[] Codes)> Errors(string operation) => Errors(Of(operation));

    /// <summary>An entry's error codes grouped by status.</summary>
    /// <param name="entry">The operation's entry.</param>
    /// <returns>Each status, lowest first, with its codes.</returns>
    internal static IReadOnlyList<(int Status, string[] Codes)> Errors(Entry entry) =>
        entry.Codes.GroupBy(StatusOf).OrderBy(group => group.Key)
            .Select(group => (group.Key, group.ToArray())).ToArray();

    /// <summary>Operation transformer: describes the operation its endpoint metadata names.</summary>
    /// <param name="operation">The generated operation.</param>
    /// <param name="context">The generator's context, with the endpoint's metadata.</param>
    /// <param name="token">Unused.</param>
    /// <returns>A completed task.</returns>
    internal static Task DescribeOperation(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken token)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var name = metadata.OfType<GatewayOperation>().LastOrDefault()?.Name ??
            metadata.OfType<IEndpointNameMetadata>().FirstOrDefault()?.EndpointName;
        return DescribeOperation(operation, name, Of(name));
    }

    /// <summary>Writes an entry into an operation: id, summary, required body with example, 200 description and one
    /// response per error status.</summary>
    /// <param name="operation">The generated operation.</param>
    /// <param name="name">The operation name, used as the operation id.</param>
    /// <param name="entry">The operation's entry.</param>
    /// <returns>A completed task.</returns>
    internal static Task DescribeOperation(OpenApiOperation operation, string? name, Entry entry)
    {
        operation.OperationId = name;
        operation.Summary = entry.Summary;
        operation.Description = entry.Description;
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                // The OpenAPI writer omits empty collections; a value-backed empty object keeps list's {} example.
                ["application/json"] = new() { Schema = entry.Body(), Example = entry.Example == "{}"
                    ? JsonValue.Create(new Dictionary<string, object?>()) : JsonNode.Parse(entry.Example) }
            }
        };
        operation.Responses!["200"].Description = entry.Answer;
        foreach (var (status, codes) in Errors(entry))
        {
            var description = $"{ReasonPhrases.GetReasonPhrase(status)}. The code is one of: {string.Join(", ", codes)}.";
            operation.Responses[status.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new OpenApiResponse
            {
                Description = description,
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = ErrorSchema(codes) } }
            };
        }
        return Task.CompletedTask;
    }

    /// <summary>Document transformer: sets the document's info, removes server addresses and fixes the query answer's
    /// <c>hint</c>.</summary>
    /// <remarks>No server addresses, so the document names no host: a client resolves the operations against the
    /// address it loaded the document from.</remarks>
    /// <param name="document">The document.</param>
    /// <param name="context">Unused.</param>
    /// <param name="token">Unused.</param>
    /// <returns>A completed task.</returns>
    internal static Task DescribeDocument(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken token)
    {
        document.Info = new OpenApiInfo { Title = ApiPage.Title, Version = "v1", Description = "A read-only data gateway." };
        document.Servers = [];
        // hint is computed, so the generator lists it as optional and nullable; every answer carries it.
        if (document.Components?.Schemas?["QueryResponse"] is OpenApiSchema answer)
        {
            answer.Required!.Add("hint");
            answer.Properties!["hint"] = new OpenApiSchema { Type = JsonSchemaType.String };
        }
        return Task.CompletedTask;
    }

    /// <summary>The list body schema: an empty object.</summary>
    /// <returns>A new schema.</returns>
    private static OpenApiSchema ListBody() => new()
    {
        Type = JsonSchemaType.Object, AdditionalPropertiesAllowed = false, MaxProperties = 0,
        Properties = new Dictionary<string, IOpenApiSchema>()
    };

    /// <summary>The describe body schema: one required source name.</summary>
    /// <returns>A new schema.</returns>
    private static OpenApiSchema DescribeBody() => new()
    {
        Type = JsonSchemaType.Object, AdditionalPropertiesAllowed = false, Required = new HashSet<string> { "source" },
        Properties = new Dictionary<string, IOpenApiSchema> { ["source"] = Source() }
    };

    /// <summary>The query body schema: a source name and a query object.</summary>
    /// <remarks>The query is described in words, not as a schema: its rules depend on the source, and describe returns
    /// them as that source's <c>querySchema</c>.</remarks>
    /// <returns>A new schema.</returns>
    private static OpenApiSchema QueryBody() => new()
    {
        Type = JsonSchemaType.Object, AdditionalPropertiesAllowed = false, Required = new HashSet<string> { "source", "query" },
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["source"] = Source(),
            ["query"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = "Use SqlKata.JsonQuery: select, filters with nested and/or, group_by, aggregates count/sum/avg/min/max, order_by and limit. Describe source returns that source's querySchema."
            }
        }
    };

    /// <summary>The schema of a source name.</summary>
    /// <returns>A new schema.</returns>
    private static OpenApiSchema Source() => new() { Type = JsonSchemaType.String, Description = "A name from list sources." };

    /// <summary>The error body schema for one status, its <c>code</c> limited to that status's codes.</summary>
    /// <param name="codes">The codes answered with that status.</param>
    /// <returns>A new schema.</returns>
    internal static OpenApiSchema ErrorSchema(string[] codes) => new()
    {
        Type = JsonSchemaType.Object, AdditionalPropertiesAllowed = false,
        Required = new HashSet<string> { "code", "path", "message", "incidentId" },
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["code"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = codes.Select(code => (JsonNode)JsonValue.Create(code)!).ToList() },
            ["path"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Where in the request; $ is the whole request." },
            ["message"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "How to fix the request." },
            ["incidentId"] = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[0-9a-f]{32}$", Description = "The failure's log line carries the same value." }
        }
    };
}
