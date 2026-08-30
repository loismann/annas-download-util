namespace AnnasArchive.Tests.Architecture;

/// <summary>
/// Files may not grow. The rule that used to protect <c>Reader2/</c> alone now
/// covers both stacks.
///
/// <para>Reader II is the best-structured code in the repo, and the reason is not
/// discipline — it is that a test failed when a file crossed 300 lines. That rule
/// was scoped to <c>src/AnnasArchive.API/Reader2/</c>, which left roughly three
/// quarters of the codebase unguarded, and the unguarded three quarters is where
/// all 77 oversized files are. Widening the countermeasure to every line it
/// applies to is the whole point of this file.</para>
///
/// <para><b>Why an allowlist rather than a deadline.</b> Nothing has to be fixed
/// to make this pass today. The 77 files that are already over their limit are
/// recorded with their exact current size, and the only thing forbidden is
/// getting worse. Improvements edit a number downward where a reviewer sees it.
/// That is the standard baseline-file pattern — RuboCop's <c>.rubocop_todo.yml</c>,
/// PHPStan's baseline — and it is the only version of this rule that could be
/// switched on in one commit.</para>
///
/// <para><b>Size is a tripwire, not a verdict.</b> Crossing the limit means
/// someone should look, not that the file is wrong.
/// <c>ASSERTIONS_AND_ASSUMPTIONS.md</c> §1.4 found that size ranks almost exactly
/// opposite to movability, and that four structural passes over the largest file
/// in the old reader found none of its defects. So this test never demands a
/// split. It demands that growth be deliberate.</para>
/// </summary>
public class FileSizeRatchetTests
{
    private const int BackendLimit = 300;
    private const int FrontendLimit = 200;

    private static int LimitFor(string path) =>
        path.EndsWith(".cs", StringComparison.Ordinal) ? BackendLimit : FrontendLimit;

    /// <summary>Every file the size rules apply to, as repo-relative paths.</summary>
    private static readonly Lazy<IReadOnlyList<(string Path, int Lines, int Limit)>> Scanned = new(() =>
        RepoLayout.BackendFiles().Concat(RepoLayout.ComponentFiles())
            .Select(f =>
            {
                var rel = RepoLayout.Relative(f);
                return (Path: rel, Lines: RepoLayout.LineCount(f), Limit: LimitFor(rel));
            })
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToArray());

    private const string FixtureName = "file-size-allowlist.txt";

    /// <summary>The recorded size of each file that is allowed to be over its limit.</summary>
    private static readonly Lazy<IReadOnlyDictionary<string, int>> Allowed = new(() =>
        RepoLayout.ReadAllowlist(FixtureName).ToDictionary(e => e[1], e => int.Parse(e[0])));

    /// <summary>Where to edit when one of these fails.</summary>
    private static string FixturePath => RepoLayout.FixturePath(FixtureName);

    /// <summary>
    /// A rule that scans nothing passes forever. This is the guard on the guard —
    /// the same reason <c>The_frontend_rules_are_checking_real_files</c> exists.
    /// </summary>
    [Fact]
    public void The_size_rules_are_scanning_both_stacks()
    {
        Scanned.Value.Should().HaveCountGreaterThan(200, "otherwise every rule below is vacuous");

        Scanned.Value.Select(f => f.Limit).Distinct()
            .Should().BeEquivalentTo(new[] { BackendLimit, FrontendLimit },
                "both stacks must be in scope; one missing makes half these rules silent");

        Allowed.Value.Should().NotBeEmpty("an empty allowlist means the fixture failed to load");
    }

    /// <summary>
    /// The rule proper. A new file, or a file that was under its limit yesterday,
    /// may not cross it. This is the half that keeps the problem from growing.
    /// </summary>
    [Fact]
    public void No_unlisted_file_crosses_its_size_limit()
    {
        var offenders = Scanned.Value
            .Where(f => f.Lines > f.Limit && !Allowed.Value.ContainsKey(f.Path))
            .Select(f => $"{f.Path} ({f.Lines} > {f.Limit})")
            .ToList();

        offenders.Should().BeEmpty(
            "a file crossed its size limit for the first time. Either bring it back under, "
            + $"or — if the size is genuinely right — add it to {FixturePath} and say why in "
            + "the commit message. Adding a line is allowed; doing it silently is not");
    }

    /// <summary>
    /// The ratchet's teeth. An allowlisted file is pinned at the size it was when
    /// it was listed, so the 77 exceptions can never quietly become 77 larger
    /// exceptions.
    /// </summary>
    [Fact]
    public void No_allowlisted_file_has_grown()
    {
        var grown = Scanned.Value
            .Where(f => Allowed.Value.TryGetValue(f.Path, out var was) && f.Lines > was)
            .Select(f => $"{f.Path}: {Allowed.Value[f.Path]} -> {f.Lines} (+{f.Lines - Allowed.Value[f.Path]})")
            .ToList();

        grown.Should().BeEmpty(
            "these files are already over the limit and were allowed to stay that way at "
            + "their recorded size, not to keep growing. Put the new code somewhere else, or "
            + $"take the same number of lines back out. Raising the number in {FixturePath} "
            + "defeats the only rule protecting these files");
    }

    /// <summary>
    /// The other direction, and the reason this is a ratchet rather than a ceiling.
    /// A file that shrank must have its new size recorded, so the allowlist tracks
    /// reality instead of a high-water mark nobody has revisited — and so the win
    /// lands in the diff.
    /// </summary>
    [Fact]
    public void Every_allowlist_entry_records_its_files_current_size()
    {
        var stale = Scanned.Value
            .Where(f => Allowed.Value.TryGetValue(f.Path, out var was) && f.Lines < was && f.Lines > f.Limit)
            .Select(f => $"{f.Path}: recorded {Allowed.Value[f.Path]}, now {f.Lines}")
            .ToList();

        stale.Should().BeEmpty(
            $"you made these smaller — record it in {FixturePath}. Leaving the old number "
            + "would let the file grow back for free, which is exactly the drift this rule "
            + "exists to stop");
    }

    /// <summary>
    /// Rot prevention. An entry for a file that was deleted or renamed exempts
    /// nothing while looking like it exempts something, and an entry for a file
    /// that is now under its limit is a graduation nobody recorded.
    /// </summary>
    [Fact]
    public void No_allowlist_entry_is_stale()
    {
        var live = Scanned.Value.ToDictionary(f => f.Path, f => f);

        var gone = Allowed.Value.Keys
            .Where(p => !live.ContainsKey(p))
            .Select(p => $"{p} (no such file — deleted or renamed?)");

        var graduated = Allowed.Value.Keys
            .Where(p => live.TryGetValue(p, out var f) && f.Lines <= f.Limit)
            .Select(p => $"{p} (now {live[p].Lines} lines, under its {live[p].Limit} limit)");

        gone.Concat(graduated).ToList().Should().BeEmpty(
            $"delete these lines from {FixturePath}. An exemption that no longer names an "
            + "oversized file is dead weight, and it hides whether the rule is still doing "
            + "anything");
    }
}
