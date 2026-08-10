namespace AnnasArchive.API.Reader2.Ai;

/// <summary>
/// A record that knows its own storage schema version.
///
/// <para>A static abstract rather than a parameter, because the version and the
/// shape must change together: passing it in makes "read with version 1, write
/// with version 2" a thing a caller can do by accident, and the symptom is a row
/// that deserialises into the wrong shape months later.</para>
/// </summary>
public interface IVersionedArtifact<TSelf> where TSelf : IVersionedArtifact<TSelf>
{
    static abstract int SchemaVersion { get; }
}

/// <summary>
/// Everything the model writes for a reader is Markdown.
///
/// <para>One shape for six call kinds rather than six near-identical records:
/// the tier, the lens, and the chapter are all in the artifact <i>key</i>, so
/// putting them in the payload too would be storing the same fact twice and
/// giving it two chances to disagree.</para>
/// </summary>
public sealed record Prose(string Markdown) : IVersionedArtifact<Prose>
{
    public static int SchemaVersion => 1;
}

/// <summary>
/// Chapter titles as the model tidied them, one per chapter in reading order.
/// Lens-independent — tidying a heading is the same job for every book type.
/// </summary>
public sealed record ChapterLabels(IReadOnlyList<string> Titles) : IVersionedArtifact<ChapterLabels>
{
    public static int SchemaVersion => 1;
}
