using System.Text.Json;
using AnnasArchive.API.Helpers;
using Microsoft.AspNetCore.Http;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// The browser parses every progress event with one handler keyed on
/// <c>stage</c> and <c>stepNumber</c>, so a field spelled wrong at one of the
/// seventeen former call sites would render as <c>undefined/undefined</c> rather
/// than fail. One writer, one shape, and these assertions are what the reader
/// actually reads.
/// </summary>
public class SseProgressTests
{
    [Fact]
    public async Task WritesTheFourFieldsTheReaderExpects()
    {
        var (body, _) = await Capture(p => p.StepAsync("chunks", 3, 10, "Analyzing chunk 3/10..."));

        var json = Payload(body);
        json.GetProperty("stage").GetString().Should().Be("chunks");
        json.GetProperty("stepNumber").GetInt32().Should().Be(3);
        json.GetProperty("totalSteps").GetInt32().Should().Be(10);
        json.GetProperty("message").GetString().Should().Be("Analyzing chunk 3/10...");
    }

    [Fact]
    public async Task FieldNamesAreCamelCase()
    {
        // The reader reads event.stepNumber. Serialising as StepNumber would not
        // throw anywhere — the progress bar would just never move.
        var (body, _) = await Capture(p => p.StepAsync("chunks", 1, 2, "x"));

        body.Should().Contain("\"stepNumber\"").And.Contain("\"totalSteps\"");
        body.Should().NotContain("\"StepNumber\"");
    }

    [Fact]
    public async Task AnErrorIsStageErrorAtStepZeroOfOne()
    {
        var (body, _) = await Capture(p => p.ErrorAsync("Failed to index book: disk full"));

        var json = Payload(body);
        json.GetProperty("stage").GetString().Should().Be("error");
        json.GetProperty("stepNumber").GetInt32().Should().Be(0);
        json.GetProperty("totalSteps").GetInt32().Should().Be(1);
        json.GetProperty("message").GetString().Should().Be("Failed to index book: disk full");
    }

    // ─── The event name is per stream, and the two streams differ ────────

    [Fact]
    public async Task NamesTheEventWhenTheStreamUsesNamedEvents()
    {
        // The chapter-summary stream sends `progress`; its reader subscribes by
        // name and ignores anything unnamed.
        var (body, _) = await Capture(p => p.StepAsync("chunks", 1, 2, "x"), eventName: "progress");

        body.Should().StartWith("event: progress\n");
    }

    [Fact]
    public async Task SendsAnUnnamedEventWhenTheStreamDoesNot()
    {
        // The chunk-boundary stream reads raw `data:` lines. An `event:` line
        // here would route the event away from the handler that wants it.
        var (body, _) = await Capture(p => p.StepAsync("detecting", 0, 1, "x"));

        body.Should().StartWith("data: ");
        body.Should().NotContain("event: ");
    }

    [Fact]
    public async Task EndsTheEventWithABlankLineSoTheBrowserDispatchesIt()
    {
        var (body, _) = await Capture(p => p.StepAsync("detecting", 0, 1, "x"));

        body.Should().EndWith("\n\n");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static async Task<(string Body, HttpResponse Response)> Capture(
        Func<SseProgress, Task> write,
        string? eventName = null)
    {
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        await write(new SseProgress(context.Response, eventName));

        context.Response.Body.Position = 0;
        return (await new StreamReader(context.Response.Body).ReadToEndAsync(), context.Response);
    }

    /// <summary>The JSON after the `data: ` prefix.</summary>
    private static JsonElement Payload(string body)
    {
        var line = body.Split('\n').First(l => l.StartsWith("data: ", StringComparison.Ordinal));
        return JsonDocument.Parse(line["data: ".Length..]).RootElement;
    }
}
