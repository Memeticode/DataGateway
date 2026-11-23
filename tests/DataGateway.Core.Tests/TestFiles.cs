namespace DataGateway.Core.Tests;

/// <summary>Finds the repository and the files tests read from it: fixtures, SQL scripts and product sources.</summary>
/// <remarks>Shared by the Core and Web test projects (the Web project links this file), so both resolve paths the
/// same way.</remarks>
internal static class TestFiles
{
    /// <summary>The repository's source root: the folder that holds <c>DataGateway.slnx</c>.</summary>
    /// <remarks>Found from the test binaries, not from source paths: a CI build maps source paths to /_/ for
    /// reproducible packages.</remarks>
    internal static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Reads a test fixture under <c>tests/fixtures</c> as raw bytes, so a golden comparison sees the exact
    /// file, line endings included.</summary>
    /// <param name="relativePath">The fixture's path below <c>tests/fixtures</c>.</param>
    internal static byte[] ReadFixture(string relativePath) =>
        File.ReadAllBytes(InRepo(Path.Combine("tests/fixtures", relativePath)));

    /// <summary>Resolves a path relative to <see cref="RepoRoot"/> and refuses one that leaves the repository.</summary>
    /// <remarks>A test that reads <c>../</c> by mistake would pass or fail on whatever sits beside the checkout; failing
    /// loudly keeps every test input inside the repository it checks.</remarks>
    /// <param name="path">A path relative to the repository's source root.</param>
    /// <returns>The full path.</returns>
    /// <exception cref="ArgumentException">The path resolves outside the repository.</exception>
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

    /// <summary>Walks up from the test binaries to the first folder holding <c>DataGateway.slnx</c>.</summary>
    private static string FindRepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "DataGateway.slnx")))
                return folder.FullName;
        throw new InvalidOperationException($"No DataGateway.slnx above the test binaries in {AppContext.BaseDirectory}.");
    }
}
