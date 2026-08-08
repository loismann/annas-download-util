namespace AnnasArchive.API.Services.Ai;

/// <summary>
/// The prompts behind passage analysis and the three-tier chapter summary.
///
/// The three tiers are one tuned ladder, not three independent prompts, which is
/// why they live together: chunk summaries are written to be synthesised
/// (300-400 words each), sections are written to be summarised again (400-500),
/// and only the final tier is written to be read by a person (700-900). Change
/// one length without the others and the tier below either starves or overruns
/// the tier above. Their budgets rise the same way —
/// <c>AI:MaxCompletionTokens:ChunkSummary</c> → <c>SectionSynthesis</c> →
/// <c>FinalSummary</c> — and the reasoning effort with them.
///
/// <para>Each call still concatenates its instructions and content into a single
/// <c>Input</c> string rather than using <see cref="AiResponsesCall.SystemPrompt"/>.
/// That is deliberate for now: a chapter summary runs twenty-plus calls through
/// these tiers, and switching the request shape is a change worth making on its
/// own, separately from moving the text.</para>
/// </summary>
public static class ChapterSummaryPrompts
{
    // ─── On-demand passage analysis ──────────────────────────────────────

    private const string PassageAnalysisInstructions =
        @"You are an advanced literary analysis assistant with deep knowledge of philosophy, critical theory, and cultural studies. Provide a rich, thoughtful analysis (max 200 words) that goes beyond surface-level reading:

**Analysis should include:**
- What's happening narratively and conceptually
- Philosophical undertones and implicit arguments the author is making
- Literary techniques and their rhetorical effect
- How this passage connects to broader themes in the work
- Academic interpretations and critical perspectives (if applicable)
- Cultural, historical, or political context that enriches understanding
- Connections to other philosophical or literary traditions

Then add a 'Definitions:' section. BE EXTREMELY THOROUGH with definitions - include ALL words/phrases a typical high school student might not know: archaic terms, foreign words/phrases, technical jargon, sophisticated vocabulary, philosophical concepts, brand names, historical items, British/European terms, proper nouns needing context, academic terminology. Err on the side of over-defining.";

    /// <summary>
    /// The reader's "analyse this passage" call.
    /// </summary>
    /// <param name="userPrompt">
    /// Built by <c>ITextProcessingService.BuildAnalysisPrompt</c> — the book
    /// context, any earlier analyses of the same chapter, and the passage.
    /// </param>
    /// <param name="knownWords">
    /// Words the reader has already marked as known. Naming them turns the
    /// definitions section from exhaustive into personal, which is the whole
    /// point of tracking them — so this is a product rule, not a tweak. The
    /// budget rises with it: an exclusion list only earns its place if the model
    /// has room to spend the saved words elsewhere.
    /// </param>
    public static AiResponsesCall PassageAnalysis(
        string model,
        string userPrompt,
        IReadOnlyCollection<string>? knownWords,
        int maxOutputTokens,
        string? reasoningEffort,
        double? temperature)
    {
        var systemPrompt = knownWords is { Count: > 0 }
            ? $"{PassageAnalysisInstructions}\n\nIMPORTANT: The user already knows these words, so DO NOT define them: {string.Join(", ", knownWords)}. Total response can be up to 600 words."
            : $"{PassageAnalysisInstructions}\n\nTotal response can be up to 600 words.";

        return new AiResponsesCall(
            Endpoint: "summarize",
            Model: model,
            Input: $"{systemPrompt}\n\n{userPrompt}",
            MaxOutputTokens: maxOutputTokens,
            ReasoningEffort: reasoningEffort,
            Temperature: temperature);
    }

    // ─── Tier 1: one chunk of the chapter ────────────────────────────────

    private const string ChunkInstructions =
        @"You are an educational guide helping someone deeply understand complex texts. Analyze this passage with rich detail:

1. **What's Happening**: Summarize the main points, arguments, or narrative events
2. **Key Concepts**: Identify and explain central ideas or terminology
3. **Context**: What historical, philosophical, or intellectual background is relevant?
4. **Significance**: Why does this matter? What is the author building toward?

Write 300-400 words that assume the reader is intelligent but may lack specialized background knowledge. Explain references and provide context.";

    public static AiResponsesCall ChunkSummary(
        string model,
        string contextLine,
        string chunk,
        int maxOutputTokens,
        string? reasoningEffort) => new(
        Endpoint: "chunk-summary",
        Model: model,
        Input: $"{ChunkInstructions}\n\nContext: {contextLine}\n\n{chunk}",
        MaxOutputTokens: maxOutputTokens,
        ReasoningEffort: reasoningEffort);

    // ─── Tier 2: several chunk summaries into one section ────────────────

    private const string SectionInstructions =
        @"You are synthesizing multiple passage analyses into a coherent section summary. Create a unified narrative that:

1. **Traces the Development**: How do the ideas/arguments/events progress through these passages?
2. **Identifies Core Themes**: What are the central concerns of this section?
3. **Contextualizes**: What intellectual traditions, historical debates, or prior thinkers is the author engaging with?
4. **Clarifies**: Explain difficult concepts in accessible terms

Write 400-500 words. Maintain educational depth while creating a flowing narrative.";

    public static AiResponsesCall SectionSynthesis(
        string model,
        string contextLine,
        IEnumerable<string> chunkSummaries,
        int maxOutputTokens,
        string? reasoningEffort) => new(
        Endpoint: "section-synthesis",
        Model: model,
        Input: $"{SectionInstructions}\n\nContext: {contextLine}\n\n{string.Join("\n\n---\n\n", chunkSummaries)}",
        MaxOutputTokens: maxOutputTokens,
        ReasoningEffort: reasoningEffort);

    // ─── Tier 3: the summary a person reads ──────────────────────────────

    private const string FinalInstructions =
        @"Create a comprehensive 700-900 word educational summary of this chapter that helps someone truly understand and appreciate the material.

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

    public static AiResponsesCall FinalSummary(
        string model,
        IEnumerable<string> contextParts,
        IEnumerable<string> sectionSummaries,
        int maxOutputTokens,
        string? reasoningEffort) => new(
        Endpoint: "final-summary",
        Model: model,
        Input: $"{FinalInstructions}\n\nBook context: {string.Join(" | ", contextParts)}\n\nSection summaries:\n{string.Join("\n\n---\n\n", sectionSummaries)}",
        MaxOutputTokens: maxOutputTokens,
        ReasoningEffort: reasoningEffort);
}
