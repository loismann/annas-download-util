using AnnasArchive.API.Helpers;
using AnnasArchive.API.Reader2.Epub;

namespace AnnasArchive.API.Reader2.Endpoints;

/// <summary>
/// One server-sent-event stream: progress steps, then a result or an error.
///
/// <para><b>Why this exists rather than a bare <see cref="SseProgress"/>.</b> The
/// pipeline reports through <see cref="IProgress{T}"/>, whose <c>Report</c> is
/// synchronous, while writing a frame is not. The usual bridge is
/// <c>_ = WriteAsync(step)</c>, which is fire-and-forget — banned in
/// <c>Reader2.*</c> — and worse than untidy here: two frames written
/// concurrently to one <see cref="HttpResponse"/> can interleave into a JSON
/// fragment the browser cannot parse, and the failure looks like a hung stream.</para>
///
/// <para>So writes are chained onto a tail task and <see cref="DrainAsync"/> is
/// awaited before the result frame. Ordering is guaranteed, nothing is dropped,
/// and no write escapes the request.</para>
/// </summary>
internal sealed class SseStream(HttpResponse response)
{
    private readonly SseProgress _progress = new(response, "progress");
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>Reports pipeline progress onto this stream, in order.</summary>
    public IProgress<ProgressStep> Progress => new Relay(this);

    /// <summary>Waits for every queued frame to reach the client.</summary>
    public Task DrainAsync()
    {
        lock (_gate) return _tail;
    }

    public async Task ResultAsync(object payload)
    {
        await DrainAsync();
        await ServerSentEventsHelper.SendEventAsync(response, payload, "result");
    }

    /// <summary>
    /// The stream failed. Sent once and last — a second error frame renders a
    /// second message in the reader.
    /// </summary>
    public async Task ErrorAsync(string message)
    {
        await DrainAsync();
        await _progress.ErrorAsync(message);
    }

    private void Enqueue(ProgressStep step)
    {
        lock (_gate)
            _tail = _tail.ContinueWith(
                _ => _progress.StepAsync(step.Stage, step.Current, step.Total, step.Message),
                TaskScheduler.Default).Unwrap();
    }

    private sealed class Relay(SseStream stream) : IProgress<ProgressStep>
    {
        public void Report(ProgressStep value) => stream.Enqueue(value);
    }
}
