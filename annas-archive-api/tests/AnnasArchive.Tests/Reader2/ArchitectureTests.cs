using System.Reflection;
using System.Text.RegularExpressions;
using AnnasArchive.API.Reader2.Lenses;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// The rules that keep Reader II from turning into Reader I.
///
/// <para>Every one of these describes a specific way the first reader went
/// wrong, and every one is checked against the code rather than trusted to
/// review. A reviewer catches the first violation and misses the fortieth; these
/// do not get tired.</para>
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly Api = typeof(LiteraryLens).Assembly;

    private static IEnumerable<Type> Reader2Types =>
        Api.GetTypes().Where(t => t.Namespace?.StartsWith("AnnasArchive.API.Reader2") == true);

    /// <summary>Source files, found from the test binary's location.</summary>
    private static IReadOnlyList<string> SourceFiles => Sources.Value;

    private static readonly Lazy<IReadOnlyList<string>> Sources = new(() =>
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src")))
            root = root.Parent;

        var reader2 = Path.Combine(root!.FullName, "src", "AnnasArchive.API", "Reader2");
        return Directory.GetFiles(reader2, "*.cs", SearchOption.AllDirectories);
    });

    private static IEnumerable<(string File, string Text)> SourceText =>
        SourceFiles.Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

    /// <summary>
    /// The whole API project, not just <c>Reader2/</c> — needed to prove a claim
    /// about what a new book type does <i>not</i> touch. A rule scoped to Reader2
    /// alone could not see a lens name leaking into configuration or an endpoint
    /// outside it, which is exactly the leak worth catching.
    /// </summary>
    private static IEnumerable<(string File, string Text)> ApiSourceText => ApiSources.Value;

    private static readonly Lazy<IReadOnlyList<(string File, string Text)>> ApiSources = new(() =>
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src")))
            root = root.Parent;

        var api = Path.Combine(root!.FullName, "src", "AnnasArchive.API");

        return Directory.GetFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
            .ToArray();
    });

    /// <summary>
    /// The Angular half of Reader II.
    ///
    /// <para>Checked from here rather than from a Karma spec because these rules
    /// are about files on disk, and a browser test cannot see them — Angular's
    /// esbuild pipeline has no <c>require.context</c> to enumerate sources with.
    /// The rule is about the repository, so the test that can read the repository
    /// is the right place for it.</para>
    /// </summary>
    private static IEnumerable<(string File, string Text)> FrontendSources
    {
        get
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "annas-archive-app")))
                root = root.Parent;

            if (root is null) return [];

            var reader2 = Path.Combine(root.FullName, "annas-archive-app", "src", "app", "reader2");

            return Directory.Exists(reader2)
                ? Directory.GetFiles(reader2, "*.ts", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".spec.ts"))
                    .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
                : [];
        }
    }

    [Fact]
    public void The_frontend_rules_are_checking_real_files()
    {
        FrontendSources.Should().HaveCountGreaterThan(5, "otherwise every rule below is vacuous");
    }

    /// <summary>Reader I is being deleted; a reference here would outlive it.</summary>
    [Fact]
    public void No_reader_two_component_imports_from_the_old_reader()
    {
        FrontendSources
            .Where(s => s.Text.Contains("book-reader") || s.Text.Contains("dropboxPath"))
            .Select(s => s.File)
            .Should().BeEmpty();
    }

    /// <summary>
    /// A hard-coded book type in the UI is exactly what would make a fourth one
    /// cost a frontend change.
    /// </summary>
    [Fact]
    public void The_frontend_hard_codes_no_lens_key()
    {
        FrontendSources
            .Where(s => !s.File.Contains("models"))
            .Where(s => Regex.IsMatch(s.Text, "[\"'](literary|military|fiction)[\"']"))
            .Select(s => s.File)
            .Should().BeEmpty("the picker renders whatever GET /lenses returns");
    }

    [Fact]
    public void No_reader_two_component_exceeds_two_hundred_lines()
    {
        FrontendSources
            .Where(s => s.File.EndsWith(".component.ts"))
            .Where(s => s.Text.Split('\n').Length > 200)
            .Select(s => s.File)
            .Should().BeEmpty();
    }

    /// <summary>
    /// Components render and decide; stores fetch. No component talks to the
    /// network at all — not even the shell.
    ///
    /// <para>The shell was exempt while it made a handful of calls of its own.
    /// Once every one of those moved into a store the exemption became a hole
    /// rather than a concession: an exempt file is where the next direct call
    /// would land, and nothing would have objected.</para>
    /// </summary>
    [Fact]
    public void No_component_talks_to_the_network()
    {
        FrontendSources
            .Where(s => s.File.EndsWith(".component.ts"))
            .Where(s => s.Text.Contains("HttpClient") || s.Text.Contains("Reader2ApiService"))
            .Select(s => s.File)
            .Should().BeEmpty("components read stores; only stores read the network");
    }

    /// <summary>
    /// <c>force=true</c> is the only flag that bills for work the household has
    /// already paid for, and it must never be reachable without the reader being
    /// asked. Every caller goes through <c>RegeneratePrompt</c>.
    ///
    /// <para>Scoped to components that inject a store, because that is where a
    /// generating call can actually be made. A presenter naming <c>force</c> in
    /// an output is only describing what it will report upwards — it has nothing
    /// to spend and nothing to ask about, and requiring it to confirm would push
    /// the dialog down into pieces that are meant to be dumb.</para>
    /// </summary>
    [Fact]
    public void Nothing_regenerates_without_asking_first()
    {
        var containers = FrontendSources
            .Where(s => s.File.EndsWith(".component.ts"))
            .Where(s => s.Text.Contains("Store);") && Regex.IsMatch(s.Text, @"\bforce\b"))
            .ToArray();

        containers.Should().NotBeEmpty("this test is worthless if it finds no container that can spend");

        containers
            .Where(s => !s.Text.Contains("ReaderConfirm"))
            .Select(s => s.File)
            .Should().BeEmpty("a force path that does not go through ReaderConfirm spends silently");
    }

    /// <summary>
    /// The forbidden list from the spec. Reader II shares infrastructure with the
    /// application and nothing at all with the reader it replaces.
    /// </summary>
    [Theory]
    [InlineData("EpubChapterCache")]
    [InlineData("DropboxEpubCache")]
    [InlineData("LibraryEpubCache")]
    [InlineData("EpubZipPaths")]
    [InlineData("AiSummaryHelpers")]
    [InlineData("ChapterLabelingHelper")]
    [InlineData("ReaderPrompts")]
    [InlineData("ChapterSummaryPrompts")]
    [InlineData("VocabularyCache")]
    [InlineData("dropboxPath")]
    public void No_reader_two_source_mentions_a_reader_one_type(string forbidden)
    {
        SourceText
            .Where(s => s.Text.Contains(forbidden, StringComparison.Ordinal))
            .Select(s => s.File)
            .Should().BeEmpty($"Reader II must not reference {forbidden}");
    }

    /// <summary>
    /// A <c>switch</c> on a lens key is how "parameterised by lens" quietly
    /// becomes a pile of special cases — and it is what makes a fourth book type
    /// cost more than one class.
    /// </summary>
    [Fact]
    public void Nothing_branches_on_a_lens_key()
    {
        var branching = SourceText
            .Where(s => !s.File.StartsWith("Lens") && s.File != "IReaderLens.cs")
            .Where(s => Regex.IsMatch(s.Text, @"(==|!=)\s*""(literary|military|fiction)""")
                        || Regex.IsMatch(s.Text, @"case\s+""(literary|military|fiction)"""))
            .Select(s => s.File);

        branching.Should().BeEmpty("a lens supplies behaviour; nothing asks which lens it is");
    }

    /// <summary>
    /// Every AI call goes through the shared completion services, which are what
    /// bill the reader. A raw <c>HttpClient</c> would spend money invisibly.
    /// </summary>
    [Fact]
    public void Nothing_builds_its_own_http_client_or_reads_the_api_key()
    {
        SourceText
            .Where(s => s.Text.Contains("new HttpClient") || s.Text.Contains("OpenAI:ApiKey"))
            .Select(s => s.File)
            .Should().BeEmpty("AI calls go through IAiChatCompletion, which bills");
    }

    /// <summary>
    /// Everything is request-scoped, resumable, and idempotent. Background work
    /// is what makes a reader's spend happen when they are not looking.
    /// </summary>
    [Fact]
    public void Nothing_runs_work_outside_the_request()
    {
        var offenders = SourceText
            .Where(s => s.Text.Contains("Task.Run(")
                        || s.Text.Contains(": BackgroundService")
                        // Any discarded call result, not just one whose name ends in
                        // Async — `_ = progress.StepAsync(…)` is the exact pattern
                        // this bans, and a name-shaped regex would miss it.
                        || Regex.IsMatch(s.Text, @"^\s*_\s*=(?!>)\s*[^;=]*\(", RegexOptions.Multiline))
            .Select(s => s.File);

        offenders.Should().BeEmpty(
            "no Task.Run, no BackgroundService, and no fire-and-forget — the SSE relay "
            + "in SseStream chains onto an awaited tail precisely to avoid the last one");
    }

    /// <summary>
    /// Identity arrives as a <c>ReaderContext</c>. A second place that works out
    /// which file a book is, is how Reader I ended up with twelve path builders.
    /// </summary>
    [Fact]
    public void Only_the_library_source_resolves_the_library_root()
    {
        // ContainSingle, not Equal: Equal takes params, so a "because" string
        // passed to it becomes a second expected element rather than an explanation.
        SourceText
            .Where(s => s.Text.Contains("LibraryHelpers.ResolveLibraryRoot("))
            .Select(s => s.File)
            .Should().ContainSingle().Which.Should().Be("ILibraryBookSource.cs");
    }

    [Fact]
    public void No_reader_two_file_exceeds_three_hundred_lines()
    {
        var tooLong = SourceFiles
            .Select(f => (Name: Path.GetFileName(f), Lines: File.ReadAllLines(f).Length))
            .Where(f => f.Lines > 300)
            .Select(f => $"{f.Name} ({f.Lines})");

        tooLong.Should().BeEmpty();
    }

    /// <summary>
    /// Nothing generates on open, on scroll, or ahead of the reader. A route that
    /// spends money on a <c>GET</c>, <c>DELETE</c>, <c>PUT</c>, or <c>PATCH</c> is
    /// one a browser can prefetch and a refresh can re-bill.
    /// </summary>
    /// <remarks>
    /// Checks what the handler actually does rather than guessing from its path.
    /// A path-keyword blacklist (the previous version of this test) missed
    /// <c>HandleSectionVocab</c>, which was mapped to both a <c>GET</c> and a
    /// <c>POST</c> and reached the gateway's generating path either way — a
    /// section nobody had summarised yet was one a mere page
    /// load could bill for, because "vocabulary" was never on the blacklist. This
    /// version reads the handler's own body, so the same class of bug fails here
    /// however the route happens to be named.
    /// </remarks>
    [Fact]
    public void Only_post_routes_can_spend_money()
    {
        var routes = Path.GetDirectoryName(SourceFiles.First(f => f.EndsWith("AiRoutes.cs")))!;

        foreach (var file in Directory.GetFiles(routes, "*Routes.cs"))
        {
            var text = File.ReadAllText(file);

            foreach (System.Text.RegularExpressions.Match route in Regex.Matches(
                         text, @"group\.Map(Get|Delete|Put|Patch)\(""[^""]+"",\s*(\w+)\)"))
            {
                var verb = route.Groups[1].Value;
                var handler = route.Groups[2].Value;
                var body = HandlerBody(text, handler);

                body.Should().NotBeEmpty($"{handler} should be a handler declared in {Path.GetFileName(file)}");

                foreach (var spend in SpendSymbols)
                    body.Should().NotContain(
                        spend, $"{handler} answers a {verb}, and nothing may spend money on one");
            }
        }
    }

    /// <summary>
    /// Anything reaching one of these has bought something: generated a chapter's
    /// worth of AI output, or run the model-facing half of an ingest.
    /// </summary>
    private static readonly string[] SpendSymbols =
        ["GetOrGenerateAsync(", ".IngestAsync(", ".BackFillAsync(", "AskLensAsync(", "AskSharedAsync("];

    /// <summary>
    /// The source from a handler's declaration up to whatever comes next — the
    /// next <c>private static</c> member, or the end of the file. Text-based
    /// rather than a real parser, matching how the rest of this file already
    /// scans; every handler here is a single expression- or block-bodied method,
    /// which is what makes "the next declaration" a safe stopping point.
    /// </summary>
    private static string HandlerBody(string source, string handlerName)
    {
        var start = source.IndexOf($" {handlerName}(", StringComparison.Ordinal);
        if (start < 0) return "";

        var next = Regex.Match(source[(start + 1)..], @"\n    (private|internal|public) static \S");

        return next.Success ? source.Substring(start, next.Index + 1) : source[start..];
    }

    /// <summary>
    /// The store is the only way to a table. A stray <c>CREATE TABLE</c> or a
    /// second query path is how the twelve caches happened.
    /// </summary>
    [Fact]
    public void Only_the_stores_talk_to_sqlite()
    {
        SourceText
            .Where(s => s.Text.Contains("CommandText"))
            .Select(s => s.File)
            .Should().BeEquivalentTo([
                "SqliteArtifactStore.cs", "BookRegistry.cs", "ReaderStateStore.cs", "VocabularyStore.cs",
                "BookmarkStore.cs"
            ]);
    }

    /// <summary>
    /// A book type costs one class and one DI line, and nothing else.
    ///
    /// <para>This is the claim the whole rebuild rests on, and until now it was
    /// only ever confirmed by reading. Stated as a rule: the name of a concrete
    /// lens may appear in its own file and in the composition root, and nowhere
    /// else in the API. A prompt table keyed by lens, an <c>if</c> in an endpoint,
    /// a budget looked up by name — each of them would make a fourth book type
    /// cost an edit somewhere a reviewer has to remember to look.</para>
    ///
    /// <para>Phase 6 adds two more lenses. If this rule holds afterwards, they
    /// genuinely were one class each.</para>
    /// </summary>
    [Fact]
    public void A_book_type_costs_one_class_and_one_registration()
    {
        var lenses = Reader2Types
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IReaderLens).IsAssignableFrom(t))
            .ToArray();

        lenses.Should().NotBeEmpty("this test is worthless if it finds no lens");

        foreach (var lens in lenses)
        {
            var mentions = ApiSourceText
                .Where(s => Regex.IsMatch(s.Text, $@"\b{Regex.Escape(lens.Name)}\b"))
                .Select(s => s.File)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            mentions.Should().BeEquivalentTo(
                [$"{lens.Name}.cs", "CoreServiceConfiguration.cs"],
                $"{lens.Name} must cost its own class and one DI line, nothing more");
        }
    }

    /// <summary>
    /// Every registered Reader II service resolves. A constructor parameter added
    /// without its registration otherwise fails at the reader's first request.
    /// </summary>
    [Fact]
    public void Every_reader_two_service_is_registered()
    {
        var interfaces = Reader2Types
            .Where(t => t.IsInterface && t.Name.StartsWith('I') && t.Name != nameof(IReaderLens))
            .Where(t => t.Name is not ("IVersionedArtifact`1"))
            .ToArray();

        interfaces.Should().NotBeEmpty("this test is worthless if it finds nothing");

        foreach (var contract in interfaces)
            Reader2Types.Any(t => t.IsClass && !t.IsAbstract && contract.IsAssignableFrom(t))
                .Should().BeTrue($"{contract.Name} has no implementation");
    }
}
