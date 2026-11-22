using DataGateway.Core.Queries;

namespace DataGateway.Core.Errors;

/// <summary>The fixed public gateway codes and what a client sees for each. Engines pick from
/// these; the private constructor means no other code can add one.</summary>
public sealed class DataGatewayError
{
    internal const string UnexpectedErrorMessage = "An unexpected error stopped DataGateway from completing the request. Tell the user, give the incidentId and stop; do not retry.";
    internal const string InternalErrorMessage = "DataGateway could not complete the request because of a fault on its side, not in the request. Contact the DataGateway administrator. Tell the user, give the incidentId and stop; do not retry.";

    public static DataGatewayError UnknownSource { get; } = new(nameof(UnknownSource), 404);
    public static DataGatewayError QueryTimeout { get; } = new(nameof(QueryTimeout), 504);
    public static DataGatewayError SourceUnavailable { get; } = new(nameof(SourceUnavailable), 503);
    public static DataGatewayError CatalogUnavailable { get; } = new(nameof(CatalogUnavailable), 503);
    public static DataGatewayError NotAuthenticated { get; } = new(nameof(NotAuthenticated), 401);
    public static DataGatewayError InternalError { get; } = new(nameof(InternalError), 500, InternalErrorMessage);
    public static DataGatewayError UnexpectedError { get; } = new(nameof(UnexpectedError), 500, UnexpectedErrorMessage);

    // Log lines only: a cancelled request gets no reply, so these never reach a client.
    public static DataGatewayError QueryCancelled { get; } = new(nameof(QueryCancelled), null);
    public static DataGatewayError CatalogCancelled { get; } = new(nameof(CatalogCancelled), null);

    public static IReadOnlyList<DataGatewayError> All { get; } = Array.AsReadOnly(
    [
        UnknownSource, QueryTimeout, SourceUnavailable, CatalogUnavailable, NotAuthenticated,
        InternalError, UnexpectedError, QueryCancelled, CatalogCancelled
    ]);

    private DataGatewayError(string code, int? httpStatus, string? fixedMessage = null)
    {
        Code = code;
        HttpStatus = httpStatus;
        FixedMessage = fixedMessage;
    }

    /// <summary>The wire text; clients compare it ordinally.</summary>
    public string Code { get; }

    /// <summary>The status a client receives; null for the log-only codes.</summary>
    public int? HttpStatus { get; }

    public bool IsClientVisible => HttpStatus is not null;

    /// <summary>The whole message for a fixed-body code, whose path is always the root; null when the failure's own text is kept.</summary>
    public string? FixedMessage { get; }

    public override string ToString() => Code;

    /// <summary>The declared code with exactly this spelling, or null.</summary>
    public static DataGatewayError? Find(string? code)
    {
        foreach (var error in All)
            if (string.Equals(error.Code, code, StringComparison.Ordinal))
                return error;
        return null;
    }

    /// <summary>The client status for a failure code: 400 for every library refusal (the library owns those names),
    /// the declared status for a public gateway code, null otherwise (ErrorEnvelope then answers InternalError).</summary>
    public static int? HttpStatusFor(string? code) =>
        code is not null && QueryAnswer.IsRefusal(code) ? 400 : Find(code)?.HttpStatus;
}
