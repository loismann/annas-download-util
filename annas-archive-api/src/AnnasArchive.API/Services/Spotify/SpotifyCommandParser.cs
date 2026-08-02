using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Helpers;
using Serilog;

namespace AnnasArchive.API.Services.Spotify;

public interface ISpotifyCommandParser
{
    Task<SpotifyValidatedCommand> ParseAsync(
        string message, string? conversationContext = null, CancellationToken token = default);
}

/// <summary>
/// Classifies what the user asked for. That is the model's entire job here.
///
/// It receives the user's own words and the action catalog — never playlist names,
/// track titles, IDs, counts, or anything else Spotify returned. That boundary is
/// Spotify's policy on feeding their content to AI systems, and it also happens to
/// make the system honest: the model cannot report a fact it was never shown.
/// </summary>
public sealed class SpotifyCommandParser : ISpotifyCommandParser
{
    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // Rejects properties the envelope does not declare, so a model that starts
        // emitting extra fields fails loudly here instead of having them ignored.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public SpotifyCommandParser(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public async Task<SpotifyValidatedCommand> ParseAsync(
        string message,
        string? conversationContext = null,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return Unresolved("What would you like to know?");

        // Capability questions are answered from the catalog, so "what can you do?"
        // costs nothing and cannot be misclassified into a Spotify call.
        if (LooksLikeCapabilityQuestion(message))
            return new SpotifyValidatedCommand(SpotifyReadAction.ExplainCapability, new SpotifyCommandArguments(), 1.0);

        var apiKey = _configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Log.Warning("[Spotify] OpenAI:ApiKey is not configured; command parsing is unavailable");
            return Unresolved("I cannot interpret requests right now — the AI service is not configured.");
        }

        try
        {
            var envelope = await RequestEnvelopeAsync(message, conversationContext, apiKey, token);
            var validated = SpotifyActionCatalog.Validate(envelope);

            Log.Information(
                "[Spotify] Parsed intent {Action} (confidence {Confidence:0.00})",
                SpotifyActionCatalog.WireNameOf(validated.Action),
                validated.Confidence);

            return validated;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[Spotify] Command parsing failed");
            return Unresolved("I could not interpret that just now. Try rephrasing it?");
        }
    }

    private async Task<SpotifyCommandEnvelope?> RequestEnvelopeAsync(
        string message,
        string? conversationContext,
        string apiKey,
        CancellationToken token)
    {
        // $$ raw string: {{ }} interpolates, single braces are literal JSON.
        var systemPrompt = $$"""
            You classify a user's request about their own Spotify account into one action.

            Available actions:
            {{SpotifyActionCatalog.PromptActionList()}}

            Respond with JSON only, no markdown, in exactly this shape:
            {
              "schemaVersion": {{SpotifyActionCatalog.SchemaVersion}},
              "action": "<one action name from the list above>",
              "arguments": {
                "query": "<search or filter text, when the action needs one>",
                "playlistReference": "<the playlist name exactly as the user wrote it>",
                "playlistReferences": ["<one entry per playlist name, when they listed several>"],
                "removeSources": <true only when merging and they said to get rid of the originals>,
                "limit": <number of results, only if the user asked for a specific count>
              },
              "confidence": <0.0 to 1.0>,
              "clarification": "<a question to ask, only when you cannot pick an action>"
            }

            Rules:
            - Use only the action names listed. If none fit, use "unknown" and set clarification.
            - Never invent a playlist name. Copy what the user wrote; the server resolves it.
            - Never output Spotify IDs, URIs, track counts, or ownership. You do not have them.
            - Never choose which playlists a change should affect. If the user did not name
              them, leave the list empty — "clean up whatever you think is best" is not a
              request you may resolve into targets.
            - Actions beginning with "plan_" do not change anything. They produce a proposal
              the user reviews and confirms separately, so classify the intent honestly rather
              than avoiding it.
            - Spotify cannot delete a playlist for everyone. "Delete this playlist" means
              removing it from this user's own library; use
              "plan_remove_playlists_from_library".
            - Use "suggest_music" for a new theme/history/curation request. When the context
              says a discovery draft is active, use "refine_music_draft" for changes to it.
            - Omit any argument that does not apply.
            """;

        var userPrompt = string.IsNullOrWhiteSpace(conversationContext)
            ? message
            : $"Earlier in this conversation:\n{conversationContext}\n\nNew message: {message}";

        var requestBody = new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.0,
            max_tokens = 300,
            response_format = new { type = "json_object" }
        };

        var client = _httpClientFactory.CreateClient("OpenAI");
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client.SendAsync(request, token);
        var content = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("[Spotify] OpenAI returned {StatusCode} while parsing a command", response.StatusCode);
            return null;
        }

        using var document = JsonDocument.Parse(content);
        var messageContent = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(messageContent))
            return null;

        return JsonSerializer.Deserialize<SpotifyCommandEnvelope>(
            AiText.StripCodeFences(messageContent), EnvelopeOptions);
    }

    private static readonly string[] CapabilityPhrases =
    [
        "what can you do", "what can i ask", "what are you able", "can you answer",
        "help me", "what do you do", "how do you work", "what commands"
    ];

    private static bool LooksLikeCapabilityQuestion(string message)
    {
        var normalized = message.Trim().ToLowerInvariant();
        return CapabilityPhrases.Any(phrase => normalized.Contains(phrase, StringComparison.Ordinal));
    }

    private static SpotifyValidatedCommand Unresolved(string clarification) =>
        new(SpotifyReadAction.Unknown, new SpotifyCommandArguments(), 0d, clarification);
}
