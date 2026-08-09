namespace AnnasArchive.API.Services.Ai;

/// <summary>
/// The prompt behind the character relationship graph.
///
/// Beside the other prompt files rather than inline in the endpoint, matching
/// <see cref="ChapterSummaryPrompts"/> and <see cref="FlashcardPrompts"/>: it is
/// a specification of the JSON the model must return, and it was 46 of the
/// handler's 159 lines.
///
/// <para>The standing instruction that matters most is the last one — say
/// nothing that is not in the summaries. The graph is shown to somebody who is
/// part-way through the book, so a model that infers ahead of the reader spoils
/// it.</para>
/// </summary>
public static class CharacterGraphPrompts
{
    /// <summary>The graph shape, the naming rules, and the no-spoilers rule.</summary>
    public const string SystemPrompt =
        @"You are a character relationship analyzer for novels. Analyze the provided story summaries and create a network graph of character relationships.

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

    /// <summary>
    /// The summaries to read, which are the only permitted source of fact.
    /// </summary>
    public static string UserPrompt(string? bookTitle, IEnumerable<string> summaries)
    {
        var summaryText = string.Join("\n\n---\n\n",
            summaries.Select((s, i) => $"Summary {i + 1}:\n{s}"));

        return $@"Analyze the characters and their relationships from these story summaries:

Book: {bookTitle ?? "Unknown"}

Story Summaries:
{summaryText}

Create a character relationship network graph based ONLY on information in these summaries.";
    }
}
