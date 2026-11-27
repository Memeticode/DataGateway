using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DataGateway.Routes;

namespace DataGateway.Page;

/// <summary>One gateway's browse page: a query form over the gateway's list, describe and query routes, served from
/// files built into this library.</summary>
/// <remarks>The page calls nothing but those routes, wherever MapDataGateway put them: each request for the page writes
/// their paths into it, as the app's link generator makes them for that request. Policy on the page's group covers every
/// file; no endpoint here allows anonymous access of its own.</remarks>
internal static class BrowsePage
{
    internal const string Path = "/browse-data-gateway";

    // Same-origin files and calls only: no inline code, no other origin, no framing, no form posts.
    internal const string ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; " +
        "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    // The attribute on the page's root element that each page request fills with the gateway's name and route paths.
    internal const string Placeholder = "data-gateway=\"\"";

    internal static IReadOnlyList<(string Name, string ContentType)> Files { get; } =
    [
        ("index.html", "text/html; charset=utf-8"),
        ("browse.css", "text/css; charset=utf-8"),
        ("browse.js", "text/javascript; charset=utf-8"),
        ("api.js", "text/javascript; charset=utf-8"),
        ("query.js", "text/javascript; charset=utf-8"),
        ("form.js", "text/javascript; charset=utf-8"),
        ("view.js", "text/javascript; charset=utf-8")
    ];

    internal static void Map(RouteGroupBuilder group, GatewayRegistration registration, GatewayRegistry registry)
    {
        var links = ((IEndpointRouteBuilder)group).ServiceProvider.GetRequiredService<LinkGenerator>();
        foreach (var (name, contentType) in Files)
        {
            var bytes = Read(name);
            IEndpointConventionBuilder endpoint;
            if (name == "index.html")
            {
                var page = Encoding.UTF8.GetString(bytes);
                endpoint = group.MapGet(Path + "/", (HttpContext context) => Index(context, registration, links, page, contentType));
                endpoint.Finally(builder =>
                    registry.RecordBrowsePageRoute(registration.Name, ((RouteEndpointBuilder)builder).RoutePattern.RawText!));
            }
            else
                endpoint = group.MapGet(Path + "/" + name, (HttpContext context) => Send(context, bytes, contentType));
            endpoint.WithMetadata(new GatewayOperation("browse")).ExcludeFromDescription();
        }
    }

    // Routing matches the path with and without a trailing slash alike. The page's relative links need the slash, so
    // redirect to it relatively, as the API page does.
    private static Task Index(HttpContext context, GatewayRegistration registration, LinkGenerator links, string page,
        string contentType)
    {
        if (context.Request.Path.Value?.EndsWith('/') != true)
        {
            context.Response.Redirect(Path.TrimStart('/') + "/");
            return Task.CompletedTask;
        }
        // By route name, so this request's route values (a tenant, say) fill the same parameters in the gateway's routes.
        // Null where those routes are not mapped, or need a value this request does not have.
        string? Route(string operation) =>
            links.GetPathByRouteValues(context, GatewayRoutes.EndpointName(registration.Name, operation));
        var gateway = JsonSerializer.Serialize(new
        {
            name = registration.Name,
            routes = new
            {
                list = Route(SourcesListRoute.Operation),
                describe = Route(SourcesDescribeRoute.Operation),
                query = Route(QueryRoute.Operation)
            }
        });
        var html = page.Replace(Placeholder, $"data-gateway=\"{HtmlEncoder.Default.Encode(gateway)}\"", StringComparison.Ordinal);
        return Send(context, Encoding.UTF8.GetBytes(html), contentType);
    }

    private static Task Send(HttpContext context, byte[] bytes, string contentType)
    {
        var headers = context.Response.Headers;
        headers.ContentType = contentType;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.CacheControl = "no-cache";
        context.Response.ContentLength = bytes.Length;
        return context.Response.Body.WriteAsync(bytes, context.RequestAborted).AsTask();
    }

    private static byte[] Read(string name)
    {
        using var stream = typeof(BrowsePage).Assembly.GetManifestResourceStream("Browse/" + name)
            ?? throw new InvalidOperationException($"The browse page file {name} is not in DataGateway; rebuild it.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
