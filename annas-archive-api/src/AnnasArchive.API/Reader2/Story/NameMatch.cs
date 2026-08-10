using AnnasArchive.API.Reader2.Vocabulary;

namespace AnnasArchive.API.Reader2.Story;

/// <summary>
/// Whether two names are the same person, decided in C# rather than by a model.
///
/// <para>Casefolding and diacritics come from <see cref="TermNorm"/> — there is
/// one implementation of "the same word as far as a reader is concerned" and this
/// is not a second one. What is added here is name-shaped: a stripped title, and
/// a dropped patronymic.</para>
///
/// <para><b>The rules are deliberately narrow.</b> This decides only what is
/// mechanically certain; everything requiring judgement — <i>Pierre</i> for
/// <i>Pyotr</i>, a nickname, a nom de guerre — reaches the model as an alias hint
/// and the merger treats it as a proposal. Widening these rules to catch more
/// would start fusing people, and a wrong merge is a story the reader cannot see
/// is wrong.</para>
/// </summary>
public static class NameMatch
{
    /// <summary>
    /// Forms of address that attach to a name without changing whose it is.
    /// Ranks are here as well as civil titles, because the military lens meets
    /// the same person as a colonel and later as a general.
    /// </summary>
    private static readonly HashSet<string> Titles = new(StringComparer.Ordinal)
    {
        "mr", "mrs", "ms", "miss", "dr", "doctor", "prof", "professor",
        "sir", "dame", "lord", "lady", "master", "mistress",
        "king", "queen", "prince", "princess", "duke", "duchess", "count", "countess",
        "earl", "baron", "baroness", "marquis", "marquess", "viscount", "tsar", "czar",
        "emperor", "empress", "archduke", "don", "dona", "herr", "frau", "monsieur",
        "madame", "mademoiselle", "signor", "signora", "senor", "senora",
        "father", "brother", "sister", "saint", "st", "rev", "reverend", "bishop",
        "general", "gen", "marshal", "fieldmarshal", "admiral", "commodore",
        "colonel", "col", "lieutenant", "lt", "captain", "capt", "commander", "cdr",
        "major", "maj", "sergeant", "sgt", "corporal", "cpl", "private", "pvt",
        "ensign", "cadet", "brigadier", "commissar", "hetman", "pasha", "bey"
    };

    /// <summary>
    /// Russian patronymic endings. A patronymic sits between the given name and
    /// the surname and is dropped in half the places a character is named, which
    /// is why a long Russian novel is the hardest case this has to survive.
    /// </summary>
    private static readonly string[] PatronymicEndings =
        ["ovich", "evich", "ievich", "yevich", "ich", "ovna", "evna", "yevna", "ichna", "inichna"];

    /// <summary>
    /// The comparable form of a name: casefolded, diacritics stripped, titles
    /// removed. Empty when nothing is left, which never matches anything.
    /// </summary>
    public static string Key(string? name) => string.Join(' ', Words(name));

    /// <summary>
    /// Every form this name could reasonably be written as — the name itself, and
    /// the name without its patronymic when it has one.
    /// </summary>
    public static IReadOnlyCollection<string> Variants(string? name)
    {
        var words = Words(name);
        if (words.Count == 0) return [];

        var variants = new HashSet<string>(StringComparer.Ordinal) { string.Join(' ', words) };

        // Only between two other words. A patronymic used alone, or as a surname,
        // is the name somebody is actually known by and dropping it loses them.
        if (words.Count >= 3)
        {
            var kept = words.Where((w, i) => i == 0 || i == words.Count - 1 || !IsPatronymic(w)).ToArray();
            if (kept.Length < words.Count) variants.Add(string.Join(' ', kept));
        }

        return variants;
    }

    /// <summary>Whether two names are mechanically the same person.</summary>
    public static bool Same(string? a, string? b)
    {
        var left = Variants(a);

        return left.Count > 0 && Variants(b).Any(left.Contains);
    }

    /// <summary>Whether any name this actor answers to matches <paramref name="name"/>.</summary>
    public static bool Answers(Actor actor, string? name) => actor.AllNames.Any(n => Same(n, name));

    /// <summary>Articles carry no more of a name than a title does.</summary>
    private static readonly HashSet<string> Articles = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "le", "la", "les", "el", "il", "der", "die", "das", "den"
    };

    private static IReadOnlyList<string> Words(string? name)
    {
        var words = TermNorm.Of(name)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !Articles.Contains(w))
            .ToArray();

        var kept = words.Where(w => !Titles.Contains(w.TrimEnd('.'))).ToArray();

        // A name that is nothing but a title keeps it — "The General" is how some
        // books name a character, and stripping it to nothing loses them. Dropping
        // the article first is what makes this work: without it "The General"
        // reduces to "the", which then matches every other "The …" in the book.
        return kept.Length > 0 ? kept : words;
    }

    private static bool IsPatronymic(string word) =>
        word.Length > 5 && PatronymicEndings.Any(e => word.EndsWith(e, StringComparison.Ordinal));
}
