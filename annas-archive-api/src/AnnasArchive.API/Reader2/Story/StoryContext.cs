using System.Text;
using AnnasArchive.API.Reader2.Lenses;

namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// The story so far, as material for a chapter summary.
///
/// <para>This is the model paying its way back (spec Phase 9): "Who appears"
/// wants <i>how long somebody has been absent</i>, and "Threads running in
/// parallel" wants <i>what is happening elsewhere</i> — and a model summarising
/// chapter 74 knows neither, because it has never seen chapter 61. The running
/// record has, so a compact slice of it rides along with the chapter text.</para>
///
/// <para><b>Built only from a position-filtered model.</b> The caller passes a
/// model already cut to the reader's chapter; nothing here reaches past it, so a
/// summary can remind but never spoil. Deliberately the last piece built — it is
/// the only place summary quality depends on model correctness, and every rule
/// the merge enforces is load-bearing under it.</para>
/// </summary>
public static class StoryContext
{
    /// <summary>
    /// The context block for a summary of <paramref name="chapter"/>, or null
    /// when the record holds nothing worth saying.
    /// </summary>
    /// <param name="model">Already filtered through <paramref name="chapter"/>.</param>
    /// <param name="maxActors">
    /// The digest cap, reused: the same people worth reminding a model about are
    /// the people worth telling it exist.
    /// </param>
    public static string? Build(
        StoryModel model, StoryVocabulary vocabulary, int chapter, int maxActors, int recentChapters)
    {
        var text = new StringBuilder();

        People(text, model, vocabulary, chapter, maxActors, recentChapters);
        Threads(text, model, vocabulary, chapter);

        if (text.Length == 0) return null;

        return
            $"""
             ## The story so far, from the running record

             Use this to remind the reader who people are, how long anyone has been
             absent, and what runs in parallel. It is a record of earlier chapters,
             not part of this one — report nothing from it as happening now.

             {text.ToString().TrimEnd()}
             """;
    }

    private static void People(
        StringBuilder text, StoryModel model, StoryVocabulary vocabulary,
        int chapter, int maxActors, int recentChapters)
    {
        var kept = StoryDigest.Keep(model.Actors, chapter, maxActors, recentChapters);
        if (kept.Count == 0) return;

        text.Append(vocabulary.Actors).Append(", and when each was last seen:\n");

        foreach (var actor in kept)
        {
            var role = actor.Role.Length > 0 ? $" — {actor.Role}" : "";

            text.Append($"- {actor.CanonicalName}{role}; last seen in chapter {actor.LastSeenChapter + 1}\n");
        }

        text.Append('\n');
    }

    /// <summary>
    /// Running and quiet threads only. A resolved thread is finished — reminding
    /// a summary of it would invite "meanwhile" sentences about things that are
    /// over.
    /// </summary>
    private static void Threads(
        StringBuilder text, StoryModel model, StoryVocabulary vocabulary, int chapter)
    {
        var open = model.Threads
            .Where(t => t.Status is ThreadStatus.Active or ThreadStatus.Dormant)
            .ToArray();

        if (open.Length == 0) return;

        text.Append(vocabulary.Threads).Append(" still open elsewhere in the story:\n");

        foreach (var thread in open)
            text.Append(thread.Status == ThreadStatus.Dormant
                ? $"- {thread.Name} — nothing since chapter {thread.LastAdvancedChapter + 1}\n"
                : $"- {thread.Name} — last moved in chapter {thread.LastAdvancedChapter + 1}\n");

        text.Append('\n');
    }
}
