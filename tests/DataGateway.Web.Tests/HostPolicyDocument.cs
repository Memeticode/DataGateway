using Microsoft.Extensions.DependencyInjection;
using DataGateway.Core.Errors;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi;
using SharedDocument = DataGateway.Page.OperationDocument;

namespace DataGateway.Web.Page;

// The example host adds its sign-in policy to the shared operation descriptions.
// SharedDocument owns every operation description; this host adds only its own policy.
internal static class HostPolicyDocument
{
    internal const string QueryExample = SharedDocument.QueryExample;

    internal static SharedDocument.Entry Of(string? operation)
    {
        var entry = SharedDocument.Of(operation);
        return entry with { Codes = [.. entry.Codes, DataGatewayError.NotAuthenticated.Code] };
    }

    internal static int StatusOf(string code) => SharedDocument.StatusOf(code);
    internal static IReadOnlyList<(int Status, string[] Codes)> Errors(string operation) =>
        SharedDocument.Errors(Of(operation));

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
