namespace AnnasArchive.API.Services.Ai;

/// <summary>
/// The prompts behind the book reader's AI features, kept away from the HTTP
/// handlers that send them for the reason set out in <c>BookDiscoveryPrompts</c>:
/// a prompt is specification, not plumbing. "Explain WHY this concept matters",
/// "skip images entirely if unsure about a URL" — these are product decisions,
/// and while they sat in the middle of a hundred lines of request building the
/// only way to review one was to read the HTTP either side of it.
///
/// Token budget and temperature arrive from configuration rather than being
/// baked in here, because they are tuned per deployment in
/// <c>appsettings.json</c> under <c>AI:MaxCompletionTokens</c> — but they are
/// still passed *through* this class so that a prompt and its budget stay in
/// one call, and moving one without the other stays hard.
/// </summary>
public static class ReaderPrompts
{
    /// <summary>
    /// The educational section summary. The instruction block is the system
    /// prompt and the passage is the user prompt — the same text the handler
    /// used to send as one string, split at the boundary it already had
    /// ("Text to summarize:").
    /// </summary>
    /// <param name="bookTitle">
    /// Named in the instructions when known. A summary of a passage reads
    /// differently when the model knows which book it is from, which is why this
    /// is worth threading through rather than dropping.
    /// </param>
    public static AiChatCall SectionSummary(
        string model,
        string? bookTitle,
        string sectionText,
        int maxCompletionTokens,
        double temperature)
    {
        var bookContext = !string.IsNullOrWhiteSpace(bookTitle)
            ? $" from the book \"{bookTitle}\""
            : "";

        return new AiChatCall(
            Endpoint: "section-summary",
            Model: model,
            SystemPrompt: $@"You are an expert educator explaining this text section{bookContext} to someone who wants to deeply understand it.

Provide a comprehensive summary that:

1. **What Happens**: Summarize the key events, dialogue, and developments in this section

2. **Explain Concepts**: When you encounter complex ideas, philosophical terms, or specialized vocabulary:
   - Define and explain the concept in accessible language
   - Provide historical or cultural context
   - Explain WHY this concept matters and what problem it addresses
   - Connect abstract ideas to concrete examples

3. **Clarify References**: For any historical, literary, philosophical, or cultural references:
   - Identify who/what is being referenced
   - Explain the significance and context
   - Show how it relates to the current text

4. **Thematic Analysis**: Explain the deeper meaning and themes being explored

Your goal is to make this text comprehensible and meaningful. If the section discusses abstract theory, explain it in plain language. If it references obscure ideas, provide the background needed to understand them. Assume the reader is intelligent but may not be familiar with specialized academic or philosophical concepts.

Keep your summary thorough but focused (2-5 paragraphs depending on complexity).",
            UserPrompt: sectionText,
            MaxCompletionTokens: maxCompletionTokens,
            Temperature: temperature);
    }

    /// <summary>
    /// The "I'm a Dummy" chapter explanation, written from the full chapter
    /// summary rather than the chapter itself — which is why the caller must
    /// have that summary already.
    /// </summary>
    /// <param name="reasoningEffort">
    /// Set instead of a temperature, never alongside one. This is the most
    /// expensive prompt in the reader and the effort knob is what it is tuned
    /// on; sending a temperature would silently switch reasoning off.
    /// </param>
    public static AiChatCall DummyChapterSummary(
        string model,
        string chapterContext,
        string baseSummaryText,
        int maxCompletionTokens,
        string? reasoningEffort) => new(
        Endpoint: "dummy-summary",
        Model: model,
        SystemPrompt: @"You are a friendly teacher who makes hard ideas feel obvious.
Write in a warm, conversational tone for a smart reader with zero background knowledge.
Use 3–5 short paragraphs. No headings, no bullet points, no numbered lists.",
        UserPrompt: $@"Explain this chapter in the clearest, most human way possible.
Focus on:
- why this matters
- what the author is really getting at
- why someone should care
- how it connects (or doesn't) to modern life

Be direct, vivid, and helpful without dumbing it down.

{chapterContext}

Chapter summary:
{baseSummaryText}",
        MaxCompletionTokens: maxCompletionTokens,
        Temperature: null,
        ReasoningEffort: reasoningEffort);

    private const string LearnMoreSystemPrompt =
        "You are a scholarly explainer with expertise in philosophy, critical theory, " +
        "literature, history, and cultural studies. Provide nuanced, intellectually rich " +
        "analysis that bridges academic and accessible discourse.";

    /// <summary>
    /// The vocabulary "learn more" deep dive. Returns HTML because it is
    /// rendered straight into the reader's panel.
    /// </summary>
    /// <remarks>
    /// The image rules are strict on purpose and are the part most worth
    /// reviewing: a hallucinated Wikimedia URL renders as a broken image in the
    /// panel, so "skip images entirely" is the better answer whenever the model
    /// is unsure.
    /// </remarks>
    public static AiResponsesCall LearnMore(
        string model,
        string term,
        string? bookTitle,
        string? sourcePath,
        string? definition,
        string? passageContext,
        int maxOutputTokens,
        string? reasoningEffort,
        double? temperature)
    {
        var contextParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(bookTitle)) contextParts.Add($"Book: {bookTitle}");
        if (!string.IsNullOrWhiteSpace(sourcePath)) contextParts.Add($"Source path: {sourcePath}");

        return new AiResponsesCall(
            Endpoint: "learn-more",
            Model: model,
            SystemPrompt: LearnMoreSystemPrompt,
            Input: $@"Provide a rich, scholarly 300-400 word deep dive on the term/phrase ""{term}"" that goes beyond dictionary definitions.

Respond as concise HTML with paragraphs, <ul>, <strong>, and include up to 2-3 reliable image URLs and 1-2 reference links (e.g., Wikipedia) that help explain the term.

**Your analysis should explore:**
- Core meaning and etymology
- Historical development and evolution of the concept
- How this term/concept is understood in different academic disciplines (philosophy, literature, sociology, etc.)
- Key thinkers, works, or movements associated with it
- How it appears in popular culture vs. academic discourse
- Common misconceptions or debates surrounding the term
- Relevance to contemporary discussions or current events (if applicable)
- Interesting facts or notable usage examples

IMAGE RULES (strict):
- Prefer upload.wikimedia.org or commons.wikimedia.org images; use fully-qualified HTTPS URLs with underscores instead of spaces.
- Do NOT include images unless you are confident the URL exists and is directly fetchable (ending in .jpg/.png/.jpeg).
- If unsure about an image URL, skip images entirely.

Structure:
- Rich overview paragraph (2-3 sentences)
- Bullet list covering the points above
- A ""Resources"" section with authoritative hyperlinks (plain <a href=""..."">text</a>)
- After the text, include a line ""Images:"" followed by <img src=""..."" alt=""..."" loading=""lazy"" /> for each image (absolute URLs only). Use images that are likely to be stable (e.g., Wikimedia, Wikipedia, major news/edu sites). No base64.

Context: {string.Join(" | ", contextParts)}
Definition (if given): {definition ?? "(none)"}
Relevant passage/context: {passageContext ?? "(none)"}",
            MaxOutputTokens: maxOutputTokens,
            ReasoningEffort: reasoningEffort,
            Temperature: temperature);
    }
}
