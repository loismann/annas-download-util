using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Helpers;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.Core.Services;
using Serilog;

namespace AnnasArchive.API.Services.Spotify;

public interface ISpotifyDiscoveryService
{
    Task<SpotifyDiscoveryDraft> CreateAsync(string prompt, int desiredCount = 25, CancellationToken token = default);
    Task<SpotifyDiscoveryDraft> RefineAsync(
        string draftId, string prompt, int? desiredCount = null, CancellationToken token = default);
    SpotifyDiscoveryDraft? Get(string draftId);
    IReadOnlyList<SpotifyDiscoveryDraft> ListSaved();
    SpotifyDiscoveryDraft Update(string draftId, SpotifyDiscoveryDraftUpdateRequest request);

    /// <summary>
    /// Throws the draft away. A draft has never touched Spotify — it is candidate
    /// text and resolved catalog matches, nothing more — so unlike a playlist this
    /// really is a delete, and it needs no plan or confirmation flow behind it.
    /// </summary>
    bool Delete(string draftId);
}

/// <summary>
/// Generates candidates from the user's words, then crosses the policy boundary:
/// deterministic code resolves those candidates through Spotify and compares them
/// to account data. Spotify responses never travel back into an AI request.
/// </summary>
public sealed class SpotifyDiscoveryService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ISpotifyService spotify,
    ISpotifyKnownMusicService knownMusic,
    ISpotifyDiscoveryStore store,
    ISpotifyCurrentUser currentUser,
    ITokenUsageService tokenUsage,
    TimeProvider timeProvider) : ISpotifyDiscoveryService
{
    private static readonly JsonSerializerOptions AiOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public Task<SpotifyDiscoveryDraft> CreateAsync(
        string prompt, int desiredCount = 25, CancellationToken token = default) =>
        GenerateAsync(null, prompt, Math.Clamp(desiredCount, 5, 50), token);

    public async Task<SpotifyDiscoveryDraft> RefineAsync(
        string draftId, string prompt, int? desiredCount = null, CancellationToken token = default)
    {
        var existing = Get(draftId) ?? throw new KeyNotFoundException("That discovery draft was not found.");
        var count = desiredCount.HasValue
            ? Math.Clamp(desiredCount.Value, 5, 50)
            : existing.DesiredTrackCount;
        return await GenerateAsync(existing, prompt, count, token);
    }

    public SpotifyDiscoveryDraft? Get(string draftId)
    {
        var draft = store.Get(currentUser.GetRequiredOwnerKey(), draftId);
        return draft == null ? null : NormalizeDisplayLabels(draft);
    }

    public IReadOnlyList<SpotifyDiscoveryDraft> ListSaved() =>
        store.List(currentUser.GetRequiredOwnerKey())
            .Where(draft => draft.SavedAt != null)
            .OrderByDescending(draft => draft.SavedAt)
            .Select(NormalizeDisplayLabels)
            .ToList();

    public bool Delete(string draftId) =>
        store.Delete(currentUser.GetRequiredOwnerKey(), draftId);

    public SpotifyDiscoveryDraft Update(string draftId, SpotifyDiscoveryDraftUpdateRequest request)
    {
        var ownerKey = currentUser.GetRequiredOwnerKey();
        var draft = store.Get(ownerKey, draftId) ?? throw new KeyNotFoundException("That discovery draft was not found.");
        draft = NormalizeDisplayLabels(draft);
        var removed = (request.RemoveCandidateIds ?? []).ToHashSet(StringComparer.Ordinal);
        var remaining = draft.Candidates.Where(candidate => !removed.Contains(candidate.Id)).ToList();

        if (request.CandidateSelections is { Count: > 0 } selections)
        {
            var candidateIds = remaining.Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
            if (selections.Keys.Any(id => !candidateIds.Contains(id)))
                throw new ArgumentException("A selected candidate does not belong to this draft.");

            remaining = remaining.Select(candidate =>
            {
                if (!selections.TryGetValue(candidate.Id, out var trackId)) return candidate;
                var selected = candidate.Alternatives.FirstOrDefault(track => track.Id == trackId);
                if (selected == null)
                    throw new ArgumentException("The selected Spotify match is not an alternative for that candidate.");
                return candidate with
                {
                    Resolution = SpotifyCandidateResolution.Resolved,
                    Track = selected,
                    Alternatives = []
                };
            }).ToList();
        }

        if (request.OrderedCandidateIds is { Count: > 0 } order)
        {
            var byId = remaining.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
            if (order.Count != remaining.Count || order.Any(id => !byId.ContainsKey(id)) ||
                order.Distinct(StringComparer.Ordinal).Count() != order.Count)
            {
                throw new ArgumentException("The candidate order must contain every remaining candidate exactly once.");
            }
            remaining = order.Select(id => byId[id]).ToList();
        }

        remaining = remaining.Select((candidate, position) => candidate with { Position = position }).ToList();
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? draft.Name
            : request.Name.Trim()[..Math.Min(request.Name.Trim().Length, 100)];
        var updated = draft with
        {
            Name = name,
            Candidates = remaining,
            State = remaining.Count > 0 &&
                    remaining.All(candidate => candidate.Resolution == SpotifyCandidateResolution.Resolved)
                ? SpotifyDiscoveryDraftState.Ready
                : SpotifyDiscoveryDraftState.Partial,
            UpdatedAt = timeProvider.GetUtcNow(),
            SavedAt = request.Saved switch
            {
                true => draft.SavedAt ?? timeProvider.GetUtcNow(),
                false => null,
                _ => draft.SavedAt
            }
        };
        store.Save(ownerKey, updated);
        return updated;
    }

    private async Task<SpotifyDiscoveryDraft> GenerateAsync(
        SpotifyDiscoveryDraft? existing,
        string prompt,
        int desiredCount,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("A musical theme or refinement is required.");

        var safePrompt = prompt.Trim();
        if (safePrompt.Length > 2_000) safePrompt = safePrompt[..2_000];
        var prompts = (existing?.UserPrompts ?? []).Append(safePrompt).TakeLast(12).ToList();
        var generated = await RequestCandidatesAsync(prompts, desiredCount, token);
        var now = timeProvider.GetUtcNow();
        var id = existing?.Id ?? Guid.NewGuid().ToString("N");
        var name = CleanText(generated.SuggestedName, existing?.Name ?? "Spotifinator Discovery", 100);
        var summary = CleanText(generated.Summary, string.Join(" · ", prompts), 500);
        var question = string.IsNullOrWhiteSpace(generated.ClarifyingQuestion)
            ? null
            : CleanText(generated.ClarifyingQuestion, null, 500);
        var aiCandidates = (generated.Candidates ?? [])
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Artist) && !string.IsNullOrWhiteSpace(candidate.Title))
            .DistinctBy(candidate => $"{SpotifyPlaylistResolver.Normalize(candidate.Artist)}|{SpotifyPlaylistResolver.Normalize(candidate.Title)}")
            .Take(desiredCount)
            .ToList();

        if (aiCandidates.Count == 0 && question != null)
        {
            var awaiting = new SpotifyDiscoveryDraft(
                id, SpotifyDiscoveryDraftState.AwaitingClarification, name, summary,
                prompts, desiredCount, question, [], string.Empty,
                existing?.CreatedAt ?? now, now, existing?.SavedAt);
            store.Save(currentUser.GetRequiredOwnerKey(), awaiting);
            return awaiting;
        }

        if (aiCandidates.Count == 0)
            throw new InvalidOperationException("The music discovery service returned no usable candidates.");

        var known = await knownMusic.GetAsync(token);
        var candidates = await ResolveAsync(aiCandidates, known.Index, token);
        var state = candidates.All(candidate => candidate.Resolution == SpotifyCandidateResolution.Resolved)
            ? SpotifyDiscoveryDraftState.Ready
            : SpotifyDiscoveryDraftState.Partial;
        var draft = new SpotifyDiscoveryDraft(
            id, state, name, summary, prompts, desiredCount, null, candidates,
            known.Coverage, existing?.CreatedAt ?? now, timeProvider.GetUtcNow(), existing?.SavedAt);
        store.Save(currentUser.GetRequiredOwnerKey(), draft);
        Log.Information(
            "[Spotify] Discovery draft {DraftId} generated: {Candidates} candidates, {Resolved} resolved, {Unresolved} unresolved",
            id, candidates.Count,
            candidates.Count(candidate => candidate.Resolution == SpotifyCandidateResolution.Resolved),
            candidates.Count(candidate => candidate.Resolution != SpotifyCandidateResolution.Resolved));
        return draft;
    }

    private async Task<List<SpotifyDiscoveryCandidate>> ResolveAsync(
        IReadOnlyList<SpotifyDiscoveryAiCandidate> suggestions,
        SpotifyKnownMusicIndex known,
        CancellationToken token)
    {
        var results = new SpotifyDiscoveryCandidate[suggestions.Count];
        using var gate = new SemaphoreSlim(2);
        var work = suggestions.Select(async (suggestion, position) =>
        {
            await gate.WaitAsync(token);
            try
            {
                var search = await spotify.SearchTracksAsync(
                    $"track:\"{suggestion.Title}\" artist:\"{suggestion.Artist}\"", 10, token);
                var exact = search.Tracks.Where(track => IsExact(suggestion, track)).ToList();
                var resolution = exact.Count == 1
                    ? SpotifyCandidateResolution.Resolved
                    : search.Tracks.Count == 0
                        ? SpotifyCandidateResolution.NotFound
                        : SpotifyCandidateResolution.Ambiguous;
                var track = exact.Count == 1 ? exact[0] : null;
                var trackAbsent = known.IsTrackAbsent(suggestion.Artist, suggestion.Title);
                var artistAbsent = known.IsArtistAbsent(suggestion.Artist);
                var probablyUnfamiliar = trackAbsent && artistAbsent;
                results[position] = new SpotifyDiscoveryCandidate(
                    Guid.NewGuid().ToString("N"), position,
                    suggestion.Artist.Trim(), suggestion.Title.Trim(), suggestion.Rationale?.Trim(),
                    resolution, track,
                    resolution == SpotifyCandidateResolution.Ambiguous ? search.Tracks.Take(3).ToList() : [],
                    probablyUnfamiliar,
                    FamiliarityLabel(probablyUnfamiliar));
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(work);
        return results.ToList();
    }

    private async Task<SpotifyDiscoveryAiResponse> RequestCandidatesAsync(
        IReadOnlyList<string> userPrompts,
        int desiredCount,
        CancellationToken token)
    {
        var apiKey = configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Music discovery is unavailable because the AI service is not configured.");

        var systemPrompt = $$"""
            You are a music historian and playlist curator with genuine depth across every genre.
            Work only from general musical knowledge and the user's own words. You receive no
            Spotify data. Suggest historically and musically credible artist/title pairs for an
            editable draft of {{desiredCount}} tracks.

            Two areas are your specialism, and requests touching them deserve real expertise
            rather than the obvious hits:
            - American popular music of the 1950s through the 1970s — rock and roll, doo-wop,
              Brill Building pop, rockabilly, Motown and Southern soul, blues and electric blues,
              gospel, country and countrypolitan, surf, garage, folk revival, funk, singer-
              songwriter, psychedelia, and early disco. Know the regional scenes and the labels:
              Sun, Chess, Stax, Motown, Atlantic, Muscle Shoals, Philadelphia International.
              Reach for the record that mattered, not only the one that charted highest.
            - Everything from 1990 onward, across all genres and territories.

            Outside those windows you are still knowledgeable and should answer confidently;
            simply do not claim specialist certainty you do not have.

            If one material question would substantially change the result, return that question and
            an empty candidates array. Otherwise return JSON only in exactly this shape:
            {
              "suggestedName": "short playlist name",
              "summary": "brief curatorial approach",
              "clarifyingQuestion": null,
              "candidates": [
                { "artist": "artist", "title": "song", "rationale": "brief reason" }
              ]
            }

            Preserve a purposeful sequence. Include only real songs you are reasonably confident exist.
            Do not mention Spotify, popularity scores, or claim anything about what the user has heard.
            A refinement replaces the candidate set using the complete user-request history below.
            """;
        var userPrompt = string.Join("\n", userPrompts.Select((value, index) => $"Request {index + 1}: {value}"));
        var requestBody = new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.4,
            max_tokens = 3_500,
            response_format = new { type = "json_object" }
        };

        var client = httpClientFactory.CreateClient("OpenAI");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await client.SendAsync(request, token);
        var content = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("The music discovery service could not generate candidates.");

        using var document = JsonDocument.Parse(content);

        // Discovery is the most expensive call Spotifinator makes (3,500
        // tokens), and it was recorded nowhere. Billed to the owner whose
        // library it is generating for.
        AiSpend.Record(tokenUsage, currentUser.GetRequiredOwnerKey(), document.RootElement);

        var json = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        var parsed = string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<SpotifyDiscoveryAiResponse>(AiText.StripCodeFences(json), AiOptions);
        return parsed ?? throw new InvalidOperationException("The music discovery service returned an invalid response.");
    }

    private static bool IsExact(SpotifyDiscoveryAiCandidate candidate, SpotifyTrackDto track)
    {
        if (SpotifyPlaylistResolver.Normalize(candidate.Title) != SpotifyPlaylistResolver.Normalize(track.Name))
            return false;
        var expectedArtist = SpotifyPlaylistResolver.Normalize(candidate.Artist);
        return track.Artists.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SpotifyPlaylistResolver.Normalize)
            .Any(artist => artist == expectedArtist);
    }

    private static string CleanText(string? value, string? fallback, int maxLength)
    {
        var result = string.IsNullOrWhiteSpace(value) ? fallback ?? string.Empty : value.Trim();
        return result[..Math.Min(result.Length, maxLength)];
    }

    private static SpotifyDiscoveryDraft NormalizeDisplayLabels(SpotifyDiscoveryDraft draft) =>
        draft with
        {
            Candidates = draft.Candidates
                .Select(candidate => candidate with
                {
                    FamiliarityLabel = FamiliarityLabel(candidate.ProbablyUnfamiliar)
                })
                .ToList()
        };

    private static string FamiliarityLabel(bool probablyUnfamiliar) =>
        probablyUnfamiliar
            ? "Probably unfamiliar — absent from your accessible library and listening evidence"
            : "Found in your accessible library or listening evidence";
}
