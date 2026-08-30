using System.Text.RegularExpressions;

namespace AnnasArchive.Tests.Configuration;

/// <summary>
/// Guards the single-error-contract convention at the level it actually lives:
/// across 38 endpoint files.
///
/// <see cref="GlobalExceptionHandlerTests"/> proves the middleware maps
/// <see cref="ArgumentException"/> to a 400. It cannot prove that the endpoints
/// still *let it reach* the middleware — that depends on 15 separate
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
    // There is no longer an exemption. The single one that existed covered Reader
    // I's SSE summary endpoint, where the response had already started and the
    // middleware could only log. That endpoint is deleted, and Reader II's streams
    // go through SseStream/ReaderRequest, which turn a failure into one error event
    // rather than catching ArgumentException themselves. So the rules below are now
    // unconditional — which is the stronger claim, and the one worth keeping.

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
    /// The string is now gone entirely: the one endpoint allowed to use it was
    /// Reader I's, and it is deleted.
    /// </summary>
    [Fact]
    public void NoEndpointUsesTheInvalidParameterStringAtAll()
    {
        var files = EndpointSources()
            .Where(f => f.Text.Contains("Invalid parameter: {ex.ParamName"))
            .Select(f => f.Name)
            .ToList();

        files.Should().BeEmpty(
            "every ArgumentException now reaches UseGlobalExceptionHandler, which "
            + "answers with the exception's own sentence");
    }

    /// <summary>
    /// Catching <see cref="ArgumentException"/> at all is what breaks the
    /// contract, so the catch itself is what is checked — not just the response
    /// body. The SSE handler is exempt for the reason documented above it.
    /// </summary>
    [Fact]
    public void NoEndpointCatchesArgumentException()
    {
        var offenders = EndpointSources()
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
    /// proved it worthless: deleting one of AiBookSearchEndpoints' five filters
    /// left four, so the assertion passed against code that had the regression.
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
            ["AiMediaSearchEndpoints.cs"] = 1,
            ["AnnaDownloadEndpoints.cs"] = 2,
            ["BookSearchEndpoints.cs"] = 1,
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
    /// Error bodies are built by <c>ApiResponse</c>, not by hand.
    ///
    /// <para>The shape <c>{ error = … }</c> was written out at 279 call sites. The
    /// cost of that was not untidiness: it is why the one site that drifted went
    /// unnoticed. A quiz endpoint answered <c>{ errors = … }</c> — plural, with no
    /// singular <c>error</c> — and since the frontend interceptor accepts a body
    /// only when it has a top-level string <c>error</c>, every validation message
    /// it produced was discarded and shown as a bare "Http failure response …
    /// 400 Bad Request".</para>
    ///
    /// <para>One hand-written site is one that can drift again, so the rule is
    /// enforced rather than agreed.</para>
    /// </summary>
    [Fact]
    public void NoEndpointHandWritesAPlainErrorBody()
    {
        var offenders = HandBuiltErrorBodies()
            .Where(b => b.Properties.Count == 1 && b.Properties[0] == "error")
            .Select(b => b.Where)
            .ToList();

        offenders.Should().BeEmpty(
            "a body that is exactly { error = … } is what ApiResponse.BadRequest/NotFound/" +
            "Conflict produces; writing it out again puts the shape back in N places");
    }

    /// <summary>
    /// The rule underneath the one above, and the one that actually bit.
    ///
    /// <para>A richer body is fine — two endpoints legitimately add <c>status</c> or
    /// <c>existingFileName</c> alongside the message, and the interceptor reads
    /// straight past them. What is not fine is an error body with <b>no top-level
    /// <c>error</c> at all</b>: the interceptor accepts a body only when
    /// <c>typeof body.error === 'string'</c>, so such a body is discarded whole and
    /// the user sees "Http failure response … 400 Bad Request" instead of the
    /// message that was carefully computed and sent.</para>
    /// </summary>
    [Fact]
    public void EveryHandWrittenErrorBodyStillCarriesAnErrorField()
    {
        var offenders = HandBuiltErrorBodies()
            .Where(b => !b.Properties.Contains("error"))
            .Select(b => $"{b.Where} -> {{ {string.Join(", ", b.Properties)} }}")
            .ToList();

        offenders.Should().BeEmpty(
            "the frontend interceptor discards any error body without a top-level string " +
            "'error'; { errors = … } shipped like this and silently swallowed every quiz " +
            "validation message");
    }

    /// <summary>
    /// The SSE preamble is set by <c>ServerSentEventsHelper.BeginStream</c>, not
    /// by hand.
    ///
    /// <para>Three call sites set the same three headers three different ways, and
    /// one used <c>Headers.Append("Content-Type", …)</c> — which appends rather
    /// than replaces, so a content type already on the response becomes
    /// <c>application/json, text/event-stream</c> and the browser refuses the
    /// stream. It worked only because nothing set a content type first on that
    /// path.</para>
    /// </summary>
    [Fact]
    public void NoEndpointSetsTheSseHeadersByHand()
    {
        var offenders = new List<string>();

        foreach (var (name, raw) in ApiSources())
        {
            if (name.EndsWith("ServerSentEventsHelper.cs")) continue;   // the one place that may

            var text = WithoutComments(raw);
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(
                         text, @"""text/event-stream"""))
            {
                offenders.Add($"{name}:{text[..m.Index].Count(c => c == '\n') + 1}");
            }
        }

        offenders.Should().BeEmpty(
            "call ServerSentEventsHelper.BeginStream(response); it assigns rather than " +
            "appends, which is the part that was getting written wrong");
    }

    /// <summary>Each <c>Results.BadRequest/NotFound/Conflict(new { … })</c> in the
    /// API project, with the property names it sets.</summary>
    private static IEnumerable<(string Where, IReadOnlyList<string> Properties)> HandBuiltErrorBodies()
    {
        foreach (var (name, raw) in ApiSources())
        {
            // The one place that is supposed to build the body.
            if (name.EndsWith("ApiResponse.cs")) continue;

            var text = WithoutComments(raw);

            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(
                         text, @"Results\.(BadRequest|NotFound|Conflict)\s*\(\s*new\s*\{"))
            {
                var open = text.IndexOf('{', m.Index);
                var body = BracedBlock(text, open);
                var props = Regex.Matches(body, @"(?:^|,)\s*(?:(\w+)\s*=|(\w+)\s*(?=[,}]|$))")
                    .Select(x => x.Groups[1].Success ? x.Groups[1].Value : x.Groups[2].Value)
                    .Where(p => p.Length > 0)
                    .ToList();

                yield return ($"{name}:{text[..m.Index].Count(c => c == '\n') + 1}", props);
            }
        }
    }

    /// <summary>The contents of the <c>{ … }</c> whose opening brace is at
    /// <paramref name="open"/>, ignoring braces inside string literals.</summary>
    private static string BracedBlock(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"')
                    i += text[i] == '\\' ? 2 : 1;
            }
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text[(open + 1)..i];
            }
        }
        return "";
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
