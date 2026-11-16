using DataGateway.Routes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace DataGateway.Page;

internal static class ApiPage
{
    internal const string Title = "DataGateway";

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
                document.Info.Title = registration.McpTitle ?? registration.Name;
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

    // The page loads nothing from outside the machine; calls go straight to DataGateway.
    internal static void Configure(ScalarOptions options) =>
        options.WithTitle(Title).DisableDefaultFonts().DisableTelemetry().DisableAgent();

    internal static void Configure(ScalarOptions options, GatewayRegistration registration, HttpContext context)
    {
        Configure(options);
        var path = context.Request.Path.ToUriComponent().TrimEnd('/');
        if (context.Request.RouteValues["documentName"] is not null)
            path = path[..path.LastIndexOf('/')];
        var prefix = path[..path.LastIndexOf('/')];
        var documentPath = (prefix + "/openapi/v1.json").TrimStart('/');
        options.WithTitle(registration.McpTitle ?? registration.Name).WithOpenApiRoutePattern(documentPath);
    }
}
