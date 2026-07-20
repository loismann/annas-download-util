using System.Text.Json;
using AnnasArchive.API.Configuration;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Services;
using Dropbox.Api;
using Serilog;

namespace AnnasArchive.API.Helpers;

/// <summary>
/// Helper methods for AI summarization operations.
/// </summary>
public static class AiSummaryHelpers
{
    /// <summary>
    /// Loads the chapter index for a book, checking both library and Dropbox sources.
    /// </summary>
    public static async Task<CachedChapterIndex?> LoadChapterIndexAsync(
        DropboxClient dropbox,
        string dropboxPath)
    {
        var existingKeys = AiContentCache.GetExistingSummaryKeys();
        if (TryResolveLibraryFileForReaderKey(dropboxPath, existingKeys, out _, out var fullPath))
        {
            var (index, _) = await LibraryEpubCache.GetOrBuildChapterIndexAsync(fullPath, dropboxPath);
            return index;
        }

        var (dropboxIndex, _) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, dropboxPath);
        return dropboxIndex;
    }

    /// <summary>
    /// Loads chapter content for a specific chapter, checking both library and Dropbox sources.
    /// </summary>
    public static async Task<string?> LoadChapterContentAsync(
        DropboxClient dropbox,
        string dropboxPath,
        int chapterId)
    {
        var existingKeys = AiContentCache.GetExistingSummaryKeys();
        if (TryResolveLibraryFileForReaderKey(dropboxPath, existingKeys, out _, out var fullPath))
        {
            var (index, cacheDir) = await LibraryEpubCache.GetOrBuildChapterIndexAsync(fullPath, dropboxPath);
            var chapter = index.Chapters.FirstOrDefault(c => c.Id == chapterId);

            if (chapter is null || string.IsNullOrWhiteSpace(chapter.FileName))
                return null;

            var chapterPath = Path.Combine(cacheDir, chapter.FileName);
            if (!File.Exists(chapterPath))
                await LibraryEpubCache.EnsureCacheBuildAsync(fullPath, dropboxPath, cacheDir);

            var content = await File.ReadAllTextAsync(chapterPath);
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }

        var (dropboxIndex, dropboxCacheDir) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, dropboxPath);
        var dropboxChapter = dropboxIndex.Chapters.FirstOrDefault(c => c.Id == chapterId);

        if (dropboxChapter is null || string.IsNullOrWhiteSpace(dropboxChapter.FileName))
            return null;

        var dropboxChapterPath = Path.Combine(dropboxCacheDir, dropboxChapter.FileName);
        if (!File.Exists(dropboxChapterPath))
            await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, dropboxPath, dropboxCacheDir);

        var dropboxContent = await File.ReadAllTextAsync(dropboxChapterPath);
        return string.IsNullOrWhiteSpace(dropboxContent) ? null : dropboxContent;
    }

    /// <summary>
    /// TIER 1: Summarizes text chunks with progress updates via SSE.
    /// </summary>
    public static async Task<(List<string> chunkSummaries, int promptTokens, int completionTokens)> SummarizeChunksAsync(
        HttpClient http,
        string model,
        List<string> chunks,
        string contextLine,
        HttpResponse response,
        IConfiguration cfg,
        IAiResponseParser aiResponseParser,
        ITokenUsageService tokenUsage)
    {
        var chunkSummaries = new List<string>();
        var promptTokensTotal = 0;
        var completionTokensTotal = 0;

        var chunkInstructions = @"You are an educational guide helping someone deeply understand complex texts. Analyze this passage with rich detail:

1. **What's Happening**: Summarize the main points, arguments, or narrative events
2. **Key Concepts**: Identify and explain central ideas or terminology
3. **Context**: What historical, philosophical, or intellectual background is relevant?
4. **Significance**: Why does this matter? What is the author building toward?

Write 300-400 words that assume the reader is intelligent but may lack specialized background knowledge. Explain references and provide context.";

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var chunkInput = $"{chunkInstructions}\n\nContext: {contextLine}\n\n{chunk}";

            await ServerSentEventsHelper.SendEventAsync(response, new
            {
                stage = "chunks",
                stepNumber = i + 1,
                totalSteps = chunks.Count,
                message = $"Analyzing chunk {i + 1}/{chunks.Count}..."
            }, "progress");

            var payload = new
            {
                model,
                input = chunkInput,
                reasoning = new { effort = cfg.GetValue<string>("AI:ReasoningEffort:ChunkSummary") },
                max_output_tokens = cfg.GetValue<int>("AI:MaxCompletionTokens:ChunkSummary")
            };

            var chunkResponse = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload);
            if (!chunkResponse.IsSuccessStatusCode)
            {
                var body = await chunkResponse.Content.ReadAsStringAsync();
                Log.Information("❌ OpenAI chunk summary failed status={(int)chunkResponse.StatusCode} body={body}");
                await ServerSentEventsHelper.SendEventAsync(response, new
                {
                    stage = "chunks",
                    stepNumber = i + 1,
                    totalSteps = chunks.Count,
                    message = $"Failed at chunk {i + 1}/{chunks.Count}",
                    error = $"HTTP {(int)chunkResponse.StatusCode}: {body}"
                }, "error");
                throw new HttpRequestException($"Chunk summarization failed at chunk {i + 1}");
            }

            using var stream = await chunkResponse.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            Log.Information("🔍 Chunk {i + 1} response JSON: {doc.RootElement.GetRawText()}");

            var chunkSummary = aiResponseParser.ExtractText(doc.RootElement) ?? string.Empty;
            Log.Information("🔍 Chunk {i + 1} extracted summary length: {chunkSummary.Length}");

            chunkSummaries.Add(chunkSummary);

            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                promptTokensTotal += usage.GetProperty("input_tokens").GetInt32();
                completionTokensTotal += usage.GetProperty("output_tokens").GetInt32();
            }

            await ServerSentEventsHelper.SendEventAsync(response, new
            {
                stage = "chunks",
                stepNumber = i + 1,
                totalSteps = chunks.Count,
                message = $"Completed chunk {i + 1}/{chunks.Count}",
                success = true
            }, "progress");

            // Throttle between API calls to prevent rate limiting
            if (i < chunks.Count - 1)
            {
                await AiThrottlingConfiguration.ThrottleBetweenItemsAsync();
            }
        }

        return (chunkSummaries, promptTokensTotal, completionTokensTotal);
    }

    /// <summary>
    /// TIER 2: Synthesizes chunk summaries into section summaries with progress updates via SSE.
    /// </summary>
    public static async Task<(List<string> sectionSummaries, int promptTokens, int completionTokens)> SynthesizeSectionsAsync(
        HttpClient http,
        string model,
        List<string> chunkSummaries,
        string contextLine,
        HttpResponse response,
        IConfiguration cfg,
        IAiResponseParser aiResponseParser,
        ITokenUsageService tokenUsage,
        int chunksPerSection = 4)
    {
        var sectionSummaries = new List<string>();
        var promptTokensTotal = 0;
        var completionTokensTotal = 0;

        var totalSections = (int)Math.Ceiling((double)chunkSummaries.Count / chunksPerSection);
        var sectionNum = 0;

        var sectionInstructions = @"You are synthesizing multiple passage analyses into a coherent section summary. Create a unified narrative that:

1. **Traces the Development**: How do the ideas/arguments/events progress through these passages?
2. **Identifies Core Themes**: What are the central concerns of this section?
3. **Contextualizes**: What intellectual traditions, historical debates, or prior thinkers is the author engaging with?
4. **Clarifies**: Explain difficult concepts in accessible terms

Write 400-500 words. Maintain educational depth while creating a flowing narrative.";

        for (var i = 0; i < chunkSummaries.Count; i += chunksPerSection)
        {
            sectionNum++;
            var sectionChunks = chunkSummaries.Skip(i).Take(chunksPerSection).ToList();
            if (sectionChunks.Count == 0) continue;

            await ServerSentEventsHelper.SendEventAsync(response, new
            {
                stage = "sections",
                stepNumber = sectionNum,
                totalSteps = totalSections,
                message = $"Synthesizing section {sectionNum}/{totalSections}..."
            }, "progress");

            var sectionInput = $"{sectionInstructions}\n\nContext: {contextLine}\n\n{string.Join("\n\n---\n\n", sectionChunks)}";

            var sectionPayload = new
            {
                model,
                input = sectionInput,
                reasoning = new { effort = cfg.GetValue<string>("AI:ReasoningEffort:SectionSynthesis") },
                max_output_tokens = cfg.GetValue<int>("AI:MaxCompletionTokens:SectionSynthesis")
            };

            var sectionResponse = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", sectionPayload);
            if (!sectionResponse.IsSuccessStatusCode)
            {
                var body = await sectionResponse.Content.ReadAsStringAsync();
                Log.Information("❌ OpenAI section summary failed status={(int)sectionResponse.StatusCode} body={body}");
                await ServerSentEventsHelper.SendEventAsync(response, new
                {
                    stage = "sections",
                    stepNumber = sectionNum,
                    totalSteps = totalSections,
                    message = $"Failed at section {sectionNum}/{totalSections}",
                    error = $"HTTP {(int)sectionResponse.StatusCode}: {body}"
                }, "error");
                throw new HttpRequestException($"Section synthesis failed at section {sectionNum}");
            }

            using var sectionStream = await sectionResponse.Content.ReadAsStreamAsync();
            using var sectionDoc = await JsonDocument.ParseAsync(sectionStream);
            var sectionSummary = aiResponseParser.ExtractText(sectionDoc.RootElement) ?? string.Empty;

            sectionSummaries.Add(sectionSummary);

            if (sectionDoc.RootElement.TryGetProperty("usage", out var sectionUsage))
            {
                promptTokensTotal += sectionUsage.GetProperty("input_tokens").GetInt32();
                completionTokensTotal += sectionUsage.GetProperty("output_tokens").GetInt32();
            }

            await ServerSentEventsHelper.SendEventAsync(response, new
            {
                stage = "sections",
                stepNumber = sectionNum,
                totalSteps = totalSections,
                message = $"Completed section {sectionNum}/{totalSections}",
                success = true
            }, "progress");

            // Throttle between API calls to prevent rate limiting
            if (sectionNum < totalSections)
            {
                await AiThrottlingConfiguration.ThrottleBetweenItemsAsync();
            }
        }

        return (sectionSummaries, promptTokensTotal, completionTokensTotal);
    }

    /// <summary>
    /// TIER 3: Creates final comprehensive summary from section summaries with progress update via SSE.
    /// </summary>
    public static async Task<(string finalSummary, int promptTokens, int completionTokens)> CreateFinalSummaryAsync(
        HttpClient http,
        string model,
        List<string> sectionSummaries,
        List<string> contextParts,
        HttpResponse response,
        IConfiguration cfg,
        IAiResponseParser aiResponseParser)
    {
        await ServerSentEventsHelper.SendEventAsync(response, new
        {
            stage = "final",
            stepNumber = 1,
            totalSteps = 1,
            message = "Creating final comprehensive summary..."
        }, "progress");

        var finalInstructions = $@"Create a comprehensive 700-900 word educational summary of this chapter that helps someone truly understand and appreciate the material.

Your summary should cover:

1. **Overview**:
   - What is this chapter fundamentally about?
   - What are the main arguments, ideas, or events?

2. **Historical & Intellectual Context**:
   - When and where was this written?
   - What historical events, political climate, or cultural conditions shaped this work?
   - What intellectual traditions or prior thinkers is the author responding to?
   - What debates or questions was the author engaging with?

3. **Core Arguments & Ideas**:
   - What are the key claims or propositions?
   - How does the author support these claims?
   - What concepts or terminology are central to understanding this?

4. **Significance & Interpretation**:
   - Why does this matter?
   - What impact has this had (or might it have)?
   - What makes this important or interesting?

5. **Connections**:
   - How does this relate to other thinkers, movements, or texts?
   - What contemporary issues or questions does this illuminate?

Write as if teaching an intelligent student. Define specialized terms, explain references, and provide context that helps someone new to this material truly understand what's going on and why it matters. Be thorough and educational.";

        var userContent = $"Book context: {string.Join(" | ", contextParts)}\n\nSection summaries:\n{string.Join("\n\n---\n\n", sectionSummaries)}";
        var fullInput = $"{finalInstructions}\n\n{userContent}";

        var finalPrompt = new
        {
            model,
            input = fullInput,
            reasoning = new { effort = cfg.GetValue<string>("AI:ReasoningEffort:FinalSummary") },
            max_output_tokens = cfg.GetValue<int>("AI:MaxCompletionTokens:FinalSummary")
        };

        var finalResponse = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", finalPrompt);
        if (!finalResponse.IsSuccessStatusCode)
        {
            var body = await finalResponse.Content.ReadAsStringAsync();
            Log.Information("❌ OpenAI final summary failed status={(int)finalResponse.StatusCode} body={body}");
            await ServerSentEventsHelper.SendEventAsync(response, new
            {
                stage = "final",
                stepNumber = 1,
                totalSteps = 1,
                message = "Failed to create final summary",
                error = $"HTTP {(int)finalResponse.StatusCode}: {body}"
            }, "error");
            throw new HttpRequestException("Final summary creation failed");
        }

        using var finalStream = await finalResponse.Content.ReadAsStreamAsync();
        using var finalDoc = await JsonDocument.ParseAsync(finalStream);

        Log.Information("🔍 Final response JSON: {finalDoc.RootElement.GetRawText()}");

        string finalSummary = aiResponseParser.ExtractText(finalDoc.RootElement) ?? "No summary returned.";
        Log.Information("🔍 Extracted summary length: {finalSummary.Length}");

        var promptTokens = 0;
        var completionTokens = 0;

        if (finalDoc.RootElement.TryGetProperty("usage", out var finalUsage))
        {
            promptTokens = finalUsage.GetProperty("input_tokens").GetInt32();
            completionTokens = finalUsage.GetProperty("output_tokens").GetInt32();
        }

        return (finalSummary, promptTokens, completionTokens);
    }

    /// <summary>
    /// Resolves a reader key to its sanitized form if it exists in existing keys.
    /// </summary>
    public static string ResolveReaderKey(string fileName, ISet<string> existingKeys)
    {
        if (existingKeys == null || existingKeys.Count == 0)
            return fileName;

        var sanitized = AiContentCache.SanitizeKey(fileName);
        if (existingKeys.Contains(sanitized))
            return sanitized;

        var match = existingKeys.FirstOrDefault(key =>
            key.EndsWith(sanitized, StringComparison.OrdinalIgnoreCase));
        return match ?? fileName;
    }

    /// <summary>
    /// Tries to resolve a library file path from a reader key.
    /// </summary>
    public static bool TryResolveLibraryFileForReaderKey(
        string readerKey,
        ISet<string> existingKeys,
        out string fileName,
        out string fullPath)
    {
        fileName = string.Empty;
        fullPath = string.Empty;
        var libraryRoot = LibraryHelpers.ResolveLibraryRoot();
        if (!Directory.Exists(libraryRoot))
            return false;

        var safeFileName = Path.GetFileName(readerKey);
        if (!string.IsNullOrWhiteSpace(safeFileName))
        {
            var directPath = Path.Combine(libraryRoot, safeFileName);
            if (File.Exists(directPath))
            {
                fileName = safeFileName;
                fullPath = directPath;
                return true;
            }
        }

        var jsonOptions = LibraryHelpers.CreateLibraryJsonOptions();
        foreach (var metaFile in Directory.GetFiles(libraryRoot, "*.meta.json"))
        {
            try
            {
                var json = File.ReadAllText(metaFile);
                var meta = System.Text.Json.JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);
                if (meta == null || string.IsNullOrWhiteSpace(meta.FileName))
                    continue;

                var key = ResolveReaderKey(meta.FileName, existingKeys);
                if (!string.Equals(key, readerKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                var candidate = Path.Combine(libraryRoot, meta.FileName);
                if (File.Exists(candidate))
                {
                    fileName = meta.FileName;
                    fullPath = candidate;
                    return true;
                }
            }
            catch
            {
                // ignore malformed meta files
            }
        }

        return false;
    }
}
