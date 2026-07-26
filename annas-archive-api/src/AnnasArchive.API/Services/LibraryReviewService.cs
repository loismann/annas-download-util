using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using Serilog;

namespace AnnasArchive.API.Services;

public class LibraryReviewService : ILibraryReviewService
{
    private const string OwnerTag = "Paul's Books";
    private const int BatchSize = 20;
    private static readonly TimeSpan ShowInterval = TimeSpan.FromHours(24);

    private const string ProgressStateKey = "library-review-progress";

    private readonly LibraryIndexCache _cache;
    private readonly Data.AppDatabase _db;
    private readonly Data.BookPersonalizationStore _personalization;
    private readonly string _legacyStoragePath;
    private readonly object _lock = new();

    public LibraryReviewService(
        LibraryIndexCache cache,
        Data.AppDatabase db,
        Data.BookPersonalizationStore personalization,
        string legacyStoragePath)
    {
        _cache = cache;
        _db = db;
        _personalization = personalization;
        _legacyStoragePath = legacyStoragePath;
    }

    public LibraryReviewStatusResponse GetStatus(string baseUrl)
    {
        lock (_lock)
        {
            var state = Load();
            var books = _cache.GetBooks(baseUrl);
            var (phase, remaining) = ComputeActivePhase(state, books);
            Save(state); // persists any phase-completion latch computed above
            return new LibraryReviewStatusResponse(
                phase,
                phase != "complete" && ShouldShow(state),
                remaining,
                IsSessionInProgress(state));
        }
    }

    public LibraryReviewSessionResponse StartOrResumeSession(string baseUrl)
    {
        lock (_lock)
        {
            var state = Load();
            var books = _cache.GetBooks(baseUrl);
            var byFileName = books.ToDictionary(b => b.FileName, StringComparer.OrdinalIgnoreCase);
            var (phase, totalRemaining) = ComputeActivePhase(state, books);

            if (phase == "complete")
            {
                state.ActiveSessionPhase = null;
                state.ActiveSessionFileNames.Clear();
                state.ActiveSessionDecidedFileNames.Clear();
                state.LastShownUtc = DateTime.UtcNow;
                Save(state);
                return new LibraryReviewSessionResponse("complete", new List<LibraryReviewBookDto>(), 0);
            }

            bool IsEligible(LibraryBookDto b) => phase == "cull" ? IsCullEligible(b) : IsGenreEligible(b);

            if (state.ActiveSessionPhase == phase && state.ActiveSessionFileNames.Count > 0)
            {
                // Resume today's batch — but first drop anything that fell out of eligibility
                // since it was drawn (deleted elsewhere, or its genre already got fixed through
                // the normal editor), so the session can still reach completion.
                foreach (var fn in state.ActiveSessionFileNames)
                {
                    if (state.ActiveSessionDecidedFileNames.Contains(fn, StringComparer.OrdinalIgnoreCase))
                        continue;
                    if (!byFileName.TryGetValue(fn, out var book) || !IsEligible(book))
                        state.ActiveSessionDecidedFileNames.Add(fn);
                }
            }
            else
            {
                var pool = books.Where(IsEligible).Select(b => b.FileName).ToList();
                Shuffle(pool);
                state.ActiveSessionPhase = phase;
                state.ActiveSessionFileNames = pool.Take(BatchSize).ToList();
                state.ActiveSessionDecidedFileNames = new List<string>();
            }

            state.LastShownUtc = DateTime.UtcNow;
            Save(state);

            var undecided = state.ActiveSessionFileNames
                .Where(fn => !state.ActiveSessionDecidedFileNames.Contains(fn, StringComparer.OrdinalIgnoreCase))
                .Select(fn => byFileName.TryGetValue(fn, out var b) ? b : null)
                .Where(b => b != null)
                .Select(b => new LibraryReviewBookDto(
                    b!.FileName, b.Title, b.Authors ?? Array.Empty<string>(),
                    b.Tags ?? Array.Empty<string>(), b.Series, b.CoverUrl, b.Format,
                    b.FavoritedBy ?? Array.Empty<string>()))
                .ToList();

            return new LibraryReviewSessionResponse(phase, undecided, totalRemaining);
        }
    }

    public async Task<LibraryReviewDecisionResult> RecordDecisionAsync(string fileName, string decision)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
            return new LibraryReviewDecisionResult(false, "Invalid fileName.");

        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");
        var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();

