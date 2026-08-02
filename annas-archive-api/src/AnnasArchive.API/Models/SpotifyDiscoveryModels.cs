namespace AnnasArchive.API.Models;

public enum SpotifyDiscoveryDraftState
{
    AwaitingClarification,
    Resolving,
    Ready,
    Partial
}

public enum SpotifyCandidateResolution
{
    Resolved,
    Ambiguous,
    NotFound
}

public record SpotifyDiscoveryCandidate(
    string Id,
    int Position,
    string Artist,
    string Title,
    string? Rationale,
    SpotifyCandidateResolution Resolution,
    SpotifyTrackDto? Track,
    IReadOnlyList<SpotifyTrackDto> Alternatives,
    bool ProbablyUnfamiliar,
    string FamiliarityLabel
);

public record SpotifyDiscoveryDraft(
    string Id,
    SpotifyDiscoveryDraftState State,
    string Name,
    string Summary,
    IReadOnlyList<string> UserPrompts,
    int DesiredTrackCount,
    string? ClarifyingQuestion,
    IReadOnlyList<SpotifyDiscoveryCandidate> Candidates,
    string KnownMusicCoverage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record SpotifyDiscoveryDraftUpdateRequest(
    string? Name = null,
    IReadOnlyList<string>? OrderedCandidateIds = null,
    IReadOnlyList<string>? RemoveCandidateIds = null,
    IReadOnlyDictionary<string, string>? CandidateSelections = null
);

internal record SpotifyDiscoveryAiCandidate(string Artist, string Title, string? Rationale = null);

internal record SpotifyDiscoveryAiResponse(
    string? SuggestedName,
    string? Summary,
    string? ClarifyingQuestion,
    IReadOnlyList<SpotifyDiscoveryAiCandidate>? Candidates
);
