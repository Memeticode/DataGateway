using System.Text.Json;
using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using DataGateway.Core.Queries;
using SqlKata.JsonQuery;
using Xunit;

namespace DataGateway.Core.Tests;

public sealed class ErrorEnvelopeTests
{
    private const string InternalMessage = "DataGateway could not complete the request because of a fault on its side, not in the request. Contact the DataGateway administrator. Tell the user. Do not retry.";

    [Fact]
    public void Unexpected_T15_code_always_has_the_fixed_500_body()
    {
        const string message = "An unexpected error stopped DataGateway from completing the request. Report the incidentId to the DataGateway administrator. Tell the user. Do not retry.";
        var expected = new ErrorReply(500, new("UnexpectedError", "$", message, "abc"));
        Assert.Equal(expected, ErrorEnvelope.From(ErrorEnvelope.Unexpected, "abc"));
        Assert.Equal(expected, ErrorEnvelope.From(new("UnexpectedError", "$.query", "Server=x leaked"), "abc"));
        Assert.Equal(message, ErrorEnvelope.UnexpectedErrorMessage);
    }

    [Fact]
    public void T3_public_codes_preserve_the_entire_failure_text()
    {
        var publicCodes = Enum.GetNames<JsonQueryErrorCode>().Select(c => (Code: c, Status: 400))
            .Concat(new[] { ("UnknownSource", 404), ("NotAuthenticated", 401), ("CatalogUnavailable", 503),
                ("SourceUnavailable", 503), ("QueryTimeout", 504) });
        var message = string.Concat(Enumerable.Repeat("Long public fix-it text. ", 100));
        foreach (var (code, status) in publicCodes)
        foreach (var path in new[] { "$", "$.source", "$.query.filters[1].col" })
            Assert.Equal(new ErrorReply(status, new(code, path, message, "abc")),
                ErrorEnvelope.From(new(code, path, message), "abc"));
    }

    [Theory]
    [InlineData("InvalidCatalog")]
    [InlineData("CatalogConfiguration")]
    [InlineData("CatalogCancelled")]
    [InlineData("QueryCancelled")]
    [InlineData("InternalError")]
    [InlineData("SomethingNew")]
    [InlineData("invalidjson")]
    [InlineData("")]
    public void T4_internal_and_unknown_codes_are_redacted(string code)
    {
        var reply = ErrorEnvelope.From(new(code, "$.source", "hidden_schema.secret_view Server=x;Password=y")
            { ExceptionType = "SqlException", SqlNumber = 208, Step = "seed" }, "abc");
        Assert.Equal(new ErrorReply(500, new("InternalError", "$", InternalMessage, "abc")), reply);
        Assert.Equal(InternalMessage, ErrorEnvelope.InternalErrorMessage);
        var json = JsonSerializer.Serialize(reply.Body, JsonSerializerOptions.Web);
        foreach (var hidden in new[] { "hidden_schema", "secret_view", "Server=x", "Password=y", "SqlException", "208", "seed", "$.source" })
            Assert.DoesNotContain(hidden, json, StringComparison.Ordinal);
        if (code.Length > 0 && code != "InternalError") Assert.DoesNotContain(code, json, StringComparison.Ordinal);
    }

    [Fact]
    public void T5_body_has_exact_wire_shape_and_order()
    {
        Assert.Equal("{\"code\":\"UnknownSource\",\"path\":\"$.source\",\"message\":\"Choose a source.\"}",
            JsonSerializer.Serialize(new ErrorBody("UnknownSource", "$.source", "Choose a source.", null), JsonSerializerOptions.Web));
        Assert.Equal(new[] { "Code", "Path", "Message", "IncidentId" }, typeof(ErrorBody).GetProperties().Select(p => p.Name));
        var signed = ErrorEnvelope.From(new("NotAuthenticated", "$", "Sign in."), "abc");
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(signed.Body, JsonSerializerOptions.Web));
        Assert.Equal(new[] { "code", "path", "message", "incidentId" }, body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("{\"code\":\"NotAuthenticated\",\"path\":\"$\",\"message\":\"Sign in.\"}",
            JsonSerializer.Serialize(ErrorEnvelope.From(new("NotAuthenticated", "$", "Sign in."), null).Body, JsonSerializerOptions.Web));
        Assert.Equal("failure", Assert.Throws<ArgumentNullException>(() => ErrorEnvelope.From(null!, null)).ParamName);
    }

    [Fact]
    public void T6_library_vocabulary_has_the_expected_codes_and_specific_wording()
    {
        string[] expected = ["InvalidJson", "InvalidStructure", "UnknownColumn", "ColumnOperationNotAllowed",
            "InvalidOperator", "InvalidAggregateFunction", "InvalidSortDirection", "InvalidValue", "InvalidClauseCombination",
            "InvalidOrderTarget", "DuplicateReference", "InvalidLimit", "StructuralLimitExceeded"];
        Assert.Equal(expected.Order(StringComparer.Ordinal), Enum.GetNames<JsonQueryErrorCode>().Order(StringComparer.Ordinal));
        foreach (var code in Enum.GetValues<JsonQueryErrorCode>())
            Assert.NotEqual("The query was refused at path. Correct it against querySchema from describe source.",
                RefusalMessage.For(code, "select[0]", null));
    }
}