        switch (decision)
        {
            case "keep":
                // User decision → personalization store, never the enrichment sidecar.
                _personalization.Update(safeFileName, p => p.CullReviewedAt = DateTime.UtcNow);
                _cache.InvalidateCache();
                break;

            case "delete":
                try
                {
                    LibraryBookDeletionHelper.DeleteBookCompletely(safeFileName, _cache, _personalization);
                }
                catch (Exception ex)
                {
                    Log.Warning("[LibraryReview] Failed to delete {FileName}: {Message}", safeFileName, ex.Message);
                    return new LibraryReviewDecisionResult(false, "Failed to delete book.");
                }
                break;

            case "genreSet":
                {
                    // Effective genre = user override (store) falling back to enrichment (sidecar),
                    // same merge the index applies.
                    string? metaGenre = null;
                    if (File.Exists(metaPath))
                    {
                        var meta = JsonSerializer.Deserialize<LibraryBookMeta>(await File.ReadAllTextAsync(metaPath), jsonOptions);
                        metaGenre = meta?.PrimaryGenre;
                    }
                    var effectiveGenre = Data.BookPersonalizationStore.OverrideString(
                        _personalization.Get(safeFileName)?.PrimaryGenre, metaGenre);
                    if (IsGenreMissing(effectiveGenre))
                        return new LibraryReviewDecisionResult(false, "Genre has not been set for this book yet.");
                }
                break;

            default:
                return new LibraryReviewDecisionResult(false, $"Unknown decision '{decision}'.");
        }

        lock (_lock)
        {
            var state = Load();
            if (!state.ActiveSessionDecidedFileNames.Contains(safeFileName, StringComparer.OrdinalIgnoreCase))
                state.ActiveSessionDecidedFileNames.Add(safeFileName);
            Save(state);
        }

        return new LibraryReviewDecisionResult(true, null);
    }

    /// <summary>
    /// Determines the active phase against the current book list, latching CullComplete/GenreComplete
    /// onto <paramref name="state"/> in place if a phase's eligible pool has just emptied out. Does
    /// not persist — callers save afterward, once, alongside whatever else they changed.
    /// </summary>
    private static (string Phase, int Remaining) ComputeActivePhase(LibraryReviewProgressState state, List<LibraryBookDto> books)
    {
        if (!state.CullComplete)
        {
            var remaining = books.Count(IsCullEligible);
            if (remaining > 0)
                return ("cull", remaining);
            state.CullComplete = true;
        }

        if (!state.GenreComplete)
        {
            var remaining = books.Count(IsGenreEligible);
            if (remaining > 0)
                return ("genre", remaining);
            state.GenreComplete = true;
        }

        return ("complete", 0);
    }

    private static bool IsOwnedByPaul(LibraryBookDto book) =>
        (book.Tags ?? Array.Empty<string>()).Contains(OwnerTag, StringComparer.OrdinalIgnoreCase);

    private static bool IsCullEligible(LibraryBookDto book) =>
        IsOwnedByPaul(book) && book.CullReviewedAt == null;

    private static bool IsGenreEligible(LibraryBookDto book) =>
        IsOwnedByPaul(book) && IsGenreMissing(book.PrimaryGenre);

    private static bool IsGenreMissing(string? genre) =>
        string.IsNullOrWhiteSpace(genre) || string.Equals(genre, "Uncategorized", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldShow(LibraryReviewProgressState state) =>
        state.LastShownUtc == null || DateTime.UtcNow - state.LastShownUtc.Value >= ShowInterval;

    private static bool IsSessionInProgress(LibraryReviewProgressState state) =>
        state.ActiveSessionPhase != null &&
        state.ActiveSessionFileNames.Except(state.ActiveSessionDecidedFileNames, StringComparer.OrdinalIgnoreCase).Any();

    private static void Shuffle(List<string> list)
    {
        var rng = Random.Shared;
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private LibraryReviewProgressState Load()
    {
        try
        {
            var json = _db.GetState(ProgressStateKey);

            // One-time carry-over from the pre-SQLite JSON file, if it's still around.
            if (json == null && File.Exists(_legacyStoragePath))
                json = File.ReadAllText(_legacyStoragePath);

            if (json == null)
                return new LibraryReviewProgressState();

            return JsonSerializer.Deserialize<LibraryReviewProgressState>(json) ?? new LibraryReviewProgressState();
        }
        catch (Exception ex)
        {
            Log.Warning("[LibraryReview] Failed to load progress state: {Message}", ex.Message);
            return new LibraryReviewProgressState();
        }
    }

    private void Save(LibraryReviewProgressState state)
    {
        try
        {
            _db.SetState(ProgressStateKey, JsonSerializer.Serialize(state));
        }
        catch (Exception ex)
        {
            Log.Warning("[LibraryReview] Failed to persist progress state: {Message}", ex.Message);
        }
    }
}
