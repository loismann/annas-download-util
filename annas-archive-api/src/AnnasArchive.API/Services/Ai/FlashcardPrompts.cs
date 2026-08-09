namespace AnnasArchive.API.Services.Ai;

/// <summary>
/// The prompts behind vocabulary flashcard extraction.
///
/// Kept beside the other prompt files rather than inline in the endpoint, for
/// the same reason as <see cref="ChapterSummaryPrompts"/>: this is a
/// specification of what the model should return, and it was 55 of the
/// endpoint handler's 169 lines. What the handler does — validate, call, parse,
/// save — is legible once the specification is somewhere else.
///
/// <para>The passage is interpolated into the *user* prompt, never the system
/// one. It is arbitrary prose out of an EPUB and can contain sentences shaped
/// like instructions; keeping the standing rules in the system prompt is what
/// lets the model tell the two apart.</para>
/// </summary>
public static class FlashcardPrompts
{
    /// <summary>The standing rules: what counts as worth a card, and the JSON shape.</summary>
    public const string SystemPrompt =
        @"You are a vocabulary flashcard generator. Your job is to extract INDIVIDUAL WORDS or SHORT PHRASES from text and create a separate flashcard for EACH ONE.

CRITICAL: Extract MULTIPLE individual terms from the passage. DO NOT create a single flashcard with the entire passage. Each flashcard should be for ONE specific word or short phrase.

Return ONLY valid JSON, no markdown or explanation.

JSON Structure (ARRAY of flashcards):
[
  { ""term"": ""audacity"", ""definition"": ""bold or rude behavior"", ""etymology"": ""Latin audax (bold)"", ""usageExamples"": [""She had the audacity to criticize."", ""His audacity was shocking.""], ""notes"": """" },
  { ""term"": ""rhizome"", ""definition"": ""(philosophy) a non-hierarchical network structure, as opposed to a tree-like hierarchy"", ""etymology"": ""Greek rhizoma (mass of roots)"", ""usageExamples"": [""Deleuze uses rhizome as a metaphor."", ""A rhizomatic structure has no center.""], ""notes"": ""Specific philosophical meaning by Deleuze & Guattari"" },
  ...
]

What to extract (BE VERY SELECTIVE):
- College-level or graduate-level vocabulary (words beyond typical high school reading)
- Foreign words/phrases used in the text
- Specialized academic, philosophical, or technical terms
- Subject-specific jargon that requires domain knowledge
- Neologisms or terms with specialized meaning in this work (e.g., philosophy terms that are also common English words but have specific meaning here)
- Archaic or literary words rarely used in modern English
- Historical/cultural references requiring background knowledge

DO NOT extract:
- Common words that high school students would know (e.g., ""said"", ""walked"", ""important"", ""although"", ""necessary"")
- Basic academic words taught in high school (e.g., ""analyze"", ""demonstrate"", ""significant"")
- Simple vocabulary regardless of context

BE STRICT: Only select words that would genuinely challenge someone with a high school education or require specific domain knowledge.

Rules:
- Extract 3-10 individual terms from the passage (fewer is better than including common words)
- Each term should be a SINGLE WORD or SHORT PHRASE (2-4 words max)
- Definitions: 1-2 sentences, clear and concise (include subject-specific meaning if applicable)
- Usage examples: 2 brief sentences showing the word in context
- Etymology: Short phrase (""Unknown"" if unclear)
- Notes: Include context if the word has a specific meaning in this discipline/work";

    /// <summary>
    /// The passage to mine, plus the book it came from and anything the reader
    /// already knows so the model does not offer it back.
    /// </summary>
    public static string UserPrompt(string passage, string? bookTitle, IReadOnlyCollection<string>? knownWords, string? extraInstructions)
    {
        var known = knownWords is { Count: > 0 }
            ? $"\n\nEXCLUDE these words (user already knows them): {string.Join(", ", knownWords)}"
            : "";

        var custom = !string.IsNullOrWhiteSpace(extraInstructions)
            ? $"\n\nSPECIAL INSTRUCTIONS:\n{extraInstructions}\n"
            : "";

        return $@"Extract vocabulary terms from this passage:

""{passage}""

Context: {bookTitle ?? "Unknown book"}{known}{custom}

Return JSON array of flashcards for individual terms found in the passage.";
    }
}
