using System.Security.Claims;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Lenses;
using AnnasArchive.API.Reader2.Story;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// Every model call the pipeline made, and what it was told to answer.
///
/// <para>Counting calls is the point. "A chapter summary of N chunks bills
/// N + ⌈N/4⌉ + 1" is only checkable against something that counts, and
/// double-billing a tier is a mistake Reader I's own comments record having
/// made.</para>
/// </summary>
public sealed class FakeCompletions : IAiChatCompletion
{
    public List<AiChatCall> Calls { get; } = [];

    /// <summary>Set to fail the nth call (1-based), for the mid-stream failure tests.</summary>
    public int FailOnCall { get; set; } = -1;

    public Func<AiChatCall, string> Answer { get; set; } = call => $"[{call.Endpoint}]";

    public int CallsOf(CallKind kind) =>
        Calls.Count(c => c.Endpoint == ModelCalls.EndpointName(kind));

    public Task<AiChatOutcome> CompleteAsync(
        AiChatCall call, HttpContext context, CancellationToken cancellationToken = default) =>
        Record(call);

    public Task<AiChatOutcome> CompleteAsync(
        AiChatCall call, string? userId, CancellationToken cancellationToken = default) =>
        Record(call);

    private Task<AiChatOutcome> Record(AiChatCall call)
    {
        Calls.Add(call);

        return Task.FromResult(Calls.Count == FailOnCall
            ? new AiChatOutcome(null, Results.Problem("the model fell over"))
            : new AiChatOutcome(Answer(call), null) { Usage = new AiUsage(100, 50) });
    }
}

/// <summary>A usage service that reports whatever a test needs it to.</summary>
public sealed class FakeTokenUsage : ITokenUsageService
{
    public double CostUsd { get; set; }

    public (long PromptTokens, long CompletionTokens, long TotalTokens) GetTotals(string userId) => (0, 0, 0);
    public double CalculateCostUsd(long promptTokens, long completionTokens) => CostUsd;

    public void AddUsage(string userId, int promptTokens, int completionTokens) { }
    public bool IsOverLimit(string userId, long allowance) => false;
    public void Reset(string? userId = null) { }

    public Dictionary<string, (long PromptTokens, long CompletionTokens, long TotalTokens, DateTime LastResetDate)>
        GetAllUsersUsage() => [];
}

/// <summary>A pipeline wired to real storage, a real lens, and a fake model.</summary>
public sealed class PipelineFixture : IDisposable
{
    public Reader2Fixture Store { get; } = new();
    public FakeCompletions Ai { get; } = new();
    public FakeTokenUsage Usage { get; } = new();
    public Reader2Options Options { get; }
    public ReaderAiPipeline Pipeline { get; }
    public ArtifactGateway Gateway { get; }
    public ModelCalls Model { get; }
    public StoryModelService Story { get; }
    public CastOverrideStore Corrections { get; }

    public PipelineFixture(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? []).Build();

        Options = Reader2Options.Load(configuration);
        Gateway = new ArtifactGateway(Store.Artifacts, new KeyedLocks(), Usage, configuration);

        Model = new ModelCalls(Options, new FakeModels(), Ai);
        Corrections = new CastOverrideStore(Gateway, Store.Artifacts);
        Story = new StoryModelService(Gateway, Store.Artifacts, Model, Options, Corrections);
        Pipeline = new ReaderAiPipeline(Gateway, Store.Artifacts, Store.Text, Model, Story, Options);
    }

    /// <summary>Enrols a book and writes one chapter of text straight to the store.</summary>
    public async Task<ReaderContext> WithChapterAsync(
        string text, int chapter = 0, string fileName = "book.epub")
    {
        var book = await Store.EnrolAsync(fileName, "epub bytes");
        await Store.Text.WriteChapterAsync(book, chapter, text);

        return Context((await Store.Books.GetAsync(book))!);
    }

    public static ReaderContext Context(EnrolledBook book, IReaderLens? lens = null) =>
        new(book, lens ?? new LiteraryLens(), "paul", SignedIn());

    /// <summary>Paragraphs of known length, so word counts in tests are exact.</summary>
    public static string Paragraphs(int count, int wordsEach) =>
        string.Join("\n\n", Enumerable.Range(0, count)
            .Select(p => string.Join(' ', Enumerable.Repeat($"w{p}", wordsEach))));

    private static HttpContext SignedIn()
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "paul")], "test"));
        return http;
    }

    public void Dispose() => Store.Dispose();

    /// <summary>The two configured models, named so a test can tell them apart.</summary>
    public sealed class FakeModels : IModelSelectionService
    {
        public string GetModelDeep() => "deep-model";
        public string GetModelFast() => "fast-model";
    }
}
