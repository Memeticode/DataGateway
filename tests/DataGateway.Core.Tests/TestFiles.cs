namespace DataGateway.Core.Tests;

internal static class TestFiles
{
    // Found from the test binaries, not from source paths: a CI build maps source paths to /_/ for reproducible packages.
    internal static string RepoRoot { get; } = FindRepoRoot();

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

    private static string FindRepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "DataGateway.slnx")))
                return folder.FullName;
        throw new InvalidOperationException($"No DataGateway.slnx above the test binaries in {AppContext.BaseDirectory}.");
    }
}
