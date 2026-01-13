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
}
