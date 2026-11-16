using System.Text.Json.Serialization;
using DataGateway.Core.Catalog;

namespace DataGateway.Core.Errors;

/// <summary>The public error body with a passed-in incident ID; property order is the wire order.</summary>
public sealed record ErrorBody(string Code, string Path, string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IncidentId);

/// <summary>The HTTP status and body that answer one failure.</summary>
public sealed record ErrorReply(int Status, ErrorBody Body);

/// <summary>Pure failure plus passed-in incident ID -> envelope. InternalError and UnexpectedError have fixed bodies. Other client-visible codes keep their path and message. Any other code, internal or unknown,
/// is answered as InternalError: its code, message and diagnostics stay in the log only.</summary>
public static class ErrorEnvelope
{
    public const string UnexpectedErrorMessage = DataGatewayError.UnexpectedErrorMessage;
    public static CatalogFailure Unexpected { get; } =
        new(DataGatewayError.UnexpectedError.Code, "$", UnexpectedErrorMessage);

    public const string InternalErrorMessage = DataGatewayError.InternalErrorMessage;

    public static ErrorReply From(CatalogFailure failure, string? incidentId)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (DataGatewayError.Find(failure.Code) is { FixedMessage: { } fixedMessage } fixedBody)
            return new(fixedBody.HttpStatus!.Value, new(fixedBody.Code, "$", fixedMessage, incidentId));

        if (DataGatewayError.HttpStatusFor(failure.Code) is { } status)
            return new(status, new(failure.Code, failure.Path, failure.Message, incidentId));

        var fallback = DataGatewayError.InternalError;
        return new(fallback.HttpStatus!.Value, new(fallback.Code, "$", fallback.FixedMessage!, incidentId));
    }
}
