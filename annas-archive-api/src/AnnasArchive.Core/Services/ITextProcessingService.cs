using System.Collections.Generic;

namespace AnnasArchive.Core.Services;

/// <summary>
/// Service for text processing operations
/// </summary>
public interface ITextProcessingService
{
    /// <summary>
    /// Extracts word offset from a filename format: "summary-{chapterId}-{wordOffset}"
    /// </summary>
    /// <param name="filenameWithoutExtension">Filename without extension</param>
    /// <returns>Word offset, or int.MaxValue if parsing fails</returns>
    int ExtractWordOffset(string filenameWithoutExtension);

    /// <summary>
    /// Builds an analysis prompt with context and previous analyses
    /// </summary>
    /// <param name="contextBlock">Book context information</param>
    /// <param name="previousAnalyses">Optional previous analyses from the session</param>
    /// <param name="currentText">Current text to analyze</param>
    /// <returns>Formatted prompt string</returns>
    string BuildAnalysisPrompt(string contextBlock, string? previousAnalyses, string currentText);

    /// <summary>
    /// Splits text into chunks of maximum word count
    /// </summary>
    /// <param name="text">Text to split</param>
    /// <param name="maxWords">Maximum words per chunk</param>
    /// <returns>List of text chunks</returns>
    List<string> SplitIntoChunks(string text, int maxWords);
}
