using System.IO.Compression;
using Scalar.AspNetCore;
using Xunit.Sdk;

namespace DataGateway.Web.Tests;

internal static class PageAssets
{
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
