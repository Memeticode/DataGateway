using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace DataGateway.Web.Tests;

// Derived only from the frozen Example buffers: one title each, three operation tags, one top-level tags list.
// Measured byte equality in HostCompositionTests.T8 on two ephemeral ports; ApplicationName is DataGateway.Web.
// web-document: 37826 bytes; SHA256 6382801c27aaa5b8e242f767d0439e8f81e3bf10a4c33d2685fa16867cf8cdd7
// web-html: 686 bytes; SHA256 a6ac864fd6c19890c210648b6efce36d8682cca12da2d24de6cccae60775a382
/// <summary>The API page the web host (DataGateway.Web) must serve, derived from the frozen example page in
/// <see cref="ExampleApiPageGolden"/>.</summary>
/// <remarks>The two hosts' pages differ only by name: the title and the operation tags. Deriving one from the other
/// by checked substitutions keeps a single frozen capture, and the size and hash checks catch a substitution that
/// went wrong.</remarks>
internal static class WebApiPageGolden
{
    /// <summary>The expected OpenAPI document bytes for the web host.</summary>
    internal static byte[] Document()
    {
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(ExampleApiPageGolden.DocumentBase64));
        text = Once(text, "\"title\": \"DataGateway\"", "\"title\": \"DataGateway.Web\"");
        foreach (var tag in new[] { "SourcesListRoute", "SourcesDescribeRoute", "QueryRoute" })
            text = Once(text, "\"tags\": [\n          \"" + tag + "\"\n        ]", "\"tags\": [\n          \"DataGateway.Web\"\n        ]");
        text = Once(text, "  \"tags\": [\n    {\n      \"name\": \"SourcesListRoute\"\n    },\n    {\n      \"name\": \"SourcesDescribeRoute\"\n    },\n    {\n      \"name\": \"QueryRoute\"\n    }\n  ]", "  \"tags\": [\n    {\n      \"name\": \"DataGateway.Web\"\n    }\n  ]");
        return Checked(text, 37826, "6382801c27aaa5b8e242f767d0439e8f81e3bf10a4c33d2685fa16867cf8cdd7");
    }
    /// <summary>The expected HTML page bytes for the web host.</summary>
    internal static byte[] Html()
    {
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(ExampleApiPageGolden.HtmlBase64));
        text = Once(text, "<title>DataGateway</title>", "<title>DataGateway.Web</title>");
        return Checked(text, 686, "a6ac864fd6c19890c210648b6efce36d8682cca12da2d24de6cccae60775a382");
    }
    /// <summary>Replaces <paramref name="old"/>, asserting it occurs exactly once.</summary>
    private static string Once(string text, string old, string replacement)
    {
        var first = text.IndexOf(old, StringComparison.Ordinal);
        Assert.True(first >= 0);
        Assert.Equal(-1, text.IndexOf(old, first + old.Length, StringComparison.Ordinal));
        return text.Replace(old, replacement, StringComparison.Ordinal);
    }
    /// <summary>Encodes <paramref name="text"/> as UTF-8 and asserts its length and SHA-256.</summary>
    private static byte[] Checked(string text, int length, string hash)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Assert.Equal(length, bytes.Length);
        Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return bytes;
    }
}
