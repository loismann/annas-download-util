using System.Text.Json;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Models;
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
        // POST /api/ai/characters/graph - Generate character graph from summaries
        app.MapPost("/api/ai/characters/graph", HandleGenerateGraph)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // GET /api/ai/characters/graph - Get cached character graph
        app.MapGet("/api/ai/characters/graph", HandleGetGraph)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        // POST /api/ai/characters/update - Update character graph with new content
        app.MapPost("/api/ai/characters/update", HandleUpdateGraph)
            .RequireAuthorization()
            .RequireRateLimiting("api");

        return app;
    }

    private static async Task<IResult> HandleGenerateGraph(
        HttpContext context,
        [FromBody] CharacterGraphRequest request,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser)
    {
        if (string.IsNullOrWhiteSpace(request.DropboxPath))
            return Results.BadRequest(new { error = "DropboxPath is required." });

        Log.Information("📊 Generating character graph for {request.BookTitle ?? request.DropboxPath}...");

        // Gather all existing summaries (both chapter and section) for this book
        var chapterSummaries = AiContentCache.GetAllChapterSummariesAsStrings(request.DropboxPath);
        var sectionSummaries = AiContentCache.GetAllSectionSummaries(request.DropboxPath);

        if (chapterSummaries.Count == 0 && sectionSummaries.Count == 0)
        {
            Log.Information("⚠️ No summaries found. Generate some chapter or section summaries first.");
            return Results.BadRequest(new { error = "No summaries found. Please generate chapter or section summaries as you read the book first." });
        }

        Log.Information("📚 Found {chapterSummaries.Count} chapter summaries and {sectionSummaries.Count} section summaries to analyze");

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
            using var http = httpFactory.CreateClient("OpenAI");

            // Build consolidated summary text
            var summaryText = string.Join("\n\n---\n\n", allSummaries.Select((s, i) =>
                $"Summary {i + 1}:\n{s}"));

            var systemPrompt = @"You are a character relationship analyzer for novels. Analyze the provided story summaries and create a network graph of character relationships.

IMPORTANT: Only include information that appears in the provided summaries. Do not add or infer information beyond what's explicitly mentioned.

Return ONLY valid JSON, no markdown, no code blocks.

JSON Structure:
{
  ""nodes"": [
    {
      ""id"": ""zhao"",
      ""label"": ""Adm. Zhao"",
      ""description"": ""Brief role (2-5 words)"",
      ""detailedDescription"": ""Detailed description of who they are, what they've done so far, their motivations and characteristics based ONLY on the summaries provided (2-3 sentences)""
    }
  ],
  ""edges"": [
    {
      ""from"": ""zhao"",
      ""to"": ""miller"",
      ""label"": ""relationship type (friend/enemy/spouse/etc.)"",
      ""detailedDescription"": ""Detailed description of their relationship and key interactions based ONLY on the summaries provided (1-2 sentences)""
    }
  ]
}

CRITICAL: The ""from"" and ""to"" fields in edges MUST use the simplified lowercase IDs, NOT the character labels.
Example: If a node has id=""zhao"" and label=""Adm. Zhao"", the edge must use ""zhao"", not ""Adm. Zhao"".

Rules:
- Include main and important secondary characters (5-15 characters max)
- Only include characters that appear in the provided summaries
- Character names MUST be properly capitalized (first letter of each word uppercase)
- If a character has a military/professional title (Admiral, Captain, Lieutenant, Sergeant, Doctor, etc.), include the abbreviated title before their name:
  * Admiral → Adm.
  * Captain → Capt.
  * Lieutenant → Lt.
  * Sergeant → Sgt.
  * Colonel → Col.
  * Doctor → Dr.
  * Professor → Prof.
  * Example: ""Adm. Zhao"", ""Capt. Miller"", ""Dr. Smith""
- Relationship labels should be concise
- Detailed descriptions should cite specific events from the summaries
- The ""id"" field should be a simplified lowercase version without titles (e.g., ""zhao"", ""miller"", ""smith"")
- Do NOT reveal information that hasn't appeared in the summaries";

            var userPrompt = $@"Analyze the characters and their relationships from these story summaries:

Book: {request.BookTitle ?? "Unknown"}

Story Summaries:
{summaryText}

Create a character relationship network graph based ONLY on information in these summaries.";

            var model = "gpt-4o"; // Use GPT-4o for cost-effective character graph generation
            Log.Information("🤖 Using model for character graph: {model}");

            var payload = modelHelper.BuildChatCompletionPayload(
                model,
                new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                maxCompletionTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:CharacterGraph"),
                temperature: cfg.GetValue<double>("AI:Temperature:CharacterGraph")
            );

            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Log.Information("❌ Character graph failed: {response.StatusCode}");
                return Results.Problem($"Character graph generation failed: {(int)response.StatusCode}");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var content = aiResponseParser.ExtractText(doc.RootElement);

            if (string.IsNullOrWhiteSpace(content))
            {
                Log.Information("❌ No content returned from GPT");
                return Results.Problem("No character graph data returned.");
            }

            // Track token usage
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
                var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
                var userId = UserHelpers.GetUserIdFromContext(context);
                if (userId != null)
                    tokenUsage.AddUsage(userId, promptTokens, completionTokens);
            }

            // Parse the character graph JSON
            try
            {
                using var jsonDoc = JsonDocument.Parse(content);
                var root = jsonDoc.RootElement;

                var nodesJson = root.GetProperty("nodes").GetRawText();
                var edgesJson = root.GetProperty("edges").GetRawText();

                var nodes = JsonSerializer.Deserialize<List<CharacterNode>>(nodesJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new List<CharacterNode>();

                var edges = JsonSerializer.Deserialize<List<CharacterEdge>>(edgesJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new List<CharacterEdge>();

                // Create response with metadata
                var graph = new CharacterGraphResponse(nodes, edges, totalSummaryCount, DateTime.UtcNow);

                // Save to cache
                AiContentCache.SaveCharacterGraph(request.DropboxPath, graph);
                Log.Information("✅ Character graph generated with {graph.Nodes.Count} characters and {graph.Edges.Count} relationships from {totalSummaryCount} summaries ({chapterSummaries.Count} chapter + {sectionSummaries.Count} section)");

                return Results.Ok(graph);
            }
            catch (Exception ex)
            {
                Log.Information("❌ Failed to parse character graph: {ex.Message}");
                Log.Information("   Content: {content.Substring(0, Math.Min(200, content.Length))}");
                return Results.Problem("Failed to parse character graph data.");
            }
        }
        catch (Exception ex)
        {
            Log.Information("❌ Character graph generation failed: {ex.Message}");
            return Results.Problem("Failed to generate character graph.");
        }
    }

    private static IResult HandleGetGraph([FromQuery] string? dropboxPath)
    {
        if (string.IsNullOrWhiteSpace(dropboxPath))
            return Results.BadRequest(new { error = "Query parameter 'dropboxPath' is required." });

        var graph = AiContentCache.LoadCharacterGraph(dropboxPath);
        if (graph == null)
            return Results.NotFound(new { error = "No character graph found. Generate one first." });

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
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ITokenUsageService tokenUsage,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser)
    {
        if (string.IsNullOrWhiteSpace(request.DropboxPath) || string.IsNullOrWhiteSpace(request.NewContent))
            return Results.BadRequest(new { error = "DropboxPath and NewContent are required." });

        var existingGraph = AiContentCache.LoadCharacterGraph(request.DropboxPath);
        if (existingGraph == null)
            return Results.BadRequest(new { error = "No existing character graph. Generate one first." });

        Log.Information("🔄 Updating character graph with new content...");

        var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return Results.Problem("OpenAI API key not configured.");

        try
        {
            using var http = httpFactory.CreateClient("OpenAI");

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

            var model = "gpt-4o"; // Use GPT-4o for cost-effective character graph updates
            Log.Information("🤖 Using model for character graph update: {model}");

            var payload = modelHelper.BuildChatCompletionPayload(
                model,
                new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                maxCompletionTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:ChapterInsight"),
                temperature: cfg.GetValue<double>("AI:Temperature:ChapterInsight")
            );

            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
            if (!response.IsSuccessStatusCode)
            {
                Log.Information("❌ Character graph update failed: {response.StatusCode}");
                return Results.Problem("Failed to update character graph.");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var content = aiResponseParser.ExtractText(doc.RootElement);

            if (string.IsNullOrWhiteSpace(content))
                return Results.Problem("No updated graph data returned.");

            // Track token usage
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
                var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
                var userId = UserHelpers.GetUserIdFromContext(context);
                if (userId != null)
                    tokenUsage.AddUsage(userId, promptTokens, completionTokens);
            }

            // Parse updated graph
            var updatedGraph = JsonSerializer.Deserialize<CharacterGraphResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new Exception("Failed to parse updated graph");

            // Save to cache
            AiContentCache.SaveCharacterGraph(request.DropboxPath, updatedGraph);
            Log.Information("✅ Character graph updated: {updatedGraph.Nodes.Count} characters, {updatedGraph.Edges.Count} relationships");

            return Results.Ok(updatedGraph);
        }
        catch (Exception ex)
        {
            Log.Information("❌ Character graph update failed: {ex.Message}");
            return Results.Problem("Failed to update character graph.");
        }
    }
}
