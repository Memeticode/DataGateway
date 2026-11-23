using DataGateway.Routes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace DataGateway.Page;

/// <summary>Each gateway's API document (OpenAPI 3.1) and its Scalar page for trying the operations by hand.</summary>
/// <remarks>Each gateway gets its own document, holding only its list, describe and query POSTs. The page and its
/// assets are mapped in their own group, so the host decides who may open it, independently of the API.</remarks>
internal static class ApiPage
{
    /// <summary>The base title of the document and the page, before the gateway name replaces it.</summary>
    internal const string Title = "DataGateway";

    /// <summary>Registers the gateway's OpenAPI document and the transformers that describe its operations.</summary>
    /// <remarks>The host policy, when set, adds its 401 code to every operation and replaces the 401 description with the
    /// host's own. That response does not require <c>incidentId</c>: an unrecorded refusal, such as the first leg of a
    /// host's access challenge, is not a failure and carries none.</remarks>
    /// <param name="services">The host's service collection.</param>
    /// <param name="registration">The gateway the document describes.</param>
    internal static void AddDocument(IServiceCollection services, GatewayRegistration registration) =>
        services.AddOpenApi(registration.DocumentName, options =>
        {
            options.ShouldInclude = description =>
                ReferenceEquals(description.ActionDescriptor.EndpointMetadata.OfType<GatewayRegistration>().LastOrDefault(), registration) &&
                description.HttpMethod == "POST" &&
                description.ActionDescriptor.EndpointMetadata.OfType<GatewayOperation>().LastOrDefault()?.Name is "list" or "describe" or "query";
            options.AddSchemaTransformer(GatewaySchemaNames.Collect);
            options.AddDocumentTransformer(async (document, context, token) =>
            {
                GatewaySchemaNames.Apply(document);
                await OperationDocument.DescribeDocument(document, context, token);
                document.Info.Title = registration.Name;
                if (registration.HostPolicy is { } policy)
                    document.Info.Description += " " + policy.Description;
            });
            options.AddOperationTransformer(async (operation, context, token) =>
            {
                var name = context.Description.ActionDescriptor.EndpointMetadata
                    .OfType<GatewayOperation>().LastOrDefault()?.Name;
                var entry = OperationDocument.Of(name);
                if (registration.HostPolicy is { } policy)
                    entry = entry with { Codes = [.. entry.Codes, policy.Failure.Code] };
                await OperationDocument.DescribeOperation(operation, name, entry);
                if (registration.HostPolicy is not { } configured)
                    return;
                var response = (OpenApiResponse)operation.Responses!["401"];
                response.Description = configured.ResponseDescription;
                ((OpenApiSchema)response.Content!["application/json"].Schema!).Required =
                    new HashSet<string> { "code", "path", "message" };
            });
        });

    /// <summary>Maps <c>/openapi/v1.json</c> and the Scalar page under the gateway's page group.</summary>
    /// <remarks>The document route is claimed when its endpoint is built, once parent group prefixes are known, so two
    /// gateways cannot serve their documents at the same address.</remarks>
    /// <param name="group">The gateway's page group.</param>
    /// <param name="registration">The gateway.</param>
    /// <param name="registry">Records the document route against other gateways.</param>
    internal static void Map(RouteGroupBuilder group, GatewayRegistration registration, GatewayRegistry registry)
    {
        var document = group.MapGet("/openapi/v1.json", async (HttpContext context) =>
        {
            var provider = context.RequestServices.GetRequiredKeyedService<IOpenApiDocumentProvider>(registration.DocumentName);
            var value = await provider.GetOpenApiDocumentAsync(context.RequestAborted);
            context.Response.ContentType = "application/json; charset=utf-8";
            using var buffer = new MemoryStream();
            await value.SerializeAsJsonAsync(buffer, OpenApiSpecVersion.OpenApi3_1, context.RequestAborted);
            buffer.Position = 0;
            await buffer.CopyToAsync(context.Response.Body, context.RequestAborted);
        }).WithMetadata(new GatewayOperation("document")).ExcludeFromDescription();
        document.Finally(builder =>
            registry.RecordPageRoute(registration.Name, ((RouteEndpointBuilder)builder).RoutePattern.RawText!));

        IEndpointConventionBuilder scalar = group.MapScalarApiReference("/scalar", (options, context) => Configure(options, registration, context));
        scalar.ExcludeFromDescription();
        // Respect host policy on the group; packaged assets must not bypass it.
        scalar.Finally(endpoint =>
        {
            endpoint.Metadata.Add(new GatewayOperation("page"));
            foreach (var open in endpoint.Metadata.OfType<IAllowAnonymous>().ToArray())
                endpoint.Metadata.Remove(open);
        });
    }

    /// <summary>The page settings every gateway shares.</summary>
    /// <remarks>The page loads nothing from outside the machine; calls go straight to DataGateway.</remarks>
    /// <param name="options">The Scalar options.</param>
    internal static void Configure(ScalarOptions options) =>
        options.WithTitle(Title).DisableDefaultFonts().DisableTelemetry().DisableAgent();

    /// <summary>The page settings for one request: the gateway's name as title, and the address of its document.</summary>
    /// <remarks>The document address is worked out from the page's own request path, so it stays right under any
    /// parent group prefix the host adds. The leading <c>/</c> is trimmed because the page script resolves the
    /// document address against the site's base path plus <c>/</c>. A path without a leading slash therefore stays
    /// correct under any path base or parent group, and the page does not have to add the base path itself.</remarks>
    /// <param name="options">The Scalar options.</param>
    /// <param name="registration">The gateway.</param>
    /// <param name="context">The page request.</param>
    internal static void Configure(ScalarOptions options, GatewayRegistration registration, HttpContext context)
    {
        Configure(options);
        var path = context.Request.Path.ToUriComponent().TrimEnd('/');
        if (context.Request.RouteValues["documentName"] is not null)
            path = path[..path.LastIndexOf('/')];
        var prefix = path[..path.LastIndexOf('/')];
        var documentPath = (prefix + "/openapi/v1.json").TrimStart('/');
        options.WithTitle(registration.Name).WithOpenApiRoutePattern(documentPath);
    }
}
