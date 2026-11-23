using System.IO.Compression;
using Scalar.AspNetCore;
using Xunit.Sdk;

namespace DataGateway.Web.Tests;

/// <summary>Reads the API page's static files straight from the Scalar package, so tests can compare what the
/// gateway serves with what the package ships.</summary>
internal static class PageAssets
{
    /// <summary>The bytes of one packaged Scalar asset, decompressed when the package stores it gzipped.</summary>
    /// <remarks>Tests compare against the bytes a browser receives, so a gzip header is undone here once rather than
    /// in every caller.</remarks>
    /// <param name="fileName">The asset's file name, as it appears at the end of its URL.</param>
    /// <returns>The asset's uncompressed bytes.</returns>
    internal static byte[] Packaged(string fileName)
    {
        var assembly = typeof(ScalarOptions).Assembly;
        using var resource = assembly.GetManifestResourceStream("ScalarStaticAssets." + fileName)
            ?? throw new XunitException("Missing package resource: " + fileName);
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        var value = bytes.ToArray();
        if (value.Length < 2 || value[0] != 0x1f || value[1] != 0x8b) return value;
        using var input = new MemoryStream(value);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
