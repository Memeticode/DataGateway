using Microsoft.Extensions.DependencyInjection;
using DataGateway.Core.Errors;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi;
using SharedDocument = DataGateway.Page.OperationDocument;

namespace DataGateway.Web.Page;

/// <summary>The example sign-in host's view of the API page's operation descriptions: the shared descriptions with
/// the host's own sign-in error added to every operation.</summary>
/// <remarks>The example host adds its sign-in policy to the shared operation descriptions.
/// <see cref="SharedDocument"/> owns every operation description; this host adds only its own policy, so the
/// tests that compare the README's error table and examples with the page read the codes a signed-in host
/// actually documents.</remarks>
internal static class HostPolicyDocument
{
    /// <summary>The shared query example, re-exported for tests that compare it with the README.</summary>
    internal const string QueryExample = SharedDocument.QueryExample;

    /// <summary>The shared description of an operation with <c>NotAuthenticated</c> added to its error codes.</summary>
    /// <param name="operation">The operation name, such as <c>list</c>.</param>
    internal static SharedDocument.Entry Of(string? operation)
    {
        var entry = SharedDocument.Of(operation);
        return entry with { Codes = [.. entry.Codes, DataGatewayError.NotAuthenticated.Code] };
    }

    /// <summary>The HTTP status the page documents for an error code; the shared mapping.</summary>
    internal static int StatusOf(string code) => SharedDocument.StatusOf(code);
    /// <summary>An operation's documented error codes grouped by status, sign-in included.</summary>
    internal static IReadOnlyList<(int Status, string[] Codes)> Errors(string operation) =>
        SharedDocument.Errors(Of(operation));

    /// <summary>Registers an OpenAPI document that describes the gateway with the host's sign-in policy and leaves out
    /// endpoints that carry gateway registration metadata.</summary>
    /// <remarks>No test calls this today; it mirrors how the example host once built its page document.</remarks>
    /// <param name="services">The host's service collection.</param>
    internal static void AddDocument(IServiceCollection services)
    {
        Func<IServiceCollection, Action<OpenApiOptions>, IServiceCollection> add = OpenApiServiceCollectionExtensions.AddOpenApi;
        add(services, options =>
        {
            var include = options.ShouldInclude;
            options.ShouldInclude = description => include(description) &&
                !description.ActionDescriptor.EndpointMetadata.OfType<GatewayRegistration>().Any();
            options.AddDocumentTransformer(async (document, context, token) =>
            {
                await SharedDocument.DescribeDocument(document, context, token);
                document.Info.Description = "A read-only data gateway. Every call needs Windows sign-in.";
            });
            options.AddOperationTransformer(DescribeOperation);
        });
    }

    /// <summary>Describes one operation from the shared description, then extends its 401 answer with the sign-in
    /// handshake note and marks code, path and message required.</summary>
    private static async Task DescribeOperation(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken token)
    {
        var name = context.Description.ActionDescriptor.EndpointMetadata.OfType<IEndpointNameMetadata>().FirstOrDefault()?.EndpointName;
        await SharedDocument.DescribeOperation(operation, name, Of(name));
        var response = (OpenApiResponse)operation.Responses!["401"];
        response.Description += " The first leg of sign-in answers with WWW-Authenticate: Negotiate and no incidentId; a refused handshake adds an incidentId.";
        ((OpenApiSchema)response.Content!["application/json"].Schema!).Required =
            new HashSet<string> { "code", "path", "message" };
    }
}
