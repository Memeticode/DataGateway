using System.Text.Json;
using DataGateway.Failures;
using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;

namespace DataGateway.Routes;

internal sealed record RouteRequest(string Operation, byte[] Body)
{
    internal static CatalogFailure TooLarge { get; } = new("StructuralLimitExceeded", "$",
        "The request body is larger than DataGateway accepts. Send a smaller request.") { Step = "request" };
    internal static CatalogFailure Unreadable { get; } = new("InvalidJson", "$",
        "The request body could not be read in full. Send the complete JSON body in one request.") { Step = "request" };

    internal static CatalogFailure Refusal(BadHttpRequestException refused) =>
        (refused.StatusCode == 413 ? TooLarge : Unreadable) with { ExceptionType = refused.GetType().Name };

    internal static async Task HandleAsync<T>(HttpContext context, string operation,
        FailureLog failures, Func<byte[], Task<CatalogResult<T>>> run) where T : class
    {
        if (await ReadAsync(context, operation, failures) is not { } received) return;
        var result = await run(received.Body);
        await RouteReply.Send(context, failures, received, result);
    }

    internal static async Task<RouteRequest?> ReadAsync(HttpContext context, string operation, FailureLog failures)
    {
        var token = context.RequestAborted;
        using var buffer = new MemoryStream(); // Kestrel's request size limit bounds it.
        try { await context.Request.Body.CopyToAsync(buffer, token); }
        catch (Exception error) when (token.IsCancellationRequested &&
            error is OperationCanceledException or IOException or JsonException) { return null; }
        catch (BadHttpRequestException refused)
        {
            var failure = Refusal(refused);
            await RouteReply.Fail(context, ErrorEnvelope.From(failure,
                failures.Write(context, operation, body: null, failure)));
            return null;
        }
        var bytes = buffer.ToArray();
        RequestTrail.Of(context).Body = bytes;
        return new(operation, bytes);
    }
}
