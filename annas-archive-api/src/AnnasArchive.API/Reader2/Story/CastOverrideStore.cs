using AnnasArchive.API.Reader2.Ai;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Storage;

namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// The reader's own corrections to a book's cast, and the only thing that writes
/// them.
///
/// <para><b>A separate artifact from the model it corrects, and a separate class
/// from the service that reads it.</b> The story model is the model's account of
/// the book; this is the reader's. Keeping them apart is what makes "your
/// corrections outlive a rebuild" true by construction rather than by a step
/// somebody has to remember to take — and giving this file the whole of the
/// writing is what stops a second path to the same rows growing later.</para>
///
/// <para>Read with no version gate at all. This is somebody's typing, not
/// generated output, and no version of anything may discard it.</para>
/// </summary>
public sealed class CastOverrideStore(ArtifactGateway gateway, IArtifactStore artifacts)
{
    public async Task<CastOverrides> ReadAsync(ReaderContext ctx, CancellationToken ct = default) =>
        (await artifacts.GetAsync<CastOverrides>(
            ArtifactKey.CastOverrides(ctx.Ref, ctx.Lens.Key),
            new ArtifactVersions(CastOverrides.SchemaVersion, Prompt: 0), ct))?.Content
        ?? CastOverrides.Empty;

    /// <summary>
    /// Saves one correction whole, so clearing every field undoes the edit.
    ///
    /// <para>Whether they are hidden is carried over rather than replaced: the
    /// edit form does not offer hiding, and a form that does not offer something
    /// has no business clearing it.</para>
    /// </summary>
    public Task<CastOverrides> SaveAsync(
        ReaderContext ctx, CastOverride correction, CancellationToken ct = default) =>
        ReviseAsync(ctx, current =>
            current.With(correction with { Hidden = current.For(correction.NameKey)?.Hidden ?? false }),
            ct);

    /// <summary>
    /// Hides somebody from the map, or puts them back.
    ///
    /// <para><b>Merged into whatever is already stored, not written over it.</b>
    /// Hiding is one press from a panel, and a client that had to resend the rest
    /// of the correction would have to know what the rest <i>is</i>. It cannot: a
    /// preferred name is projected onto the canonical one, so nothing served back
    /// distinguishes a name the reader chose from a name the model did. Sending
    /// its best guess would have pinned a display name on everybody with an alias
    /// the first time they were hidden.</para>
    /// </summary>
    public Task<CastOverrides> HideAsync(
        ReaderContext ctx, string nameKey, bool hidden, CancellationToken ct = default) =>
        ReviseAsync(ctx, current =>
            current.With((current.For(nameKey) ?? new CastOverride(nameKey)) with { Hidden = hidden }),
            ct);

    /// <summary>
    /// Read, change, write — under the gateway's keyed lock.
    ///
    /// <para><see cref="ArtifactGateway.ReviseAsync"/> rather than a plain write,
    /// for the same reason ingesting takes the lock: the whole set is one row, so
    /// two corrections saved at once would both read it and the second would erase
    /// the first. Un-gated and unbilled — nothing here reaches a model.</para>
    /// </summary>
    private Task<CastOverrides> ReviseAsync(
        ReaderContext ctx, Func<CastOverrides, CastOverrides> change, CancellationToken ct) =>
        gateway.ReviseAsync(
            ArtifactKey.CastOverrides(ctx.Ref, ctx.Lens.Key), promptVersion: 0,
            async token => change(await ReadAsync(ctx, token)), ct);
}
