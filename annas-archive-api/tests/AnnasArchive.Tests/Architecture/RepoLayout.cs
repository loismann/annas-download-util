namespace AnnasArchive.Tests.Architecture;

/// <summary>
/// Where the source lives, for rules that read the repository rather than the
/// compiled assembly.
///
/// <para>Shared by the ratchet tests so the root walk and the line-counting rule
/// are defined once. Both were duplicated the moment there was a second rule,
/// which is the point at which duplication stops being cheaper than the
/// abstraction.</para>
/// </summary>
internal static class RepoLayout
{
    /// <summary>
    /// The repository root: the directory that holds the Angular app. Found by
    /// walking up from the test binary, the same way the Reader II rules locate
    /// sources.
    /// </summary>
    public static string Root => RootValue.Value;

    private static readonly Lazy<string> RootValue = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "annas-archive-app")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new DirectoryNotFoundException(
                "could not find the repository root above " + AppContext.BaseDirectory);
    });

    /// <summary>Forward-slashed and relative to the root, so it matches a fixture line.</summary>
    public static string Relative(string absolutePath) =>
        Path.GetRelativePath(Root, absolutePath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Every backend source file, excluding build output.</summary>
    public static IEnumerable<string> BackendFiles() =>
        Directory.GetFiles(Path.Combine(Root, "annas-archive-api", "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>Every Angular component, excluding specs.</summary>
    public static IEnumerable<string> ComponentFiles() =>
        Directory.GetFiles(Path.Combine(Root, "annas-archive-app", "src", "app"), "*.component.ts",
                SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".spec.ts", StringComparison.Ordinal));

    /// <summary>
    /// Counts newlines rather than calling <c>ReadAllLines</c>, so the number is
    /// identical to what <c>wc -l</c> reports. The regeneration command in the
    /// allowlist header uses <c>wc -l</c>; if the two disagreed by one on files
    /// without a trailing newline, every regeneration would produce a spurious
    /// diff.
    /// </summary>
    public static int LineCount(string absolutePath) =>
        File.ReadAllText(absolutePath).Count(c => c == '\n');

    /// <summary>
    /// Reads an allowlist fixture: non-empty, non-<c>#</c> lines, tab-split.
    /// </summary>
    public static IReadOnlyList<string[]> ReadAllowlist(string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        if (!File.Exists(path))
            throw new FileNotFoundException("the allowlist must ship with the tests", path);

        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t').Select(p => p.Trim()).ToArray())
            .ToArray();
    }

    /// <summary>The repo-relative path of a fixture, for a failure message.</summary>
    public static string FixturePath(string fixtureName) =>
        "annas-archive-api/tests/AnnasArchive.Tests/Fixtures/" + fixtureName;
}
