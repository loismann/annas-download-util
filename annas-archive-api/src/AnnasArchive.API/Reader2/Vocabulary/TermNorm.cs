using System.Globalization;
using System.Text;

namespace AnnasArchive.API.Reader2.Vocabulary;

/// <summary>
/// The one way a term becomes a key.
///
/// <para>A reader who has learned <i>naïveté</i> has learned <i>naivete</i>, and
/// a reader who met <i>Dasein</i> at the start of a chapter has met <i>dasein</i>
/// at the end of it. Without one normalisation those are separate rows: the
/// exclusion list stops working, the deep dive is bought twice, and the same word
/// appears in the vocabulary panel three times.</para>
///
/// <para>It is also the primary key of <c>r2_vocabulary</c> and the
/// <c>subkey</c> of every cached deep dive, so it has to be stable — which is
/// why it is a pure function here rather than a call to whatever
/// <c>ToLower</c> the caller reached for.</para>
/// </summary>
public static class TermNorm
{
    /// <summary>
    /// Casefolded, diacritics stripped, inner whitespace collapsed, and trimmed
    /// of the punctuation a selection picks up at its edges.
    /// </summary>
    public static string Of(string? term)
    {
        if (string.IsNullOrWhiteSpace(term)) return "";

        // FormD splits an accented character into its base plus a combining mark,
        // which is what makes the marks droppable one by one.
        var decomposed = term.Trim().Normalize(NormalizationForm.FormD);
        var stripped = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;

            // A selection routinely drags in a quote, a comma, or a full stop.
            // Inner punctuation stays: "self-consciousness" is one term, and so
            // is "l'être".
            stripped.Append(char.IsWhiteSpace(c) ? ' ' : c);
        }

        var collapsed = string.Join(' ',
            stripped.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // Trim() as well as Trim(Edges): "— reification …" leaves a space behind
        // each stripped mark, and Trim(Edges) alone stops at the first one.
        return collapsed.Trim(Edges).Trim().ToLowerInvariant().Normalize(NormalizationForm.FormC);
    }

    private static readonly char[] Edges =
        [.. "\"'“”‘’.,;:!?()[]{}—–-…*_ "];

    /// <summary>Whether two terms are the same word as far as the reader is concerned.</summary>
    public static bool Same(string? a, string? b) => Of(a) == Of(b) && Of(a).Length > 0;
}
