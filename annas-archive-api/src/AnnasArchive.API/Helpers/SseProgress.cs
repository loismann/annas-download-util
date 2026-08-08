namespace AnnasArchive.API.Helpers;

/// <summary>
/// The progress events an SSE endpoint sends while it works.
///
/// The shape — <c>stage</c>, <c>stepNumber</c>, <c>totalSteps</c>,
/// <c>message</c> — was written out by hand seventeen times across the
/// chunk-boundary, section-summary and chapter-summary streams, and the browser
/// parses all seventeen with one handler. Seventeen anonymous objects are
/// seventeen chances to misspell a field into silence: the reader reads
/// <c>event.stepNumber</c>, and a <c>stepNo</c> would render as
/// <c>undefined/undefined</c> rather than fail.
///
/// <para>The response and the event name are fixed per stream, so they are held
/// here rather than repeated at every call. That is most of what made the
/// hand-written version worth copying: the two streams disagree about the event
/// name — the chapter summary sends named <c>progress</c> events, the
/// chunk-boundary stream sends unnamed ones — and getting that wrong at one
/// call site out of seventeen is invisible until a browser ignores the event.</para>
/// </summary>
/// <param name="eventName">
/// Null sends an unnamed event, which the browser delivers as <c>message</c>.
/// Both forms are in use and they are not interchangeable per stream.
/// </param>
public sealed class SseProgress(HttpResponse response, string? eventName = null)
{
    /// <summary>Where the work is now. <paramref name="stage"/> is the stream's
    /// own vocabulary — "indexing", "chunks", "sections", "final".</summary>
    public Task StepAsync(string stage, int stepNumber, int totalSteps, string message) =>
        ServerSentEventsHelper.SendEventAsync(
            response,
            new { stage, stepNumber, totalSteps, message },
            eventName);

    /// <summary>
    /// The stream failed and is about to stop. Always step 0 of 1 — a failure
    /// has no position in the run, and the browser renders the message rather
    /// than the counter.
    /// </summary>
    public Task ErrorAsync(string message) =>
        StepAsync("error", 0, 1, message);
}
