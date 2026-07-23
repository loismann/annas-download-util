using AnnasArchive.Core.Services;

namespace AnnasArchive.API.Models;

/// <summary>
/// Request/response models for AI-powered endpoints.
/// </summary>

// ─── Summarization ───────────────────────────────────────────────────────
public record SummarizeRequest(
    string Text,
    string? BookTitle,
    string? Author,
    int? Year,
    string? Premise,
    string? DropboxPath,
    int? ChapterId,
    int? WordOffset,
    List<string>? KnownWords);

public record SummarizeResponse(string Summary);

// ─── Learn More ──────────────────────────────────────────────────────────
public record LearnMoreRequest(
    string Term,
    string? Definition,
    string? DropboxPath,
    string? BookTitle,
    string? Context);

public record LearnMoreResponse(string Detail);

// ─── Flashcards ──────────────────────────────────────────────────────────
public record FlashcardRequest(
    string Term,
    string? Definition,
    string? DropboxPath,
    string? BookTitle,
    string? Context,
    List<string>? KnownWords,
    bool? SaveToLibrary);

public record FlashcardResult(List<FlashcardItem> Cards);

// ─── Author Suggestions ──────────────────────────────────────────────────
public record SuggestAuthorsRequest(string BookTitle);
public record SuggestAuthorsResponse(List<AuthorSuggestion> Authors);
public record AuthorSuggestion(string Author, string Confidence);

// ─── Related Books ───────────────────────────────────────────────────────
public record RelatedBooksRequest(string BookTitle, string Author);
public record RelatedBooksResponse(
    List<SeriesBook> SameSeries,
    List<AuthorSeries> OtherSeries,
    string? SeriesSummary);

public record SeriesBook(
    string Title,
    int Order,
    string Description,
    string? CoverUrl,
    string? DescriptionSource = null);

public record AuthorSeries(
    string SeriesName,
    int BookCount,
    List<SeriesBook> Books,
    string Description,
    string Summary);

// ─── AI Book Search ──────────────────────────────────────────────────────
public record AiBookSearchRequest(string Query);

public record AiBookSearchItem(
    string Title,
    string Author,
    string Summary,
    string Importance,
    string? CoverUrl,
    string? DescriptionSource = null);

public record AiBookSearchResponse(string? Summary, List<AiBookSearchItem> Books);

// ─── AI TV/Movie Search ──────────────────────────────────────────────────
public record AiMediaSearchRequest(string Query);

/// <summary>Type is the model's own best judgment of whether a title is
/// normally catalogued as a TV series or a movie (e.g. anime OVAs can go
/// either way) — the frontend resolves each one against Sonarr or Radarr
/// accordingly.</summary>
public record AiMediaSearchItem(
    string Title,
    int? Year,
    string Type,
    string? Blurb);

public record AiMediaSearchResponse(string? Summary, List<AiMediaSearchItem> Results);

// ─── Series Book Matching ────────────────────────────────────────────────
public record MatchSeriesBooksRequest(
    string? SeriesName,
    string Author,
    string? PreferredFormat,
    List<BookWithCandidates> Books);

public record BookWithCandidates(
    string Title,
    int Order,
    List<CandidateBook> Candidates);

public record CandidateBook(
    string Md5,
    string Title,
    List<string> Authors,
    string Format,
    string FileSize);

public record SeriesBookMatch(
    string BookTitle,
    int Order,
    string Status,
    string? SelectedMd5,
    string? SelectedTitle,
    string Confidence,
    string Reason);

public record MatchSeriesBooksResponse(List<SeriesBookMatch> Matches);

// ─── Chapter Summaries ───────────────────────────────────────────────────
public record FullChapterSummaryRequest(
    string DropboxPath,
    int ChapterId,
    string? BookTitle,
    string? Author,
    int? Year,
    string? Premise,
    int? DisplayChapterNumber = null,
    bool ForceRegenerate = false);

public record UltraChapterSummaryRequest(
    string DropboxPath,
    int ChapterId,
    string? BookTitle,
    string? Author,
    int? Year,
    string? Premise,
    int? DisplayChapterNumber = null,
    bool ForceRegenerate = false);

public record FullChapterSummaryResponse(
    string Summary,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    double? AllowanceUsedPercent,
    long? TokensRemaining,
    DateTime CachedAt,
    List<ProcessingStep> Steps);

public record ProcessingStep(
    string Stage,
    int StepNumber,
    int TotalSteps,
    string Message,
    bool Success,
    string? Error);

public record ChapterSummaryCacheResponse(
    string Summary,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    DateTime CachedAt);

// ─── Token Usage ─────────────────────────────────────────────────────────
public record TokenUsageResponse(
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    long? Allowance,
    double? AllowanceUsedPercent,
    long? TokensRemaining,
    DateTime? ResetsAtUtc,
    double? TotalCostUsd);

public record UserTokenUsage(
    string UserId,
    string DisplayName,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    double TotalCostUsd,
    double AllowanceUsd,
    double AllowanceUsedPercent,
    DateTime ResetsAtUtc,
    bool IsOverLimit);

// ─── Section Summaries ───────────────────────────────────────────────────
public record ChunkBoundary(int Start, int End, int WordCount);
public record ChunkBoundariesResponse(int ChapterId, List<ChunkBoundary> Chunks, DateTime CachedAt);
public record SectionSummaryRequest(string DropboxPath, int ChapterId, int SectionIndex, string? BookTitle, string? Author);
public record SectionSummaryResponse(string Summary, int SectionIndex, int PromptTokens, int CompletionTokens, int TotalTokens, DateTime CachedAt, List<FlashcardItem>? Vocab = null);
public record SaveSectionVocabRequest(string DropboxPath, int ChapterId, int SectionIndex, List<FlashcardItem> Vocab);

// ─── Vocabulary Tracking ─────────────────────────────────────────────────
public record AddVocabWordRequest(string Term, string? BookId);
public record AddStudyWordRequest(string Term, string? Definition, string? BookId);

// ─── Character Graph ─────────────────────────────────────────────────────
public record CharacterGraphRequest(string DropboxPath, string? BookTitle, string? Context);
public record CharacterGraphUpdateRequest(string DropboxPath, string NewContent);
public record CharacterNode(string Id, string Label, string Description, string? DetailedDescription);
public record CharacterEdge(string From, string To, string Label, string? DetailedDescription);
public record CharacterGraphResponse(List<CharacterNode> Nodes, List<CharacterEdge> Edges, int SummaryCount, DateTime CachedAt);
