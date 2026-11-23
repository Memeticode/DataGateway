using DataGateway.Core.Queries;

namespace DataGateway.Core.Errors;

/// <summary>The fixed public gateway codes and what a client sees for each. Engines pick from
/// these; the private constructor means no other code can add one.</summary>
/// <remarks>Query library refusals (such as <c>UnknownColumn</c> or <c>InvalidJson</c>) are not declared here:
/// <see cref="HttpStatusFor"/> answers them 400. Any other undeclared code, such as <c>InvalidCatalog</c>, reaches a
/// client as <see cref="InternalError"/> (<see cref="ErrorEnvelope"/>).</remarks>
public sealed class DataGatewayError
{
    /// <summary>The fixed body of <see cref="UnexpectedError"/>.</summary>
    internal const string UnexpectedErrorMessage = "An unexpected error stopped DataGateway from completing the request. Tell the user, give the incidentId and stop; do not retry.";
    /// <summary>The fixed body of <see cref="InternalError"/>.</summary>
    internal const string InternalErrorMessage = "DataGateway could not complete the request because of a fault on its side, not in the request. Contact the DataGateway administrator. Tell the user, give the incidentId and stop; do not retry.";

    /// <summary>The requested source is not active in the catalog (404).</summary>
    public static DataGatewayError UnknownSource { get; } = new(nameof(UnknownSource), 404);
    /// <summary>A query ran past its source's deadline (504).</summary>
    public static DataGatewayError QueryTimeout { get; } = new(nameof(QueryTimeout), 504);
    /// <summary>The source database could not run the query (503).</summary>
    public static DataGatewayError SourceUnavailable { get; } = new(nameof(SourceUnavailable), 503);
    /// <summary>The catalog could not be read (503).</summary>
    public static DataGatewayError CatalogUnavailable { get; } = new(nameof(CatalogUnavailable), 503);
    /// <summary>The caller is not signed in, or sign-in was refused (401).</summary>
    public static DataGatewayError NotAuthenticated { get; } = new(nameof(NotAuthenticated), 401);
    /// <summary>A fault on the gateway's side, such as a code it does not declare (500, fixed body).</summary>
    public static DataGatewayError InternalError { get; } = new(nameof(InternalError), 500, InternalErrorMessage);
    /// <summary>An exception nothing expected (500, fixed body).</summary>
    public static DataGatewayError UnexpectedError { get; } = new(nameof(UnexpectedError), 500, UnexpectedErrorMessage);

    // Log lines only: a cancelled request gets no reply, so these never reach a client.
    /// <summary>The caller ended the request while its query ran; log only.</summary>
    public static DataGatewayError QueryCancelled { get; } = new(nameof(QueryCancelled), null);
    /// <summary>The caller ended the request while the catalog was read; log only.</summary>
    public static DataGatewayError CatalogCancelled { get; } = new(nameof(CatalogCancelled), null);

    /// <summary>Every declared code, client-visible and log-only.</summary>
    public static IReadOnlyList<DataGatewayError> All { get; } = Array.AsReadOnly(
    [
        UnknownSource, QueryTimeout, SourceUnavailable, CatalogUnavailable, NotAuthenticated,
        InternalError, UnexpectedError, QueryCancelled, CatalogCancelled
    ]);

    /// <summary>Declares one code.</summary>
    /// <param name="code">The wire text.</param>
    /// <param name="httpStatus">The client status; null for a log-only code.</param>
    /// <param name="fixedMessage">The whole message for a fixed-body code; null to keep the failure's own text.</param>
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

    /// <summary>Whether a client can ever receive this code.</summary>
    public bool IsClientVisible => HttpStatus is not null;

    /// <summary>The whole message for a fixed-body code, whose path is always the root; null when the failure's own text is kept.</summary>
    public string? FixedMessage { get; }

    /// <summary>The wire text.</summary>
    /// <returns><see cref="Code"/>.</returns>
    public override string ToString() => Code;

    /// <summary>The declared code with exactly this spelling, or null.</summary>
    /// <param name="code">The code to look up; null finds nothing.</param>
    /// <returns>The declared code, or null.</returns>
    public static DataGatewayError? Find(string? code)
    {
        foreach (var error in All)
            if (string.Equals(error.Code, code, StringComparison.Ordinal))
                return error;
        return null;
    }

    /// <summary>The client status for a failure code: 400 for every library refusal (the library owns those names),
    /// the declared status for a public gateway code, null otherwise (ErrorEnvelope then answers InternalError).</summary>
    /// <param name="code">The failure code; null finds nothing.</param>
    /// <returns>400, the declared status, or null.</returns>
    public static int? HttpStatusFor(string? code) =>
        code is not null && QueryAnswer.IsRefusal(code) ? 400 : Find(code)?.HttpStatus;
}
