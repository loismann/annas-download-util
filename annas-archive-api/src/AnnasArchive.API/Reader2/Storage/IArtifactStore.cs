using AnnasArchive.API.Reader2.Domain;

namespace AnnasArchive.API.Reader2.Storage;

/// <summary>The versions the running build considers current, for one artifact kind.</summary>
/// <param name="Schema">Shape of <c>content_json</c>.</param>
/// <param name="Prompt">The prompt that produced it; 0 for computed artifacts.</param>
public readonly record struct ArtifactVersions(int Schema, int Prompt)
{
    /// <summary>For artifacts no model produced.</summary>
    public static ArtifactVersions Computed(int schema) => new(schema, 0);
}

/// <summary>
/// The single read/write path for everything Reader II generates.
///
/// <para>Reader I had twelve of these — one per cache directory, each with its
/// own path builder, its own JSON handling, and its own idea of what a cache hit
/// meant. Reader II has this.</para>
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Reads an artifact, applying the version gates.
    ///
    /// <list type="bullet">
    /// <item><description><b>Stale prompt</b> — a miss. The content is valid but
    /// an older prompt wrote it, so the caller regenerates and overwrites.</description></item>
    /// <item><description><b>Stale schema</b> — a miss, <i>and the row is
    /// deleted</i>. It can no longer be deserialised into the current record, so
    /// keeping it only invites a crash on the next read. There is deliberately no
    /// upcasting: with no migration anywhere else in this design, adding one here
    /// would be the compatibility shim the rebuild exists to avoid.</description></item>
    /// <item><description><b>Newer than current</b> — never deleted. A rollback
    /// must not destroy work a newer build produced. It is served if it still
    /// deserialises, and treated as a miss if it does not.</description></item>
    /// </list>
    /// </summary>
    Task<Stored<T>?> GetAsync<T>(ArtifactKey key, ArtifactVersions current, CancellationToken ct = default);

    /// <summary>
    /// Writes an artifact, replacing any existing one for the same key.
    ///
    /// <para>Will not overwrite a row written by a <i>newer</i> schema — the
    /// other half of the rollback promise, since a gate that refuses to delete
    /// newer work but lets the next write clobber it protects nothing.</para>
    /// </summary>
    Task PutAsync<T>(ArtifactKey key, T content, ArtifactProvenance provenance, CancellationToken ct = default);

    /// <summary>
    /// Every artifact of one kind for one book and lens, in ordinal order —
    /// the section summaries of a chapter, the passage analyses before an offset.
    /// Applies the same read gates as <see cref="GetAsync{T}"/> but deletes nothing.
    /// </summary>
    Task<IReadOnlyList<Stored<T>>> ListAsync<T>(
        ArtifactQuery query, ArtifactVersions current, CancellationToken ct = default);

    /// <summary>Deletes every artifact for a book. Returns the row count.</summary>
    Task<int> DeleteForBookAsync(BookRef book, CancellationToken ct = default);

    /// <summary>
    /// Deletes rows below a prompt version — the bulk companion to the per-read
    /// gate, for reclaiming space after a prompt change.
    /// </summary>
    Task<int> DeleteStaleAsync(
        BookRef book, string lensKey, int belowPromptVersion, CancellationToken ct = default);
}

/// <summary>
/// Which artifacts to list. A record rather than five parameters because
/// <see cref="Chapter"/> is genuinely optional and a null means "every chapter",
/// which is not something a sentinel int can say clearly.
/// </summary>
public sealed record ArtifactQuery(BookRef Book, string LensKey, ArtifactKind Kind, int? Chapter = null);
