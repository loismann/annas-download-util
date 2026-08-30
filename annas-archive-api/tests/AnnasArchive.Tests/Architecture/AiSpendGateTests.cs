using System.Text.RegularExpressions;

namespace AnnasArchive.Tests.Architecture;

/// <summary>
/// Every call that spends a person's AI allowance has to be behind the check that
/// enforces it.
///
/// <para><b>Why this is a rule and not a code review.</b> The monthly allowance is
/// applied entirely by hand: each entry point calls
/// <c>TokenLimitHelpers.CheckTokenLimit</c> itself, and nothing anywhere makes that
/// mandatory — no middleware, no filter, no base class. Six entry points remember
/// and one forgot, which is the predictable outcome of a convention that only lives
/// in the heads of the people who happened to write the last one. Adding a
/// seventh AI feature is one omission away from an uncapped bill.</para>
///
/// <para><b>Three ways a file can be legitimate.</b> It gates its own spend; it only
/// ever bills <see cref="AnnasArchive.API.Services.Ai.AiSpend.BackgroundAccount"/>,
/// which is household money with no per-user allowance to check; or it is on the
/// declaration list, which names the entry point that gates it.</para>
///
/// <para>Reads the source rather than the assembly, like the other ratchets — the
/// question is about which call sites exist, not what happens at runtime.</para>
/// </summary>
public class AiSpendGateTests
{
    private const string Fixture = "ungated-ai-spend.txt";

    /// <summary>The two wrappers every OpenAI call in this app goes through, plus the
    /// reader's own. A file that names one of these can reach a paid API.</summary>
    private static readonly Regex ReachesTheModel =
        new(@"\b(AiChatCompletion|AiResponsesCompletion|IAiChatCompletion|IAiResponsesCompletion)\b");

    /// <summary>The call that enforces the allowance.</summary>
    private static readonly Regex Gates = new(@"\bCheckTokenLimit\b");

    /// <summary>Billing that names the background account rather than a person.</summary>
    private static readonly Regex BillsBackground = new(@"\bBackgroundAccount\b");

