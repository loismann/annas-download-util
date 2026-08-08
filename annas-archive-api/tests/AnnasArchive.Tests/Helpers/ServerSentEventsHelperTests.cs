using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AnnasArchive.API.Helpers;
using Microsoft.AspNetCore.Http;
using Xunit;

public class ServerSentEventsHelperTests
{
    [Fact]
    public async Task SendEventAsync_ShouldWriteDataLine_WhenNoEventNameProvided()
    {
        var context = new DefaultHttpContext();
        await using var bodyStream = new MemoryStream();
        context.Response.Body = bodyStream;

        var payload = new { message = "hello", value = 5 };

        await ServerSentEventsHelper.SendEventAsync(context.Response, payload);

        bodyStream.Position = 0;
        var output = await new StreamReader(bodyStream).ReadToEndAsync();

        Assert.DoesNotContain("event:", output);
        Assert.StartsWith("data: ", output);

        var dataLine = output.Split('\n')[0].Substring("data: ".Length);
        using var doc = JsonDocument.Parse(dataLine);
        Assert.Equal("hello", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task SendEventAsync_ShouldIncludeEventLine_WhenEventNameProvided()
    {
        var context = new DefaultHttpContext();
        await using var bodyStream = new MemoryStream();
        context.Response.Body = bodyStream;

        var payload = new { stage = "progress", step = 1 };

        await ServerSentEventsHelper.SendEventAsync(context.Response, payload, "progress");

        bodyStream.Position = 0;
        var output = await new StreamReader(bodyStream).ReadToEndAsync();
        var lines = output.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("event: progress", lines[0]);
        Assert.StartsWith("data: ", lines[1]);

        var dataLine = lines[1].Substring("data: ".Length);
        using var doc = JsonDocument.Parse(dataLine);
        Assert.Equal("progress", doc.RootElement.GetProperty("stage").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("step").GetInt32());
    }

    // ── the stream preamble ──────────────────────────────────────────────

    /// <summary>
    /// The three headers that make a response an event stream. Written out at
    /// three call sites in three different ways before this existed.
    /// </summary>
    [Fact]
    public void BeginStream_DeclaresTheResponseAnEventStream()
    {
        var context = new DefaultHttpContext();

        ServerSentEventsHelper.BeginStream(context.Response);

        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal("no-cache", context.Response.Headers.CacheControl);
        Assert.Equal("keep-alive", context.Response.Headers.Connection);
    }

    /// <summary>
    /// The reason the helper assigns rather than appends.
    ///
    /// <para>One call site used <c>Headers.Append("Content-Type", …)</c>, which
    /// adds to the header instead of replacing it. Content-Type is single-valued,
    /// so a second value produces <c>application/json, text/event-stream</c> and
    /// the browser's EventSource refuses the stream. Running the preamble twice is
    /// the cheapest way to state that invariant: assignment is idempotent,
    /// appending is not.</para>
    /// </summary>
    [Fact]
    public void BeginStream_IsIdempotent()
    {
        var context = new DefaultHttpContext();

        ServerSentEventsHelper.BeginStream(context.Response);
        ServerSentEventsHelper.BeginStream(context.Response);

        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Single(context.Response.Headers.CacheControl);
        Assert.Single(context.Response.Headers.Connection);
    }

    /// <summary>
    /// A content type already on the response is replaced, not appended to. This
    /// is the case the old Append call was one code path away from hitting: the
    /// same handler answers configuration errors with WriteAsJsonAsync, which sets
    /// application/json.
    /// </summary>
    [Fact]
    public void BeginStream_ReplacesAnExistingContentType()
    {
        var context = new DefaultHttpContext();
        context.Response.ContentType = "application/json";

        ServerSentEventsHelper.BeginStream(context.Response);

        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.DoesNotContain("json", context.Response.ContentType!);
    }
}
