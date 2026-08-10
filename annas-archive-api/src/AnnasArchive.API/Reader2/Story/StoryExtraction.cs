using System.Text.Json;
using AnnasArchive.API.Reader2.Ai;

namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// Reads what the extraction call answered into a <see cref="StoryDelta"/>.
///
/// <para><b>Lenient by design, in one direction only.</b> A missing key, an
/// unknown tier, a confidence written as a word or as a number, a fenced code
/// block around the JSON — all survive, because the alternative is losing a whole
/// chapter's extraction over punctuation. But every value it cannot read falls to
/// the <i>cautious</i> side: an unreadable confidence is <see cref="AliasConfidence.Low"/>,
/// which sends the hint to the reader as a question instead of merging it. Being
/// generous about what arrives must never be generous about what is believed.</para>
/// </summary>
public static class StoryExtraction
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>The delta the model reported for <paramref name="chapter"/>.</summary>
    /// <exception cref="ReaderAiException">The answer was not JSON at all.</exception>
    public static StoryDelta Parse(string answer, int chapter)
    {
        using var document = JsonDocument.Parse(Unfence(answer));
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new ReaderAiException("Story extraction did not answer with a JSON object.");

        return new StoryDelta(
            chapter,
            Each(root, "newActors", ReadActor),
            Each(root, "actorUpdates", ReadUpdate),
            Each(root, "aliasHints", ReadHint),
            Each(root, "newGroups", ReadGroup),
            Each(root, "groupUpdates", ReadGroupUpdate),
            Each(root, "edgeChanges", ReadEdge),
            Each(root, "newThreads", ReadThread),
            Each(root, "threadBeats", ReadBeat));
    }

    /// <summary>
    /// True when the answer could be read at all. Used by the ingest path to tell
    /// a bad answer from a bad request.
    /// </summary>
    public static bool TryParse(string answer, int chapter, out StoryDelta delta)
    {
        try
        {
            delta = Parse(answer, chapter);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ReaderAiException)
        {
            delta = StoryDelta.Empty(chapter);
            return false;
        }
    }

    private static NewActor ReadActor(JsonElement e) => new(
        Text(e, "canonicalName", "name"),
        Names(e, "aliases"),
        Tier(Text(e, "tier")),
        Names(e, "groupIds"),
        Text(e, "role"),
        Text(e, "dossier"),
        Text(e, "status"),
        Text(e, "arcChange", "arc"));

    private static ActorUpdate ReadUpdate(JsonElement e) => new(
        Text(e, "actorId", "id"),
        Has(e, "tier") ? Tier(Text(e, "tier")) : null,
        Optional(e, "role"),
        Optional(e, "dossier"),
        Optional(e, "status"),
        Optional(e, "arcChange", "arc"),
        Has(e, "groupIds") ? Names(e, "groupIds") : null,
        Has(e, "aliases") ? Names(e, "aliases") : null);

    private static AliasHint ReadHint(JsonElement e) => new(
        Text(e, "alias", "name"), Text(e, "actorId", "id"), Confidence(e));

    private static NewGroup ReadGroup(JsonElement e) => new(
        Text(e, "name"), Kind(Text(e, "kind")), Names(e, "memberIds"), Names(e, "rivalGroupIds"));

    private static GroupUpdate ReadGroupUpdate(JsonElement e) => new(
        Text(e, "groupId", "id"),
        Has(e, "memberIds") ? Names(e, "memberIds") : null,
        Has(e, "rivalGroupIds") ? Names(e, "rivalGroupIds") : null);

    private static EdgeChange ReadEdge(JsonElement e) => new(
        Text(e, "from"), Text(e, "to"), Text(e, "type"), Text(e, "note"),
        Has(e, "ended") && e.GetProperty("ended").ValueKind == JsonValueKind.True);

    private static NewThread ReadThread(JsonElement e) => new(
        Text(e, "name"), Names(e, "participantIds"), Text(e, "firstBeat", "whatMoved", "beat"));

    private static ThreadBeat ReadBeat(JsonElement e) => new(
        Text(e, "threadId", "id"), Text(e, "whatMoved", "beat"));

    // ─── reading values ─────────────────────────────────────────────────

    /// <summary>
    /// Every readable entry of an array property. One bad entry is skipped rather
    /// than failing the chapter — thirty good actors are worth more than a clean
    /// error about the thirty-first.
    /// </summary>
    private static IReadOnlyList<T> Each<T>(JsonElement root, string name, Func<JsonElement, T> read)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return [];

        var items = new List<T>();

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;

            try { items.Add(read(element)); }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { }
        }

        return items;
    }

    private static bool Has(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;

    private static string Text(JsonElement e, params string[] names) => Optional(e, names) ?? "";

    private static string? Optional(JsonElement e, params string[] names)
    {
        foreach (var name in names)
            if (e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString()?.Trim() is { Length: > 0 } text ? text : null;

        return null;
    }

    private static IReadOnlyList<string> Names(JsonElement e, string name) =>
        e.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!.Trim())
                .Where(v => v.Length > 0)]
            : [];

    /// <summary>An unreadable tier is <c>mentioned</c> — the tier that claims least.</summary>
    private static ActorTier Tier(string text) =>
        Enum.TryParse<ActorTier>(text, ignoreCase: true, out var tier) ? tier : ActorTier.Mentioned;

    private static GroupKind Kind(string text) =>
        Enum.TryParse<GroupKind>(text.Replace("-", "").Replace(" ", ""), ignoreCase: true, out var kind)
            ? kind
            : GroupKind.Other;

    /// <summary>
    /// Confidence as a word or as a number, and <see cref="AliasConfidence.Low"/>
    /// when it is neither.
    ///
    /// <para>The thresholds are deliberately high. This is the one value that
    /// decides whether two names are merged without anybody looking, so a model
    /// hedging at 0.8 is asked rather than believed.</para>
    /// </summary>
    private static AliasConfidence Confidence(JsonElement e)
    {
        if (!e.TryGetProperty("confidence", out var value)) return AliasConfidence.Low;

        if (value.ValueKind == JsonValueKind.Number)
            return value.GetDouble() switch
            {
                >= 0.9 => AliasConfidence.High,
                >= 0.6 => AliasConfidence.Medium,
                _ => AliasConfidence.Low
            };

        return value.ValueKind == JsonValueKind.String
               && Enum.TryParse<AliasConfidence>(value.GetString(), ignoreCase: true, out var parsed)
            ? parsed
            : AliasConfidence.Low;
    }

    /// <summary>Strips a Markdown code fence, which models add however firmly asked not to.</summary>
    private static string Unfence(string answer)
    {
        var text = answer.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

        var firstBreak = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);

        return firstBreak > 0 && lastFence > firstBreak ? text[(firstBreak + 1)..lastFence].Trim() : text;
    }
}
