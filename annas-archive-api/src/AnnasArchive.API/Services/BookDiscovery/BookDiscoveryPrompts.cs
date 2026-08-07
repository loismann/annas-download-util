using System.Text.Json;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services.Ai;

namespace AnnasArchive.API.Services.BookDiscovery;

/// <summary>
/// The prompts behind the five book-discovery routes.
///
/// These are the actual specification of what each endpoint returns — the rules
/// about omnibus editions, spoiler-free summaries and never inventing a title
/// are product decisions, not plumbing. Buried in the middle of a handler they
/// could only be reviewed by reading around forty lines of HTTP either side of
/// them, and could not be tested at all without a live OpenAI call. Here they
/// are ordinary strings a test can assert on.
///
/// Each builder returns a complete <see cref="AiChatCall"/> so the token budget
/// and temperature travel with the prompt they were tuned for.
/// </summary>
public static class BookDiscoveryPrompts
{
    /// <summary>The word budget per book, tightened when the query produced a
    /// long extracted list. 60+ titles at 45 words each does not fit in the
    /// completion budget, and the model truncates mid-array rather than
    /// shortening on its own.</summary>
    public const int PerBookWordLimit = 45;
    public const int TightPerBookWordLimit = 24;
    public const int TightPerBookThreshold = 60;

    public static AiChatCall SuggestAuthors(string model, string bookTitle) => new(
        Endpoint: "suggest-authors",
        Model: model,
        SystemPrompt: "You are a book metadata expert. Given a book title, suggest the 3-5 most likely authors sorted by probability. Return ONLY valid JSON with no markdown, explanation, or additional text.",
        UserPrompt: $@"Book title: ""{bookTitle}""

Return ONLY a JSON array of likely authors sorted by probability (most likely first). Each entry should have ""author"" (full name) and ""confidence"" (high/medium/low).

Example format:
[
  {{""author"": ""J.R.R. Tolkien"", ""confidence"": ""high""}},
  {{""author"": ""Christopher Tolkien"", ""confidence"": ""medium""}}
]

If the title is ambiguous or you don't recognize it, return an empty array: []

Do NOT include any markdown formatting, explanations, or text outside the JSON array.",
        MaxCompletionTokens: 500,
        Temperature: 0.3);

