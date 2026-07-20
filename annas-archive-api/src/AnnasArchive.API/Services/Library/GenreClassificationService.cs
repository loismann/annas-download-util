namespace AnnasArchive.API.Services.Library;

/// <summary>
/// Service for classifying books into standard genres based on subject keywords.
/// Uses a keyword scoring system to match subjects to standard genres.
/// </summary>
public class GenreClassificationService : IGenreClassificationService
{
    private static readonly string[] StandardGenres =
    {
        "Science Fiction", "Fantasy", "Mystery & Detective", "Thriller", "Romance",
        "Historical Fiction", "Literary Fiction", "Horror", "Adventure", "Young Adult",
        "Children's", "Graphic Novel", "Short Stories", "Classics", "Biography & Memoir",
        "History", "Science & Technology", "Philosophy", "Self-Help", "Business & Economics",
        "Travel", "True Crime", "Essays", "Politics & Current Events", "Religion & Spirituality",
        "Art & Photography", "Cooking & Food", "Health & Fitness", "Poetry", "Drama",
        "Reference", "Uncategorized"
    };

    private static readonly Dictionary<string, string[]> GenreKeywordMap = new()
    {
        ["Science Fiction"] = new[] { "science fiction", "sci-fi", "scifi", "space opera", "cyberpunk", "dystopia", "dystopian", "time travel", "space", "aliens", "future", "robots", "artificial intelligence" },
        ["Fantasy"] = new[] { "fantasy", "magic", "wizards", "dragons", "sword and sorcery", "epic fantasy", "urban fantasy", "paranormal", "mythical", "fairy tale", "elves", "supernatural" },
        ["Mystery & Detective"] = new[] { "mystery", "detective", "crime", "murder", "investigation", "whodunit", "noir", "police procedural", "sleuth", "clues" },
        ["Thriller"] = new[] { "thriller", "suspense", "action", "espionage", "spy", "psychological thriller", "conspiracy", "terrorism" },
        ["Romance"] = new[] { "romance", "love story", "romantic", "love", "relationships", "contemporary romance", "historical romance", "romantic comedy" },
        ["Historical Fiction"] = new[] { "historical fiction", "historical", "period", "world war", "civil war", "victorian", "medieval", "ancient" },
        ["Literary Fiction"] = new[] { "literary fiction", "literary", "contemporary fiction", "modern fiction", "satire", "allegory" },
        ["Horror"] = new[] { "horror", "terror", "scary", "ghost", "vampire", "zombie", "monsters", "haunted", "dark", "gothic" },
        ["Adventure"] = new[] { "adventure", "quest", "journey", "exploration", "expedition", "survival", "treasure", "pirates" },
        ["Young Adult"] = new[] { "young adult", "ya", "teen", "teenage", "coming of age", "high school", "adolescent" },
        ["Children's"] = new[] { "children", "kids", "juvenile", "picture book", "early reader", "middle grade", "bedtime story" },
        ["Graphic Novel"] = new[] { "graphic novel", "comic", "manga", "illustrated", "sequential art" },
        ["Short Stories"] = new[] { "short stories", "anthology", "collection", "novellas", "short fiction" },
        ["Classics"] = new[] { "classic", "classical", "nineteenth century", "19th century", "eighteenth century", "18th century", "masterpiece" },
        ["Biography & Memoir"] = new[] { "biography", "memoir", "autobiography", "life story", "diaries", "letters", "personal narrative", "biographical" },
        ["History"] = new[] { "history", "historical", "civilization", "archaeology", "ancient history", "military history", "social history" },
        ["Science & Technology"] = new[] { "science", "technology", "physics", "biology", "chemistry", "mathematics", "astronomy", "engineering", "computers", "nature" },
        ["Philosophy"] = new[] { "philosophy", "philosophical", "ethics", "logic", "metaphysics", "epistemology", "existentialism", "phenomenology" },
        ["Self-Help"] = new[] { "self-help", "self improvement", "personal development", "motivation", "success", "happiness", "productivity" },
        ["Business & Economics"] = new[] { "business", "economics", "finance", "management", "entrepreneurship", "marketing", "investing", "money", "capitalism" },
        ["Travel"] = new[] { "travel", "tourism", "guidebook", "travelogue", "adventure travel", "cultural exploration", "geography" },
        ["True Crime"] = new[] { "true crime", "criminal", "murder case", "serial killer", "investigation", "forensic", "crime story" },
        ["Essays"] = new[] { "essays", "essay", "nonfiction", "criticism", "commentary", "reflections", "observations" },
        ["Politics & Current Events"] = new[] { "politics", "political", "government", "democracy", "current events", "international relations", "diplomacy", "elections" },
        ["Religion & Spirituality"] = new[] { "religion", "religious", "spirituality", "faith", "theology", "christianity", "buddhism", "islam", "meditation", "prayer" },
        ["Art & Photography"] = new[] { "art", "photography", "painting", "sculpture", "artists", "visual arts", "design", "architecture" },
        ["Cooking & Food"] = new[] { "cooking", "food", "recipes", "cookbook", "culinary", "cuisine", "baking", "gastronomy" },
        ["Health & Fitness"] = new[] { "health", "fitness", "exercise", "nutrition", "diet", "wellness", "medicine", "medical", "yoga" },
        ["Poetry"] = new[] { "poetry", "poems", "verse", "sonnets", "haiku" },
        ["Drama"] = new[] { "drama", "plays", "theater", "theatre", "screenplay", "script" },
        ["Reference"] = new[] { "reference", "encyclopedia", "dictionary", "handbook", "manual", "guide", "textbook", "directory" }
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "fiction", "non-fiction", "nonfiction", "general", "book", "books",
        "literature", "accessible book", "protected daisy", "in library",
        "open library staff picks", "american", "english", "british"
    };

    /// <inheritdoc />
    public string ClassifyGenre(IEnumerable<string>? subjects)
    {
        if (subjects == null)
            return "Uncategorized";

        var subjectArray = subjects.ToArray();
        if (subjectArray.Length == 0)
            return "Uncategorized";

        var scores = new Dictionary<string, int>();

        foreach (var subject in subjectArray)
        {
            var normalized = subject.ToLowerInvariant().Trim();
            foreach (var (genre, keywords) in GenreKeywordMap)
            {
                var score = 0;
                foreach (var keyword in keywords)
                {
                    if (normalized == keyword)
                        score += 10; // Exact match
                    else if (normalized.Contains(keyword))
                        score += 5; // Contains keyword
                    else if (keyword.Contains(normalized) && normalized.Length > 3)
                        score += 2; // Keyword contains subject
                }

                if (score > 0)
                {
                    if (!scores.ContainsKey(genre))
                        scores[genre] = 0;
                    scores[genre] += score;
                }
            }
        }

        if (scores.Count == 0)
            return "Uncategorized";

        return scores.OrderByDescending(kv => kv.Value).First().Key;
    }

    /// <inheritdoc />
    public string[] ExtractTags(IEnumerable<string>? subjects, string? primaryGenre, int limit = 5)
    {
        if (subjects == null)
            return Array.Empty<string>();

        var subjectArray = subjects.ToArray();
        if (subjectArray.Length == 0)
            return Array.Empty<string>();

        return subjectArray
            .Where(s =>
            {
                var lower = s.ToLowerInvariant();
                return !StopWords.Contains(lower) &&
                       !string.Equals(s, primaryGenre, StringComparison.OrdinalIgnoreCase) &&
                       !lower.Contains("fictitious character") &&
                       s.Length > 1 &&
                       !int.TryParse(s, out _);
            })
            .Take(limit)
            .ToArray();
    }
}
