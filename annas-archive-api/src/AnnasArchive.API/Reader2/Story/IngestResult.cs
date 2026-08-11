namespace AnnasArchive.API.Reader2.Story;

/// <summary>Why one chapter's ingest did nothing, or null when it did something.</summary>
public enum IngestSkip
{
    /// <summary>Already folded in. The idempotency that makes a back-fill resumable.</summary>
    AlreadyIngested,

    /// <summary>No chapter summary to extract from. Ingest never summarises.</summary>
    NoSummary,

    /// <summary>This book's type does not accumulate a story model.</summary>
    NotAStoryLens,

    /// <summary>
    /// The extraction answered with something that could not be read.
    ///
    /// <para>The odd one out: the other three cost nothing and are decided before
    /// any model is asked, and this one is discovered after the household has
    /// already paid. The chapter is deliberately left un-ingested so it can be
    /// tried again — see the note on truncation in <c>ExtractAsync</c>.</para>
    /// </summary>
    Unreadable
}

/// <summary>
/// What one chapter's ingest did, and the model as it stands afterwards.
///
/// <para>Apart from <see cref="StoryModelService"/> because it is the shape the
/// callers read rather than part of the machinery: the summary route reports it to
/// the reader, and the back-fill counts it.</para>
/// </summary>
public sealed record IngestResult(StoryModel Model, IngestSkip? Skipped)
{
    public bool DidWork => Skipped is null;
}
