using AnnasArchive.API.Models;

namespace AnnasArchive.API.Services;

/// <summary>
/// Drives the daily "review your library" modal: a forced cull (keep/delete) pass over every
/// book tagged "Paul's Books", followed by a forced genre-setting pass over the same set, each
/// done 20 books at a time, at most once per rolling 24h window (or on demand).
/// </summary>
public interface ILibraryReviewService
{
    LibraryReviewStatusResponse GetStatus(string baseUrl);

    /// <summary>
    /// Starts a new daily batch, or resumes the current one if today's batch isn't finished yet
    /// (e.g. after a page refresh). Always bumps the "last shown" timestamp, whether triggered
    /// automatically or via the on-demand button — both count as "shown" for the 24h gate.
    /// </summary>
    LibraryReviewSessionResponse StartOrResumeSession(string baseUrl);

    /// <summary>
    /// Records a decision for one book in the current session: "keep" (cull phase), "delete"
    /// (cull phase — permanent, wipes all traces), or "genreSet" (genre phase — the client must
    /// have already PATCHed the book's metadata with a real genre before calling this).
    /// </summary>
    Task<LibraryReviewDecisionResult> RecordDecisionAsync(string fileName, string decision);
}
