using System.Runtime.CompilerServices;

namespace DataGateway.Core.Tests;

internal static class TestFiles
{
    internal static string RepoRoot => Path.GetFullPath(Path.Combine(SourceDirectory(), "../.."));

    internal static byte[] ReadFixture(string relativePath) =>
        File.ReadAllBytes(InRepo(Path.Combine("tests/fixtures", relativePath)));

    internal static string InRepo(string path)
    {
        var fullPath = Path.GetFullPath(Path.Combine(RepoRoot, path));
        var relative = Path.GetRelativePath(RepoRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Test input must be inside the repository: {path}", nameof(path));
        }

        return fullPath;
    }

    private static string SourceDirectory([CallerFilePath] string sourceFile = "") =>
        Path.GetDirectoryName(sourceFile)!;
}
