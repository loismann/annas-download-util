namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// The reader's corrections, laid over the model's own account.
///
/// <para><b>A projection, not an edit.</b> The stored model keeps what the
/// extraction actually found; what anybody reads is that plus this. Three things
/// follow, and all three are the reason it was built this way: a rebuild cannot
/// destroy a correction, every correction is undone by deleting it, and the
/// record never quietly becomes a thing the model never said.</para>
///
/// <para>Pure, like the merge. Corrections are resolved by name through
/// <see cref="NameMatch"/> — the same rule the merger uses to decide two names
/// are one person — so a correction made before a rebuild lands on the same
/// character after one, whatever id they were given the second time.</para>
/// </summary>
public static class CastCorrections
{
    /// <summary>The model as the reader has corrected it.</summary>
    public static StoryModel Apply(StoryModel model, CastOverrides overrides)
    {
        if (overrides.Entries.Count == 0) return model;

        var corrected = overrides.Entries.Aggregate(model, Rename);

        return overrides.Entries.Aggregate(corrected, Fuse);
    }

    /// <summary>
    /// A preferred name and a note, applied to whoever answers to the key.
    ///
    /// <para>The name the model chose becomes an alias rather than being thrown
    /// away: it is still the name the book uses, still what a later chapter will
    /// call them, and still how this very correction finds them again.</para>
    /// </summary>
    private static StoryModel Rename(StoryModel model, CastOverride entry)
    {
        if (Find(model, entry.NameKey) is not { } actor) return model;

        var renamed = actor with
        {
            CanonicalName = string.IsNullOrWhiteSpace(entry.PreferredName)
                ? actor.CanonicalName
                : entry.PreferredName.Trim(),
            ReaderNote = entry.Note?.Trim() ?? "",
            Hidden = entry.Hidden
        };

        if (renamed.CanonicalName != actor.CanonicalName)
            renamed = renamed with { Aliases = MergeLists.Names(actor.Aliases, [actor.CanonicalName]) };

        return model with { Actors = [.. model.Actors.Select(a => a.Id == actor.Id ? renamed : a)] };
    }

    /// <summary>
    /// "These two are the same person", from the reader rather than the model.
    ///
    /// <para>The same fuse the merger's own questions produce, which is the point:
    /// this is not a second notion of what fusing means, it is the reader
    /// supplying the certainty the model did not have.</para>
    /// </summary>
    private static StoryModel Fuse(StoryModel model, CastOverride entry)
    {
        if (Find(model, entry.NameKey) is not { } keep) return model;

        return entry.Fused.Aggregate(model, (current, key) =>
            Find(current, key) is { } gone && gone.Id != keep.Id
                ? MergeResolution.Fuse(current, keep.Id, gone.Id)
                : current);
    }

    /// <summary>
    /// Whoever answers to this name, or nobody.
    ///
    /// <para>Two actors answering to one name resolves to neither, on the same
    /// rule as every other reference in the merge: a correction applied to the
    /// wrong one of two people is a wrong record the reader cannot see, and the
    /// worst thing a correction could possibly do.</para>
    /// </summary>
    private static Actor? Find(StoryModel model, string nameKey)
    {
        var answering = model.Actors
            .Where(a => a.AllNames.Any(n => NameMatch.Key(n) == nameKey))
            .ToArray();

        return answering.Length == 1 ? answering[0] : null;
    }
}
