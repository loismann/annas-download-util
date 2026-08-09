using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Ai;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace AnnasArchive.API.Endpoints;

/// <summary>
/// Extension methods for mapping AI Character Graph endpoints.
/// </summary>
public static class AiCharacterEndpoints
{
    /// <summary>
    /// Maps AI Character Graph endpoints to the application.
    /// </summary>
    public static WebApplication MapAiCharacterEndpoints(this WebApplication app)
    {
        // The guard pair lives on the group, so a route added below inherits it
        // instead of needing it repeated — which is how one silently ships without.
        var group = app.MapGroup("/api/ai/characters")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/ai/characters/graph - Generate character graph from summaries
        group.MapPost("/graph", HandleGenerateGraph);

        // GET /api/ai/characters/graph - Get cached character graph
        group.MapGet("/graph", HandleGetGraph);

        // POST /api/ai/characters/update - Update character graph with new content
        group.MapPost("/update", HandleUpdateGraph);

        return app;
    }

    private static async Task<IResult> HandleGenerateGraph(
        HttpContext context,
        [FromBody] CharacterGraphRequest request,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IAiResponseParser aiResponseParser,
        IAiChatCompletion chat)
    {
        if (string.IsNullOrWhiteSpace(request.DropboxPath))
            return ApiResponse.BadRequest("DropboxPath is required.");

        Log.Information("📊 Generating character graph for {Book}...", request.BookTitle ?? request.DropboxPath);

        // Gather all existing summaries (both chapter and section) for this book
        var chapterSummaries = AiContentCache.GetAllChapterSummariesAsStrings(request.DropboxPath);
        var sectionSummaries = AiContentCache.GetAllSectionSummaries(request.DropboxPath);

        if (chapterSummaries.Count == 0 && sectionSummaries.Count == 0)
        {
            Log.Warning("⚠️ No summaries found. Generate some chapter or section summaries first.");
            return ApiResponse.BadRequest("No summaries found. Please generate chapter or section summaries as you read the book first.");
        }

        Log.Information("📚 Found {ChapterSummariesCount} chapter summaries and {SectionSummariesCount} section summaries to analyze", chapterSummaries.Count, sectionSummaries.Count);

        // Combine all summaries
        var allSummaries = new List<string>();
        allSummaries.AddRange(chapterSummaries);
        allSummaries.AddRange(sectionSummaries);
        var totalSummaryCount = allSummaries.Count;

        var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return Results.Problem("OpenAI API key not configured.");

        try
        {
            var userPrompt = CharacterGraphPrompts.UserPrompt(request.BookTitle, allSummaries);


            var outcome = await chat.CompleteAsync(
                new AiChatCall(
                    Endpoint: "character-graph",
                    // gpt-4o rather than the configured deep model: this reads
                    // summaries that already exist, it does not write prose.
                    Model: "gpt-4o",
                    SystemPrompt: CharacterGraphPrompts.SystemPrompt,
                    UserPrompt: userPrompt,
                    MaxCompletionTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:CharacterGraph"),
                    Temperature: cfg.GetValue<double>("AI:Temperature:CharacterGraph")),
                context);

            if (!outcome.Succeeded) return outcome.Failure!;

            var content = outcome.Text;
            if (string.IsNullOrWhiteSpace(content))
            {
                Log.Error("❌ No content returned from GPT");
                return Results.Problem("No character graph data returned.");
            }

            var graph = ParseGraph(content, totalSummaryCount);
            if (graph is null)
                return Results.Problem("Failed to parse character graph data.");

            AiContentCache.SaveCharacterGraph(request.DropboxPath, graph);
            Log.Information(
                "Character graph generated: {NodeCount} characters, {EdgeCount} relationships, from {TotalSummaryCount} summaries ({ChapterCount} chapter + {SectionCount} section)",
                graph.Nodes.Count, graph.Edges.Count, totalSummaryCount, chapterSummaries.Count, sectionSummaries.Count);

            return Results.Ok(graph);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Error(ex, "❌ Character graph generation failed");
            return Results.Problem("Failed to generate character graph.");
        }
    }

