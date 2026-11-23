using System.Text.Json.Serialization;
using DataGateway.Core.Catalog;

namespace DataGateway.Core.Errors;

/// <summary>The public error body with a passed-in incident ID; property order is the wire order.</summary>
/// <param name="Code">The public code.</param>
/// <param name="Path">The JSON path at fault; the root for a fixed-body code.</param>
/// <param name="Message">What to change, or the fixed message.</param>
/// <param name="IncidentId">The id that ties the answer to its log line; left out of the JSON when null.</param>
public sealed record ErrorBody(string Code, string Path, string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IncidentId);

/// <summary>The HTTP status and body that answer one failure.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Body">The JSON body.</param>
public sealed record ErrorReply(int Status, ErrorBody Body);

/// <summary>Pure failure plus passed-in incident ID -> envelope. InternalError and UnexpectedError have fixed bodies. Other client-visible codes keep their path and message. Any other code, internal or unknown,
/// is answered as InternalError: its code, message and diagnostics stay in the log only.</summary>
/// <remarks>The redaction is by code, not by content: an undeclared code's message may name a view or carry driver
/// text, so none of it is trusted onto the wire.</remarks>
public static class ErrorEnvelope
{
    /// <summary>The fixed message of <c>UnexpectedError</c>.</summary>
    public const string UnexpectedErrorMessage = DataGatewayError.UnexpectedErrorMessage;
    /// <summary>The failure to report when an exception nothing expected reaches the edge.</summary>
    public static CatalogFailure Unexpected { get; } =
        new(DataGatewayError.UnexpectedError.Code, "$", UnexpectedErrorMessage);

    /// <summary>The fixed message of <c>InternalError</c>.</summary>
    public const string InternalErrorMessage = DataGatewayError.InternalErrorMessage;

    /// <summary>The answer for one failure.</summary>
    /// <param name="failure">The failure to answer.</param>
    /// <param name="incidentId">The incident id to put in the body; null leaves it out.</param>
    /// <returns>The status and body.</returns>
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
