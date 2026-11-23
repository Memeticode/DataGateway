using System.Text.Json;
using DataGateway.Failures;
using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;

namespace DataGateway.Routes;

/// <summary>A request body read in full, with its operation: what a route hands to the operation and to the log
/// line.</summary>
/// <param name="Operation">The operation name.</param>
/// <param name="Body">The request body as received.</param>
internal sealed record RouteRequest(string Operation, byte[] Body)
{
    /// <summary>The refusal for a body over the server's request size limit.</summary>
    internal static CatalogFailure TooLarge { get; } = new("StructuralLimitExceeded", "$",
        "The request body is larger than DataGateway accepts. Send a smaller request.") { Step = "request" };
    /// <summary>The refusal for a body the server could not read in full.</summary>
    internal static CatalogFailure Unreadable { get; } = new("InvalidJson", "$",
        "The request body could not be read in full. Send the complete JSON body in one request.") { Step = "request" };

    /// <summary>The refusal for a body the server would not read: too large for status 413, otherwise unreadable.</summary>
    /// <param name="refused">The server's exception; only its type name is kept.</param>
    /// <returns>The failure.</returns>
    internal static CatalogFailure Refusal(BadHttpRequestException refused) =>
        (refused.StatusCode == 413 ? TooLarge : Unreadable) with { ExceptionType = refused.GetType().Name };

    /// <summary>Reads the body, runs the operation and answers.</summary>
    /// <typeparam name="T">The answer type.</typeparam>
    /// <param name="context">The request.</param>
    /// <param name="operation">The operation name.</param>
    /// <param name="failures">The gateway's failure log.</param>
    /// <param name="run">Runs the operation on the body.</param>
    /// <returns>A task that completes once the request is answered.</returns>
    internal static async Task HandleAsync<T>(HttpContext context, string operation,
        FailureLog failures, Func<byte[], Task<CatalogResult<T>>> run) where T : class
    {
        if (await ReadAsync(context, operation, failures) is not { } received) return;
        var result = await run(received.Body);
        await RouteReply.Send(context, failures, received, result);
    }

    /// <summary>Reads the whole body and records it in the trail.</summary>
    /// <remarks>The body is read as bytes, not bound by the framework, so the operation decides what is invalid. A
    /// read the server refuses is logged and answered here; a read cut short by the caller leaving is not a
    /// failure.</remarks>
    /// <param name="context">The request.</param>
    /// <param name="operation">The operation name.</param>
    /// <param name="failures">The gateway's failure log.</param>
    /// <returns>The request, or null when it was already answered or the caller has gone.</returns>
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