    private static readonly JsonSerializerOptions GraphJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Reads the model's reply as a character graph, or null if it is not one.
    ///
    /// Missing `nodes`/`edges` and malformed JSON are the same outcome here:
    /// there is nothing partial worth showing, since a graph with edges pointing
    /// at characters that were not returned draws worse than no graph at all.
    /// </summary>
    private static CharacterGraphResponse? ParseGraph(string content, int summaryCount)
    {
        try
        {
            using var jsonDoc = JsonDocument.Parse(content);
            var root = jsonDoc.RootElement;

            var nodes = JsonSerializer.Deserialize<List<CharacterNode>>(
                root.GetProperty("nodes").GetRawText(), GraphJson) ?? [];
            var edges = JsonSerializer.Deserialize<List<CharacterEdge>>(
                root.GetProperty("edges").GetRawText(), GraphJson) ?? [];

            return new CharacterGraphResponse(nodes, edges, summaryCount, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Character graph did not parse. Response began: {Preview}",
                content[..Math.Min(200, content.Length)]);
            return null;
        }
    }

    private static IResult HandleGetGraph([FromQuery] string? dropboxPath)
    {
        if (string.IsNullOrWhiteSpace(dropboxPath))
            return ApiResponse.BadRequest("Query parameter 'dropboxPath' is required.");

        var graph = AiContentCache.LoadCharacterGraph(dropboxPath);
        if (graph == null)
            return ApiResponse.NotFound("No character graph found. Generate one first.");

        // Check if the graph is stale (has fewer summaries than currently exist)
        var currentChapterSummaries = AiContentCache.GetAllChapterSummariesAsStrings(dropboxPath);
        var currentSectionSummaries = AiContentCache.GetAllSectionSummaries(dropboxPath);
        var currentTotalCount = currentChapterSummaries.Count + currentSectionSummaries.Count;
        var needsUpdate = currentTotalCount > graph.SummaryCount;

        return Results.Ok(new
        {
            graph.Nodes,
            graph.Edges,
            graph.SummaryCount,
            graph.CachedAt,
            CurrentSummaryCount = currentTotalCount,
            NeedsUpdate = needsUpdate
        });
    }

    private static async Task<IResult> HandleUpdateGraph(
        HttpContext context,
        [FromBody] CharacterGraphUpdateRequest request,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IAiResponseParser aiResponseParser,
        IAiChatCompletion chat)
    {
        if (string.IsNullOrWhiteSpace(request.DropboxPath) || string.IsNullOrWhiteSpace(request.NewContent))
            return ApiResponse.BadRequest("DropboxPath and NewContent are required.");

        var existingGraph = AiContentCache.LoadCharacterGraph(request.DropboxPath);
        if (existingGraph == null)
            return ApiResponse.BadRequest("No existing character graph. Generate one first.");

        Log.Information("🔄 Updating character graph with new content...");

        var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return Results.Problem("OpenAI API key not configured.");

        try
        {
            var existingJson = JsonSerializer.Serialize(existingGraph);

            var systemPrompt = @"You are a character relationship analyzer. Update an existing character network graph based on new story content.

Return ONLY valid JSON, no markdown.

Rules:
- Add new characters if they appear and are important
- Add new relationships discovered
- Update relationship labels if they change
- Keep the same JSON structure as the existing graph
- Do NOT remove existing characters or relationships unless directly contradicted";

            var userPrompt = $@"Existing character graph:
{existingJson}

New story content:
{request.NewContent}

Update the character graph with any new information. Return the complete updated graph.";

            var outcome = await chat.CompleteAsync(
                new AiChatCall(
                    Endpoint: "character-graph-update",
                    Model: "gpt-4o",
                    SystemPrompt: systemPrompt,
                    UserPrompt: userPrompt,
                    MaxCompletionTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:ChapterInsight"),
                    Temperature: cfg.GetValue<double>("AI:Temperature:ChapterInsight")),
                context);

            if (!outcome.Succeeded) return outcome.Failure!;

            var content = outcome.Text;
            if (string.IsNullOrWhiteSpace(content))
                return Results.Problem("No updated graph data returned.");

            // Parse updated graph
            var updatedGraph = JsonSerializer.Deserialize<CharacterGraphResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new Exception("Failed to parse updated graph");

            // Save to cache
            AiContentCache.SaveCharacterGraph(request.DropboxPath, updatedGraph);
            Log.Information("✅ Character graph updated: {UpdatedGraphNodesCount} characters, {UpdatedGraphEdgesCount} relationships", updatedGraph.Nodes.Count, updatedGraph.Edges.Count);

            return Results.Ok(updatedGraph);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            Log.Error(ex, "❌ Character graph update failed");
            return Results.Problem("Failed to update character graph.");
        }
    }
}
