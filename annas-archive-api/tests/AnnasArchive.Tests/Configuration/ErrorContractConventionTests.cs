using System.Text.RegularExpressions;

namespace AnnasArchive.Tests.Configuration;

/// <summary>
/// Guards the single-error-contract convention at the level it actually lives:
/// across 38 endpoint files.
///
/// <see cref="GlobalExceptionHandlerTests"/> proves the middleware maps
/// <see cref="ArgumentException"/> to a 400. It cannot prove that the endpoints
/// still *let it reach* the middleware — that depends on 29 separate
/// <c>when (ex is not ArgumentException)</c> filters, and deleting one is a
/// silent 400-to-500 regression that no behavioural test would notice without
/// standing up all 29 endpoints and forcing an argument failure through each.
///
/// So this reads the source. It is a convention test, and it fails loudly rather
/// than skipping when it cannot find the tree — a guard that quietly stops
/// running is worse than no guard.
/// </summary>
public class ErrorContractConventionTests
{
    /// <summary>The one place this pattern is still allowed, and why.</summary>
    private const string SseException = "AiSectionSummaryEndpoints.cs";

    private static DirectoryInfo SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "annas-archive-util.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test must be run from inside the repository, not a published bundle");

        var endpoints = new DirectoryInfo(Path.Combine(dir!.FullName, "src", "AnnasArchive.API", "Endpoints"));
        endpoints.Exists.Should().BeTrue($"expected endpoint sources at {endpoints.FullName}");
        return endpoints;
    }

    private static IEnumerable<(string Name, string Text)> EndpointSources() =>
        SourceRoot().GetFiles("*.cs").Select(f => (f.Name, File.ReadAllText(f.FullName)));

    /// <summary>Every source file in the API project. Used by the checks whose
    /// rule is not specific to endpoints.</summary>
    private static IEnumerable<(string Name, string Text)> ApiSources()
    {
        var api = SourceRoot().Parent!;
        return api.GetFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Name: Path.GetRelativePath(api.FullName, f.FullName), Text: File.ReadAllText(f.FullName)));
    }

    /// <summary>
    /// Blanks out comment lines, keeping line numbering intact.
    ///
    /// <para>Needed because a rule worth guarding is also worth documenting, and
    /// the clearest documentation quotes the pattern being banned. Without this,
    /// the doc comment on <c>DescriptionEndpoint.FetchAsync</c> — which shows the
    /// interpolated template it exists to prevent — is itself reported as a
    /// violation.</para>
    /// </summary>
    private static string WithoutComments(string text) =>
        string.Join('\n', text.Split('\n').Select(line =>
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("/*")
                ? ""
                : line;
        }));

    /// <summary>
    /// The exact shape that was deleted from 29 handlers. It answered 400 with
    /// <c>{ error = "Invalid parameter: &lt;name&gt;" }</c> — a second error
    /// contract with no <c>errorCode</c> and no <c>details</c>, and with the
    /// exception's own sentence thrown away.
    /// </summary>
    [Fact]
    public void NoEndpointReintroducesTheHandWrittenInvalidParameterResponse()
    {
        // Matched on the single line, not the whole file: a file may legitimately
        // contain both a BadRequest (for its own up-front validation) and the SSE
        // error event, and a file-level conjunction reports that as a violation.
        var offenders = EndpointSources()
            .SelectMany(f => f.Text.Split('\n').Select(line => (f.Name, Line: line)))
            .Where(x => x.Line.Contains("Results.BadRequest") && x.Line.Contains("Invalid parameter: {ex.ParamName"))
            .Select(x => x.Name)
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty(
            "ArgumentException is mapped to a 400 once, by UseGlobalExceptionHandler; " +
            "a handler that catches it locally answers with a poorer error body");
    }

    /// <summary>
    /// Every remaining use of that string must be the documented SSE case, where
    /// the response has already started and the middleware can only log.
    /// </summary>
    [Fact]
    public void TheOnlyRemainingInvalidParameterStringIsTheDocumentedSseCase()
    {
        var files = EndpointSources()
            .Where(f => f.Text.Contains("Invalid parameter: {ex.ParamName"))
            .Select(f => f.Name)
            .ToList();

        files.Should().BeEquivalentTo(new[] { SseException });
    }

    /// <summary>
    /// Catching <see cref="ArgumentException"/> at all is what breaks the
    /// contract, so the catch itself is what is checked — not just the response
    /// body. The SSE handler is exempt for the reason documented above it.
    /// </summary>
    [Fact]
    public void NoEndpointCatchesArgumentExceptionExceptTheSseHandler()
    {
        var offenders = EndpointSources()
            .Where(f => f.Name != SseException)
            .Where(f => Regex.IsMatch(f.Text, @"catch\s*\(\s*ArgumentException"))
            .Select(f => f.Name)
            .ToList();

        offenders.Should().BeEmpty(
            "these were removed so the exception reaches UseGlobalExceptionHandler");
    }

    /// <summary>
    /// The other half of the change, and the dangerous one to lose. A bare
    /// <c>catch (Exception)</c> sitting where an ArgumentException catch used to be
    /// swallows argument failures as 500s again — silently, since the endpoint
    /// still returns a plausible response.
    ///
    /// <para>Asserted as an <b>exact count per file</b>, not as "contains at least
    /// one". The first version of this test used <c>Contains</c> and a mutation
    /// proved it worthless: deleting one of DropboxReaderEndpoints' seven filters
    /// left six, so the assertion passed against code that had the regression.
    /// A file with N filtered catch-alls needs all N.</para>
    ///
    /// <para>If you legitimately add or remove an endpoint, update the number here
    /// deliberately — that edit is the point, not an obstacle.</para>
    /// </summary>
    [Fact]
    public void EveryFilteredCatchAllStillCarriesItsFilter()
    {
        var expected = new Dictionary<string, int>
        {
            ["AiBookSearchEndpoints.cs"] = 5,
            ["AiCharacterEndpoints.cs"] = 2,
            ["AiFlashcardsEndpoints.cs"] = 3,
            ["AiMediaSearchEndpoints.cs"] = 1,
            ["AiSectionSummaryEndpoints.cs"] = 1,
            ["AiSummarizeEndpoints.cs"] = 1,
            ["AiVocabEndpoints.cs"] = 1,
            ["AnnaDownloadEndpoints.cs"] = 2,
            ["BookSearchEndpoints.cs"] = 1,
            ["DropboxReaderEndpoints.cs"] = 7,
            ["GamingEndpoints.cs"] = 2,
            ["QuizEndpoints.cs"] = 1,
            ["VideoLibraryMetadataEndpoints.cs"] = 2,
            ["VpnSettingsEndpoints.cs"] = 1,
        };

        var actual = EndpointSources()
            .Select(f => (f.Name, Count: CountOccurrences(f.Text, "when (ex is not ArgumentException)")))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.Name, x => x.Count);

        actual.Should().BeEquivalentTo(expected,
            "a catch-all that loses its filter turns that endpoint's 400s back into 500s");
    }

    /// <summary>
    /// A failure must not be reported as HTTP 200.
    ///
    /// Fifteen sites used to answer <c>Results.Ok(new { success = false, … })</c>.
    /// The browser read the flag and showed the error correctly, so nothing was
    /// visibly broken — but Serilog, Seq and every status-code-based dashboard saw
    /// an unbroken wall of 200s, including through an hour where every download
    /// failed. A failure the tooling cannot see is a failure nobody can count.
    ///
    /// <para>Scans backwards from each <c>success = false</c> to the return that
    /// produced it, so a multi-line object literal is caught too — the fifteenth
    /// site was found only because it was written across several lines and a
    /// single-line grep had missed it.</para>
    /// </summary>
    [Fact]
    public void NoEndpointReportsAFailureAsHttp200()
    {
        var offenders = new List<string>();

        foreach (var (name, text) in EndpointSources())
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("success = false")) continue;
                if (lines[i].TrimStart().StartsWith("//")) continue;   // a comment about the rule

                // Walk back to the return that owns this literal.
                for (var j = i; j >= 0 && j > i - 12; j--)
                {
                    if (lines[j].Contains("Results.Json") || lines[j].Contains("statusCode:")) break;
                    if (lines[j].Contains("Results.Ok("))
                    {
                        offenders.Add($"{name}:{i + 1}");
                        break;
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "a failed operation must carry a non-2xx status; success=false inside a 200 " +
            "is invisible to Serilog, Seq and any status-based monitoring");
    }

    /// <summary>
    /// A Serilog message template must be a literal, never an interpolated string.
    ///
    /// <para>Four description endpoints independently arrived at
    /// <c>Log.Information(found ? $"… for '{title}'" : $"…")</c>. That hands
    /// Serilog a template built from the book's own title, so a title containing
    /// a brace is parsed as a placeholder and the line is mangled or dropped —
    /// and scraped titles do contain braces. It also destroys the structured
    /// property, so the value cannot be searched on in Seq.</para>
    ///
    /// <para>The interpolation is frequently not on the same line as the
    /// <c>Log.</c> call — in all four cases it sat inside a ternary on the
    /// following line — so this parses the first argument of each call rather
    /// than grepping. A line-based check finds none of them.</para>
    /// </summary>
    [Fact]
    public void NoLogCallBuildsItsTemplateByInterpolation()
    {
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var (name, raw) in ApiSources())
        {
            var text = WithoutComments(raw);

            // Fully qualified: the test project also references Moq, which has its own Match.
            foreach (System.Text.RegularExpressions.Match call in
                     Regex.Matches(text, @"\bLog\.(Information|Warning|Error|Debug|Fatal|Verbose)\s*\("))
            {
                scanned++;
                var open = call.Index + call.Length - 1;
                if (FirstArgument(text, open).Contains("$\""))
                    offenders.Add($"{name}:{text[..call.Index].Count(c => c == '\n') + 1}");
            }
        }

        // A guard that quietly stops finding anything is worse than no guard.
        scanned.Should().BeGreaterThan(500, "the scan should be reaching the whole API project");

        offenders.Should().BeEmpty(
            "the template must be a literal and the values passed as arguments; " +
            "an interpolated template mangles any value containing a brace and " +
            "leaves nothing structured to search on");
    }

    /// <summary>
    /// The source text of a call's first argument, given the index of its opening
    /// parenthesis. Stops at the first top-level comma, so a template that is
    /// correctly literal is not blamed for an interpolated <em>argument</em> after
    /// it — passing <c>$"…"</c> as a value is ugly but harmless.
    /// </summary>
    private static string FirstArgument(string text, int openParen)
    {
        var depth = 0;
        for (var i = openParen; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(': depth++; break;
                case ')':
                    depth--;
                    if (depth == 0) return text[(openParen + 1)..i];
                    break;
                case ',' when depth == 1:
                    return text[(openParen + 1)..i];
            }
        }
        return "";
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