    public static AiChatCall RelatedBooks(string model, string bookTitle, string author) => new(
        Endpoint: "related-books",
        Model: model,
        SystemPrompt: "You are a literary expert with comprehensive knowledge of book series and author bibliographies. Given a book title and author, identify related books. Return ONLY valid JSON with no markdown or explanations.",
        UserPrompt: $@"Book: ""{bookTitle}"" by {author}

Provide:
1. A summary of the current series (if this book is part of a series)
2. Other books in the SAME SERIES (if this book is part of a series)
3. OTHER SERIES by this author (different series they've written) with ALL books in each series

Return ONLY this JSON structure:
{{
  ""seriesSummary"": ""A 2-3 sentence overview of the current series, its themes, and significance. Null if not part of a series."",
  ""sameSeries"": [
    {{""title"": ""Book Title"", ""order"": 1, ""description"": ""Brief 1-line description""}}
  ],
  ""seriesName"": ""Series Name (optional)"",
  ""seriesSearchQuery"": ""Search query to find series books (optional)"",
  ""otherSeries"": [
    {{
      ""seriesName"": ""Series Name"",
      ""bookCount"": 3,
      ""books"": [
        {{""title"": ""Book 1 Title"", ""order"": 1, ""description"": ""Brief description""}}
      ],
      ""description"": ""Brief 1-line description of series"",
      ""summary"": ""2-3 sentence overview of this series""
    }}
  ]
}}

Rules:
- If the book is NOT part of a series, return null for seriesSummary
- If the series has MANY books, still return ALL known published titles (no ellipses)
- If you cannot list all titles, set seriesName and seriesSearchQuery for lookup
- For otherSeries, include ALL books in each series in the ""books"" array
- Only include PUBLISHED books (no unreleased/rumored books)
- Sort all books by publication/reading order
- For otherSeries, include 3-5 most notable series
- Each series summary should be 2-3 sentences covering themes, plot arc, and significance
- Keep individual book descriptions concise (max 15 words)
- Return ONLY the JSON object, no markdown formatting",
        MaxCompletionTokens: 3500,
        Temperature: 0.3);

    private const string BookSearchSystemPrompt =
        @"You are a book discovery assistant. Determine whether the user query is asking for books.
If it is, return a list of relevant books with an engaging, spoiler-free summary of the search.
Return ONLY valid JSON with no markdown or extra text.";

    /// <param name="extractedTitles">
    /// Titles scraped from a URL in the query. When present the model is told to
    /// return those and only those — the point of a "make a list from this page"
    /// query is the page's list, so a plausible invention is a wrong answer.
    /// </param>
    public static AiChatCall BookSearch(string model, string query, IReadOnlyList<string> extractedTitles)
    {
        var hasUrl = ContainsUrl(query);
        var hasExtractedTitles = extractedTitles.Count > 0;
        var maxResults = hasExtractedTitles ? Math.Min(20, extractedTitles.Count) : 20;

        var extractedBlock = hasExtractedTitles
            ? $"ExtractedTitles (from the URL):\n- {string.Join("\n- ", extractedTitles.Take(100))}\n"
            : "ExtractedTitles: None\n";

        return new AiChatCall(
            Endpoint: "book-search",
            Model: model,
            SystemPrompt: BookSearchSystemPrompt,
            UserPrompt: $@"Query: ""{query}""
{extractedBlock}
Return ONLY this JSON structure:
{{
  ""isBookQuery"": boolean,
  ""message"": string|null,
  ""summary"": string|null,
  ""books"": [
    {{
      ""title"": ""Book title"",
      ""author"": ""Author name"",
      ""summary"": ""Spoiler-free note on what makes this book special (2-3 sentences)"",
      ""importance"": ""Context/impact (historical, critical acclaim, cultural influence; 1 sentence)""
    }}
  ]
}}

Rules:
- If the query is NOT about books, set isBookQuery=false and return a brief message.
- If ExtractedTitles are provided, return those titles in that order and fill in author if known; do not invent titles not present.
- If ExtractedTitles are not provided, return up to {maxResults} books when the query includes a URL or asks for a list; otherwise return 10-25.
- Make the summary 2-3 sentences, spoiler-free, and engaging (max 80 words).
- The summary should briefly explain what the list represents and why it's notable (e.g., award significance, era, genre influence).
- Keep each book summary and importance concise (max {WordLimitFor(extractedTitles)} words each).",
            MaxCompletionTokens: hasUrl ? 6000 : 2000,
            Temperature: 0.3,
            IsRetry: false);
    }

    /// <summary>
    /// The second attempt when the first returned an empty list. It drops the
    /// structure-detection framing and simply insists on books, on a fixed
    /// gpt-4o rather than the configured deep model — the deep model already
    /// declined once, so retrying it identically is the one thing guaranteed
    /// not to help.
    /// </summary>
    public static AiChatCall BookSearchRetry(string query, IReadOnlyList<string> extractedTitles) => new(
        Endpoint: "book-search",
        Model: "gpt-4o",
        SystemPrompt: BookSearchSystemPrompt,
        UserPrompt: $@"Query: ""{query}""

Return ONLY this JSON structure:
{{
  ""isBookQuery"": true,
  ""message"": null,
  ""summary"": string|null,
  ""books"": [
    {{
      ""title"": ""Book title"",
      ""author"": ""Author name"",
      ""summary"": ""Spoiler-free note on what makes this book special (2-3 sentences)"",
      ""importance"": ""Context/impact (historical, critical acclaim, cultural influence; 1 sentence)""
    }}
  ]
}}

Rules:
- You MUST return 10-20 books. Do not return an empty list.
- Make the summary 2-3 sentences, spoiler-free, and engaging (max 80 words).
- Keep each book summary and importance concise (max {WordLimitFor(extractedTitles)} words each).",
        MaxCompletionTokens: 2500,
        Temperature: 0.4,
        IsRetry: true);

    public static AiChatCall MatchSeriesBooks(string model, MatchSeriesBooksRequest request)
    {
        var booksJson = JsonSerializer.Serialize(request.Books, new JsonSerializerOptions { WriteIndented = true });

        return new AiChatCall(
            Endpoint: "match-series-books",
            Model: model,
            SystemPrompt: @"You are an expert book matcher. You analyze search results from a library database and select the best match for each book in a series.

Your task: For each book, examine all search result candidates and select the BEST match based on:
1. Title match (handle variations like subtitles, series numbers in parentheses)
2. Author match (exact or close match)
3. Format match (if specified)
4. Detect and AVOID: Omnibus editions, anthologies, collections, combined volumes
5. Prefer standalone individual books over compilations

Return ONLY valid JSON with no markdown or explanation.",
            UserPrompt: $@"Series: ""{request.SeriesName ?? "Unknown Series"}""
Author: ""{request.Author}""
Preferred Format: ""{request.PreferredFormat ?? "ANY"}""

For each book below, I'm providing the title we're looking for and the search results. Select the BEST candidate or flag if no good match exists.

Books and Search Results:
{booksJson}

Return ONLY this JSON structure:
{{
  ""matches"": [
    {{
      ""bookTitle"": ""Book title we searched for"",
      ""order"": 1,
      ""status"": ""matched|ambiguous|not_found"",
      ""selectedMd5"": ""md5_of_best_match"",
      ""selectedTitle"": ""Full title from search results"",
      ""confidence"": ""exact|likely|uncertain"",
      ""reason"": ""Brief explanation (e.g., 'Exact title and author match', 'Anthology detected', etc.)""
    }}
  ]
}}

Rules:
- status: ""matched"" if you found a good match, ""ambiguous"" if multiple viable options, ""not_found"" if no good match
- confidence: ""exact"" for perfect matches, ""likely"" for close matches, ""uncertain"" if you're not sure
- ALWAYS avoid omnibus/anthology editions unless that's the ONLY option
- If a book has ""(Books 1-3)"" or ""Complete Series"" in the title, flag it as ambiguous or not_found
- Match format if specified (e.g., only select EPUB if format is EPUB)",
            MaxCompletionTokens: 2000,
            Temperature: 0.2);
    }

    /// <summary>
    /// Index-only payload — asking the model to faithfully echo back 32-char md5
    /// hashes for 50-100+ books risks silent transcription errors that would
    /// misfile a book into the wrong group or drop it from the response
    /// entirely. Small integer indices round-trip reliably; the caller maps them
    /// back to md5 using the same array it sent.
    /// </summary>
    public static AiChatCall GroupSearchResults(string model, IReadOnlyList<GroupableBook> books)
    {
        var indexedBooks = books
            .Select((b, i) => new { index = i, title = b.Title, authors = b.Authors, format = b.Format, year = b.Year })
            .ToList();
        var booksJson = JsonSerializer.Serialize(indexedBooks, new JsonSerializerOptions { WriteIndented = true });

        return new AiChatCall(
            Endpoint: "group-search-results",
            Model: model,
            SystemPrompt: @"You are a library cataloging assistant. You'll receive a JSON array of book search results, each with an index, title, authors, format, and year. Many entries represent the SAME underlying book — a different file format (EPUB/PDF/MOBI/AZW3/etc.) or a duplicate upload/scan of the identical edition.

Your task: group indices that represent the same book together. Format never matters for grouping — EPUB and PDF copies of the same book belong in the same group. Do NOT group:
- Different volumes/books in a series (e.g. a book titled ""Book 2"" or ""#2"" is a DIFFERENT book from ""Book 1"" or the base title with no number)
- Different, unrelated books that merely share a similar title
- Meaningfully different editions (e.g. abridged vs unabridged, a translation vs the original) unless you're confident they're the same core work

Every index from the input must appear in exactly one group in the output. A book with no duplicates is still its own group of one.

Return ONLY valid JSON with no markdown or explanation.",
            UserPrompt: $@"Books:
{booksJson}

Return ONLY this JSON structure:
{{
  ""groups"": [[0, 3, 7], [1], [2, 5]]
}}

Each inner array is a list of indices that are the same book.",
            MaxCompletionTokens: 4000,
            Temperature: 0.1);
    }

    public static bool ContainsUrl(string query) =>
        query.Contains("http://", StringComparison.OrdinalIgnoreCase)
        || query.Contains("https://", StringComparison.OrdinalIgnoreCase);

    private static int WordLimitFor(IReadOnlyList<string> extractedTitles) =>
        extractedTitles.Count >= TightPerBookThreshold ? TightPerBookWordLimit : PerBookWordLimit;
}
