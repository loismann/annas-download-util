using AnnasArchive.API.Helpers;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Storage;
using AnnasArchive.Core.Services;

namespace AnnasArchive.API.Reader2.Ai;

/// <summary>The reader's allowance is spent; the response to send instead.</summary>
public sealed class TokenAllowanceException(IResult gateResponse)
    : Exception("The reader's monthly AI allowance is exhausted.")
{
    public IResult GateResponse { get; } = gateResponse;
}

/// <summary>
/// What a generator produced and what it cost. The versions are deliberately
/// absent: those belong to the gateway, which is the only thing that knows both
/// the schema of the record and whose prompt wrote it.
/// </summary>
public sealed record Produced<T>(T Content, string Model, int PromptTokens = 0, int CompletionTokens = 0);

/// <summary>
/// The one path from "the reader asked for this" to "here it is".
///
/// <para>Reader I copy-pasted check-cache / lock / generate / save across three
/// endpoint files, and the copies disagreed — which is how two tabs could bill
/// the same chapter summary twice. There is one of it here, and everything that
/// stores an artifact goes through it.</para>
///
/// <para><b>The order is the design.</b> Cache first, so a hit costs nothing and
/// never takes a lock. Lock second, so the loser of a race re-reads the cache the
/// winner just filled instead of paying again. Token gate third — after the
/// cache, because a reader at their limit should still be served work already
/// paid for, and <i>before</i> any streaming, because a gate response cannot be
/// sent once SSE headers have gone out. Persist last, so a failure or a
/// disconnect leaves no partial row.</para>
/// </summary>
public sealed class ArtifactGateway(
    IArtifactStore artifacts,
    KeyedLocks locks,
    ITokenUsageService tokenUsage,
    IConfiguration configuration)
{
    /// <summary>
    /// Returns the stored artifact, or generates one with a model, bills for it,
    /// and persists it.
    /// </summary>
    /// <param name="promptVersion">
    /// Whose wording produced this — the lens's version for a lens-supplied
    /// prompt, <see cref="Lenses.SharedPrompts.Version"/> for one no lens owns.
    /// It is what makes an artifact written under older wording a cache miss.
    /// </param>
    /// <param name="force">
    /// Skips the cache <i>read</i> and overwrites. It does not skip the lock or
    /// the token gate: a regenerate is a purchase like any other.
    /// </param>
    /// <exception cref="TokenAllowanceException">
    /// Thrown rather than returned, so no caller can start generating by
    /// forgetting to inspect a result — and thrown before a token is spent.
    /// </exception>
    public Task<T> GetOrGenerateAsync<T>(
        ArtifactKey key,
        ReaderContext ctx,
        int promptVersion,
        Func<CancellationToken, Task<Produced<T>>> generate,
        bool force = false,
        CancellationToken ct = default)
        where T : IVersionedArtifact<T> =>
        ResolveAsync(
            key, new ArtifactVersions(T.SchemaVersion, promptVersion), ctx, force,
            async token =>
            {
                var made = await generate(token);

                return (made.Content, new ArtifactProvenance(
                    T.SchemaVersion, promptVersion, made.Model,
                    made.PromptTokens, made.CompletionTokens));
            },
            ct);

    /// <summary>
    /// Returns the stored artifact, or works one out locally.
    ///
    /// <para>Separate from <see cref="GetOrGenerateAsync"/> rather than a flag,
    /// because a computed artifact must never touch the token gate: chunk
    /// boundaries are paragraph arithmetic, and a reader at their spending limit
    /// still gets to open a chapter. Having no path from here to a model is what
    /// guarantees that.</para>
    /// </summary>
    public Task<T> GetOrComputeAsync<T>(
        ArtifactKey key,
        ReaderContext ctx,
        Func<CancellationToken, Task<T>> compute,
        bool force = false,
        CancellationToken ct = default)
        where T : IVersionedArtifact<T> =>
        ResolveAsync(
            key, ArtifactVersions.Computed(T.SchemaVersion), ctx: null, force,
            async token => (await compute(token), ArtifactProvenance.Computed(T.SchemaVersion)),
            ct);

    /// <summary>
    /// Rewrites a stored artifact under its own lock, with no model and no gate.
    ///
    /// <para>For a read-modify-write of something a reader already owns — answering
    /// one of the story model's questions is the whole of it today. It takes the
    /// lock because it is the same single row an ingest touches, and skipping it
    /// would lose whichever of the two finished second.</para>
    ///
    /// <para><b>Why neither of the other two.</b> <see cref="GetOrGenerateAsync"/>
    /// gates on the allowance, and refusing to let somebody tidy their own cast
    /// list because their spending is exhausted is a purchase that never happens
    /// charged to a reader who cannot make one. <see cref="GetOrComputeAsync"/>
    /// writes computed provenance, which would erase which prompt version wrote
    /// the model and take the explicit rebuild with it.</para>
    /// </summary>
    public Task<T> ReviseAsync<T>(
        ArtifactKey key,
        int promptVersion,
        Func<CancellationToken, Task<T>> revise,
        CancellationToken ct = default)
        where T : IVersionedArtifact<T> =>
        ResolveAsync(
            key, new ArtifactVersions(T.SchemaVersion, promptVersion), ctx: null, force: true,
            async token => (
                await revise(token),
                new ArtifactProvenance(T.SchemaVersion, promptVersion, Model: "none")),
            ct);

    /// <summary>
    /// The gate response if this reader cannot spend, or null if they can.
    ///
    /// <para>For the one caller that has to ask <i>before</i> it starts: an SSE
    /// route cannot answer 429 once its headers have gone out, so it checks here
    /// first. The check inside <see cref="GetOrGenerateAsync"/> stays — this is a
    /// pre-flight, not a replacement, and a non-streaming route needs no such
    /// warning.</para>
    /// </summary>
    public IResult? Refusal(ReaderContext ctx) =>
        TokenLimitHelpers.CheckTokenLimit(configuration, tokenUsage, ctx.Http);

    /// <summary>The stored artifact if a current one exists, without generating.</summary>
    public async Task<T?> PeekAsync<T>(ArtifactKey key, int promptVersion, CancellationToken ct = default)
        where T : class, IVersionedArtifact<T> =>
        (await artifacts.GetAsync<T>(key, new ArtifactVersions(T.SchemaVersion, promptVersion), ct))?.Content;

    /// <param name="ctx">Null for computed artifacts, which never reach the gate.</param>
    private async Task<T> ResolveAsync<T>(
        ArtifactKey key,
        ArtifactVersions versions,
        ReaderContext? ctx,
        bool force,
        Func<CancellationToken, Task<(T Content, ArtifactProvenance Provenance)>> produce,
        CancellationToken ct)
    {
        if (!force && await artifacts.GetAsync<T>(key, versions, ct) is { } cached)
            return cached.Content;

        using var _ = await locks.AcquireAsync(key.ToString(), ct);

        // Re-check under the lock. Without this, the second of two concurrent
        // requests pays again for what the first has just finished.
        if (!force && await artifacts.GetAsync<T>(key, versions, ct) is { } filled)
            return filled.Content;

        if (ctx is not null &&
            TokenLimitHelpers.CheckTokenLimit(configuration, tokenUsage, ctx.Http) is { } gate)
            throw new TokenAllowanceException(gate);

        var (content, provenance) = await produce(ct);
        await artifacts.PutAsync(key, content, provenance, ct);

        return content;
    }
}
