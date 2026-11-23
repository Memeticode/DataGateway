using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DataGateway.Core.Tests;
using Xunit;

namespace DataGateway.Web.Tests;

/// <summary>The Windows sign-in sample (samples/WindowsAuthSqlite): anonymous callers are refused on every route,
/// Windows-signed-in callers are served, and its catalog and database files stay the README quickstart's.</summary>
public sealed class WindowsAuthSampleTests
{
    /// <remarks>The signed-in call sends the current user's own Windows credentials over a real Negotiate handshake,
    /// so it proves the sample's sign-in, not a stand-in for it.</remarks>
    [Fact]
    public async Task Anonymous_callers_are_challenged_and_windows_callers_are_served()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gateway-winauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "sample.db");
            using var seedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await WindowsAuthSample.SampleDatabase.CreateAsync(file, seedTimeout.Token);
            var app = WindowsAuthSample.SampleProgram.CreateApplication(["--environment", "Production",
                "--contentRoot", directory, "--urls", "http://127.0.0.1:0"], file);
            try
            {
                using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await app.StartAsync(startupTimeout.Token);
                var address = new Uri(Assert.Single(app.Urls));

                using var anonymous = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(20) };
                foreach (var route in new[] { "/sources/list", "/query", "/mcp" })
                {
                    using var refused = await anonymous.PostAsync(route, Json("{}"));
                    Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
                    Assert.Equal("Negotiate", Assert.Single(refused.Headers.WwwAuthenticate).Scheme);
                }

                using var signedIn = new HttpClient(new HttpClientHandler { UseDefaultCredentials = true })
                    { BaseAddress = address, Timeout = TimeSpan.FromSeconds(20) };
                using var list = await signedIn.PostAsync("/sources/list", Json("{}"));
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                Assert.Equal("""{"sources":[{"name":"sample_items","title":"Sample items","description":"Three invented items","grain":"One item","rowCap":2}]}""",
                    await list.Content.ReadAsStringAsync());
            }
            finally
            {
                try
                {
                    using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await app.StopAsync(shutdownTimeout.Token);
                }
                finally { await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)); }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <remarks>The README shows this code as the quickstart; comparing the files with its marked regions means the
    /// code a reader copies is the code this test runs.</remarks>
    [Theory]
    [InlineData("Catalog", "SampleCatalog.cs")]
    [InlineData("Database", "SampleDatabase.cs")]
    public void Catalog_and_database_files_are_the_readme_quickstart(string region, string file)
    {
        var readme = File.ReadAllText(TestFiles.InRepo("README.md"), Encoding.UTF8).Replace("\r", "");
        var block = Regex.Match(readme, $"<!-- sample region={region} -->\n```csharp\n(.*?)\n```", RegexOptions.Singleline);
        Assert.True(block.Success, "README has no sample region " + region);
        var expected = block.Groups[1].Value.Replace("namespace GatewaySample", "namespace WindowsAuthSample") + "\n";
        var actual = File.ReadAllText(TestFiles.InRepo("samples/WindowsAuthSqlite/" + file), Encoding.UTF8).Replace("\r", "");
        Assert.Equal(expected, actual);
    }

    /// <summary>A JSON request body.</summary>
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
