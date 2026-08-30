using System.Text.RegularExpressions;

namespace AnnasArchive.Tests.Architecture;

/// <summary>
/// Components render and decide; stores and API services fetch. Enforced for
/// every component in the app, not just Reader II's.
///
/// <para>Reader II has had this rule since it was built — <c>No_component_talks_to_the_network</c>
/// in <c>ArchitectureTests</c> — and it is why none of its components appear in
/// the allowlist. The rest of the app was never checked, and 38 of 95 components
/// inject <c>HttpClient</c> or an <c>*ApiService</c> directly. Those 38 are
/// recorded rather than fixed; what this stops is the 39th.</para>
///
/// <para><b>Why the detection ignores comments.</b> The Reader II rule is a plain
/// <c>Contains("HttpClient")</c>, which is fine inside a folder where the answer
/// is always zero. Run app-wide it reports 16 offenders, and 14 of them are doc
/// comments explaining the write-cancellation rule — sentences of the form "…
/// unsubscribing an HttpClient call aborts the request …". A rule with 14 false
/// positives in its first run is a rule that gets an exemption bolted on and then
/// ignored, so this one strips comments and looks for an actual injection.</para>
/// </summary>
public class ComponentNetworkRatchetTests
{
    private const string FixtureName = "component-network-allowlist.txt";

    private static string FixturePath => RepoLayout.FixturePath(FixtureName);

    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline);
    private static readonly Regex LineComment = new("//[^\n]*");

    /// <summary><c>inject(SomethingApiService)</c>, the Angular 19 idiom used here.</summary>
    private static readonly Regex Injected = new(@"inject\(\s*(?:HttpClient|\w*ApiService)\s*\)");

    /// <summary>A constructor parameter or field typed as one, for the older components.</summary>
    private static readonly Regex TypedAs = new(@":\s*(?:HttpClient|\w*ApiService)\b");

    private static readonly Regex RawHttp = new(@"\bHttpClient\b");

    private static string CodeOnly(string source) =>
        LineComment.Replace(BlockComment.Replace(source, ""), "");

    /// <summary>Every component, with whether it reaches the network itself.</summary>
    private static readonly Lazy<IReadOnlyList<(string Path, bool Fetches, bool Raw)>> Scanned = new(() =>
        RepoLayout.ComponentFiles()
            .Select(f =>
            {
                var code = CodeOnly(File.ReadAllText(f));
                return (Path: RepoLayout.Relative(f),
                        Fetches: Injected.IsMatch(code) || TypedAs.IsMatch(code),
                        Raw: RawHttp.IsMatch(code));
            })
            .OrderBy(c => c.Path, StringComparer.Ordinal)
            .ToArray());

    private static readonly Lazy<IReadOnlySet<string>> Allowed = new(() =>
        RepoLayout.ReadAllowlist(FixtureName).Select(e => e[0]).ToHashSet());

    /// <summary>
    /// A rule that scans nothing passes forever, and one whose detection has
    /// silently stopped matching is worse — it reads as "no component fetches",
    /// which would be excellent news rather than a broken regex.
    /// </summary>
    [Fact]
    public void The_component_network_rule_is_scanning_real_files()
    {
        Scanned.Value.Should().HaveCountGreaterThan(50, "otherwise every rule below is vacuous");

        Scanned.Value.Where(c => c.Fetches).Should().NotBeEmpty(
            "38 components fetch today; finding none means the detection broke, not that "
            + "the app was fixed");

        Allowed.Value.Should().NotBeEmpty("an empty allowlist means the fixture failed to load");
    }

    /// <summary>
    /// The rule proper, and the only half that has to hold: the count cannot grow.
    /// </summary>
    [Fact]
    public void No_unlisted_component_talks_to_the_network()
    {
        var offenders = Scanned.Value
            .Where(c => c.Fetches && !Allowed.Value.Contains(c.Path))
            .Select(c => c.Path + (c.Raw ? " (raw HttpClient)" : ""))
            .ToList();

        offenders.Should().BeEmpty(
            "a component fetches its own data. Move the call into a store or an API service "
            + "and inject that instead — the component should receive state, not go and get "
            + $"it. If this is genuinely the right shape, add it to {FixturePath} and say why");
    }

    /// <summary>
    /// Rot prevention, and the reason this list can only shrink. When a component
    /// stops fetching, its line must go — otherwise the allowlist slowly becomes a
    /// list of components that used to have a problem, and nobody can tell how much
    /// of it is still real.
    /// </summary>
    [Fact]
    public void No_component_network_allowlist_entry_is_stale()
    {
        var live = Scanned.Value.ToDictionary(c => c.Path, c => c);

        var gone = Allowed.Value
            .Where(p => !live.ContainsKey(p))
            .Select(p => $"{p} (no such component — deleted or renamed?)");

        var fixedUp = Allowed.Value
            .Where(p => live.TryGetValue(p, out var c) && !c.Fetches)
            .Select(p => $"{p} (no longer fetches — nice; delete the line)");

        gone.Concat(fixedUp).ToList().Should().BeEmpty(
            $"delete these lines from {FixturePath}");
    }

    /// <summary>
    /// The <c>[raw]</c> annotations must stay honest, because they are what says
    /// which of the 38 to move first. A component that owns a URL, its headers and
    /// its error handling is a worse violation than one calling a typed service,
    /// and an unmaintained marker would hide that ranking.
    /// </summary>
    [Fact]
    public void The_raw_http_markers_match_reality()
    {
        var marked = RepoLayout.ReadAllowlist(FixtureName)
            .Where(e => e.Length > 1 && e[1] == "[raw]")
            .Select(e => e[0])
            .ToHashSet();

        var actuallyRaw = Scanned.Value.Where(c => c.Fetches && c.Raw).Select(c => c.Path).ToHashSet();

        marked.Should().BeEquivalentTo(actuallyRaw,
            "either a component stopped injecting HttpClient and its [raw] marker is stale, "
            + "or one started and was never marked");
    }
}