    /// <summary>
    /// Resolving <i>who</i> is spending. This is the signal, and picking it correctly
    /// took a wrong turn worth recording: matching <c>billTo</c> as well flagged five
    /// files that merely <b>relay</b> that parameter down to the wrapper —
    /// <c>DescriptionFetcherService</c>, <c>RelatedBooksEnricher</c> and friends. They
    /// cannot check an allowance, because they are never told whose it is. The file
    /// that resolves the identity is the file that can, and must, check it.
    /// </summary>
    private static readonly Regex BillsAPerson =
        new(@"\b(GetRequiredOwnerKey|GetUserIdFromContext)\b");

    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline);
    private static readonly Regex LineComment = new("//[^\n]*");

    /// <summary>Comments describe the rule constantly; only code can satisfy it.</summary>
    private static string CodeOnly(string source) =>
        LineComment.Replace(BlockComment.Replace(source, ""), "");

    private sealed record Site(string Path, bool Gates, bool BillsPerson, bool BillsBackground);

    private static readonly Lazy<IReadOnlyList<Site>> Sites = new(() =>
        RepoLayout.BackendFiles()
            .Select(f => (Path: f, Code: CodeOnly(File.ReadAllText(f))))
            // Plumbing, not call sites: TokenLimitHelpers defines the gate, AiSpend
            // defines the billing, and Services/Ai holds the wrappers every caller goes
            // through. One of those wrappers offers an overload that resolves the
            // identity for its caller, which makes it look like an origination point;
            // it is the mechanism, and gating inside it would gate the background jobs
            // that deliberately have no allowance.
            .Where(f => !f.Path.EndsWith("TokenLimitHelpers.cs", StringComparison.Ordinal)
                     && !f.Path.Contains($"{Path.DirectorySeparatorChar}Services{Path.DirectorySeparatorChar}Ai{Path.DirectorySeparatorChar}",
                                          StringComparison.Ordinal))
            .Where(f => ReachesTheModel.IsMatch(f.Code))
            .Select(f => new Site(
                RepoLayout.Relative(f.Path),
                Gates.IsMatch(f.Code),
                BillsAPerson.IsMatch(f.Code),
                BillsBackground.IsMatch(f.Code)))
            .OrderBy(s => s.Path, StringComparer.Ordinal)
            .ToArray());

    private sealed record Declared(string Status, string EntryPoint);

    private static readonly Lazy<IReadOnlyDictionary<string, Declared>> Declarations = new(() =>
        RepoLayout.ReadAllowlist(Fixture).ToDictionary(
            p => p[0],
            p => new Declared(
                p.Length > 1 ? p[1] : "",
                p.Length > 2 ? p[2] : "")));

    /// <summary>
    /// The rule is worthless if it is scanning nothing. Guards against a rename or a
    /// moved folder turning every assertion below into a vacuous pass.
    /// </summary>
    [Fact]
    public void The_rule_is_finding_the_AI_call_sites()
    {
        Sites.Value.Should().HaveCountGreaterThan(5,
            "this app calls OpenAI from several services and endpoints; finding almost none "
            + "means the detection broke, not that the spending stopped");

        Sites.Value.Should().Contain(s => s.Gates,
            "at least the AI search endpoints enforce the allowance");
    }

    /// <summary>
    /// The rule itself. A file that can reach a paid model, bills a real person for
    /// it, and never checks that person's allowance is a place where somebody over
    /// their cap keeps spending.
    /// </summary>
    [Fact]
    public void No_new_call_site_spends_a_persons_allowance_without_checking_it()
    {
        var offenders = Sites.Value
            .Where(s => s.BillsPerson && !s.Gates && !Declarations.Value.ContainsKey(s.Path))
            .Select(s => s.Path)
            .ToList();

        offenders.Should().BeEmpty(
            $"these bill a person for AI without enforcing their monthly allowance. Either call "
            + $"TokenLimitHelpers.CheckTokenLimit at the entry point, bill "
            + $"AiSpend.BackgroundAccount if the work is the household's rather than one "
            + $"person's, or — only if the spend is genuinely meant to be uncapped — add the "
            + $"file to {RepoLayout.FixturePath(Fixture)} with the route that should gate it:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A file that has taken on the check itself needs no declaration at all — its
    /// line should go, whichever status it carried.
    /// </summary>
    [Fact]
    public void No_declaration_survives_its_file_taking_on_the_check()
    {
        var selfGating = Declarations.Value.Keys
            .Where(p => Sites.Value.Any(s => s.Path == p && s.Gates))
            .ToList();

        selfGating.Should().BeEmpty(
            $"these now call CheckTokenLimit themselves, so their lines in "
            + $"{RepoLayout.FixturePath(Fixture)} describe nothing and should be deleted:\n  "
            + string.Join("\n  ", selfGating));
    }

    /// <summary>
    /// <b>A <c>gated-upstream</c> claim is verified, not taken on trust.</b>
    ///
    /// <para>This test exists because the first version of the rule did take it on
    /// trust, and deleting the gate from the entry point failed nothing at all: the
    /// endpoint file never names an AI wrapper, so it is not a call site, and the
    /// service it calls was exempt by declaration. The exemption would have outlived
    /// the thing it was describing — which is the failure mode every allowlist in this
    /// repo is supposed to avoid.</para>
    ///
    /// <para>So the route named in the third column is looked up: whichever file
    /// registers it must call the check.</para>
    /// </summary>
    [Fact]
    public void Every_gated_upstream_claim_is_backed_by_an_entry_point_that_really_gates()
    {
        var unbacked = new List<string>();

        foreach (var (path, declared) in Declarations.Value.Where(d => d.Value.Status == "gated-upstream"))
        {
            // "POST /api/spotify/command" -> "/api/spotify/command"
            var route = declared.EntryPoint.Split(' ').Last();

            var registrars = RepoLayout.BackendFiles()
                .Select(f => (Path: f, Code: CodeOnly(File.ReadAllText(f))))
                .Where(f => f.Code.Contains($"\"{route}\"", StringComparison.Ordinal)
                         && f.Code.Contains("Map", StringComparison.Ordinal))
                .ToList();

            if (registrars.Count == 0)
                unbacked.Add($"{path}: nothing registers {route}");
            else if (!registrars.Any(f => Gates.IsMatch(f.Code)))
                unbacked.Add($"{path}: {route} is registered but its file does not call CheckTokenLimit");
        }

        unbacked.Should().BeEmpty(
            $"a 'gated-upstream' line in {RepoLayout.FixturePath(Fixture)} claims the named "
            + "route enforces the allowance. If that stopped being true, the claim is now "
            + "exempting a real defect:\n  " + string.Join("\n  ", unbacked));
    }

    /// <summary>
    /// Only two statuses mean anything, and a typo in that column would silently
    /// widen the exemption to cover a real defect.
    /// </summary>
    [Fact]
    public void Every_declaration_carries_a_status_the_rule_understands()
    {
        Declarations.Value
            .Where(d => d.Value.Status is not ("gated-upstream" or "ungated"))
            .Select(d => $"{d.Key} ({d.Value.Status})")
            .Should().BeEmpty("the status column is 'gated-upstream' or 'ungated'");
    }

    /// <summary>
    /// The number that matters. <c>ungated</c> lines are routes where somebody past
    /// their cap keeps spending; the count may fall and may not rise. It is zero
    /// today — <c>POST /api/spotify/command</c> was the last one, and gating it is
    /// what moved its service to <c>gated-upstream</c>.
    /// </summary>
    [Fact]
    public void The_number_of_ungated_money_routes_has_not_risen()
    {
        Declarations.Value
            .Where(d => d.Value.Status == "ungated")
            .Select(d => $"{d.Key} → {d.Value.EntryPoint}")
            .Should().BeEmpty(
                "every AI call site that bills a person is now behind the allowance check. "
                + "A new line here is a route where somebody over their monthly cap can keep "
                + "spending, so it needs a decision rather than a fixture edit");
    }

    /// <summary>
    /// And may not name a file that no longer reaches a model at all — a stale entry
    /// silently exempts whatever later takes that path.
    /// </summary>
    [Fact]
    public void No_entry_on_the_defect_list_is_stale()
    {
        var stale = Declarations.Value.Keys
            .Where(p => Sites.Value.All(s => s.Path != p))
            .ToList();

        stale.Should().BeEmpty(
            $"these no longer reach a paid model, so their lines in "
            + $"{RepoLayout.FixturePath(Fixture)} exempt nothing and should go:\n  "
            + string.Join("\n  ", stale));
    }

    /// <summary>
    /// Every entry names the route that should gate it. Without that, the list is a
    /// set of paths nobody can act on — the person who eventually fixes one needs to
    /// know where the entry point is, and the person adding a line is made to work
    /// out whether there even is one.
    /// </summary>
    [Fact]
    public void Every_entry_on_the_defect_list_names_the_route_that_should_gate_it()
    {
        Declarations.Value.Where(e => string.IsNullOrWhiteSpace(e.Value.EntryPoint)).Select(e => e.Key)
            .Should().BeEmpty("each line is '<path><TAB><status><TAB><entry point>'");
    }
}
