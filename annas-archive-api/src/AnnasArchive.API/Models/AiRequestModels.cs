using AnnasArchive.Core.Services;

namespace AnnasArchive.API.Models;

/// <summary>
/// Request/response models for AI-powered endpoints.
/// </summary>

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

// ─── Search Result Grouping (duplicate/format detection) ──────────────────
// Anna's Archive/LibGen return many near-duplicate entries per book — the
// same edition uploaded as EPUB, PDF, MOBI, or just re-scanned/re-uploaded
// multiple times in the same format. This groups which search results are
// really the same underlying book, so the frontend can collapse them into
// one card instead of one row per file.
public record GroupSearchResultsRequest(List<GroupableBook> Books);

public record GroupableBook(
    string Md5,
    string Title,
    List<string> Authors,
    string Format,
    int? Year);

/// <summary>Each inner list is the Md5s of one group of "same book" results —
/// every Md5 from the request appears in exactly one group, including books
/// with no duplicates (a singleton group of one).</summary>
public record GroupSearchResultsResponse(List<List<string>> Groups);

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

