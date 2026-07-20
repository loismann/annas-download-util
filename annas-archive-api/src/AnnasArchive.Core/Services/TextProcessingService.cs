using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for text processing operations
/// </summary>
public class TextProcessingService : ITextProcessingService
{
    /// <summary>
    /// Extracts word offset from a filename format: "summary-{chapterId}-{wordOffset}"
    /// </summary>
    public int ExtractWordOffset(string filenameWithoutExtension)
    {
        // Format: "summary-{chapterId}-{wordOffset}"
        var parts = filenameWithoutExtension.Split('-');
        if (parts.Length >= 3 && int.TryParse(parts[2], out var offset))
            return offset;
        return int.MaxValue; // If we can't parse, put at the end
    }

    /// <summary>
    /// Builds an analysis prompt with context and previous analyses
    /// </summary>
    public string BuildAnalysisPrompt(string contextBlock, string? previousAnalyses, string currentText)
    {
        var prompt = new StringBuilder();

        prompt.AppendLine($"{contextBlock}\n");

        if (!string.IsNullOrWhiteSpace(previousAnalyses))
        {
            prompt.AppendLine("Previous analyses from this reading session:");
            prompt.AppendLine(previousAnalyses);
            prompt.AppendLine("\n---\n");
        }

        prompt.AppendLine("Analyze this passage:");
        prompt.AppendLine(currentText);

        return prompt.ToString();
    }

    /// <summary>
    /// Splits text into chunks of maximum word count
    /// </summary>
    public List<string> SplitIntoChunks(string text, int maxWords)
    {
        if (string.IsNullOrWhiteSpace(text) || maxWords <= 0)
            return new List<string>();

        var words = Regex.Split(text, @"\s+").Where(w => !string.IsNullOrWhiteSpace(w)).ToArray();
        var chunks = new List<string>();
        for (var i = 0; i < words.Length; i += maxWords)
        {
            var slice = words.Skip(i).Take(maxWords);
            chunks.Add(string.Join(" ", slice));
        }
        return chunks;
    }
}
