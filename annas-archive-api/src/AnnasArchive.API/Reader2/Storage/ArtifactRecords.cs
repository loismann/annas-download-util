namespace AnnasArchive.API.Reader2.Storage;

/// <summary>
/// How an artifact came to exist. Recorded on every row so "why does this
/// summary look like that" is answerable from the database alone — the question
/// Reader I cannot answer at all, because it stores no version and no model.
/// </summary>
/// <param name="SchemaVersion">
/// The shape of <c>content_json</c>. Bumped when the record it deserialises into
/// changes incompatibly; an older row is then unreadable and is discarded rather
/// than upcast.
/// </param>
/// <param name="PromptVersion">
/// The prompt that produced it. Bumped whenever prompt text changes, which is
/// what turns "this summary predates the current prompt" from invisible into a
/// cache miss.
/// </param>
public sealed record ArtifactProvenance(
    int SchemaVersion,
    int PromptVersion,
    string Model,
    int PromptTokens = 0,
    int CompletionTokens = 0)
{
    /// <summary>
    /// For artifacts no model produced — the chapter index, section boundaries.
    /// They still carry a schema version, because their shape can still change.
    /// </summary>
    public static ArtifactProvenance Computed(int schemaVersion) =>
        new(schemaVersion, PromptVersion: 0, Model: "none");
}

/// <summary>An artifact read back from the store, with its provenance.</summary>
public sealed record Stored<T>(
    ArtifactKey Key,
    T Content,
    ArtifactProvenance Provenance,
    DateTime CreatedAtUtc);

/// <summary>An artifact just produced, ready to be written.</summary>
public sealed record Generated<T>(T Content, ArtifactProvenance Provenance);
