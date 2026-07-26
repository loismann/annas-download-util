namespace AnnasArchive.API.Models;

/// <summary>
/// Models for the daily library-review modal (cull-then-genre triage of Paul's 8000+ untagged books).
/// </summary>

public record LibraryReviewStatusResponse(
    string Phase,             // "cull" | "genre" | "complete"
    bool ShouldShow,
    int RemainingInPhase,
    bool SessionInProgress);

public record LibraryReviewBookDto(
    string FileName,
    string Title,
    string[] Authors,
    string[] Tags,
    string? Series,
    string? CoverUrl,
    string Format,
    string[] FavoritedBy);

/// <param name="TotalRemainingInPhase">Size of the whole eligible pool for this phase at the
/// moment the session started/resumed — includes the books in this session's own batch. Lets
/// the modal show overall progress ("N left in this phase"), not just position within today's batch.</param>
public record LibraryReviewSessionResponse(
    string Phase,
    List<LibraryReviewBookDto> Books,
    int TotalRemainingInPhase);

public record LibraryReviewDecisionRequest(
    string FileName,
    string Decision);          // "keep" | "delete" | "genreSet"

public record LibraryReviewDecisionResult(bool Success, string? Error);

/// <summary>
/// Persisted global progress state for the review flow — single file, not per-user, since this is
/// inherently a Paul-only feature. Loaded/saved as one JSON blob by <see cref="Services.ILibraryReviewService"/>.
/// </summary>
public class LibraryReviewProgressState
{
    public DateTime? LastShownUtc { get; set; }
    public string? ActiveSessionPhase { get; set; }
    public List<string> ActiveSessionFileNames { get; set; } = new();
    public List<string> ActiveSessionDecidedFileNames { get; set; } = new();
}
