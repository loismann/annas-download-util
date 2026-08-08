// AnnasArchive.Core has neither implicit usings nor a nullable context enabled,
// so both are declared explicitly here rather than adding CS8632 warnings.
#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnnasArchive.Core.Helpers;

/// <summary>
/// The one place a string becomes a filename.
///
/// This replaced six hand-written copies that had quietly drifted apart. Most
/// of them were built on <see cref="Path.GetInvalidFileNameChars"/> alone,
/// which is a trap: on Linux — where this app actually runs, in Docker — that
/// returns exactly two characters, <c>'\0'</c> and <c>'/'</c>. Everything else
/// (control characters, <c>&lt; &gt; : " | ? *</c>, a leading <c>..</c>) sailed
/// straight through into a real path.
///
/// Two intents, deliberately separate, because they must not behave the same:
///
/// <list type="bullet">
/// <item><see cref="ForKey"/> — the input <em>is</em> the identity (a Dropbox
/// path used as a cache folder name). Separators are flattened to <c>_</c> so
/// the whole path stays distinguishable. Stripping directories here would
/// collapse two different books with the same filename onto one cache entry,
/// and would rename every existing cache folder on disk.</item>
/// <item><see cref="ForUserInput"/> — the input is untrusted and only the leaf
/// name matters (an upload, a book title, a video title). Directory components
/// are discarded outright, which is what makes traversal impossible rather than
/// merely inconvenient.</item>
/// </list>
/// </summary>
public static class SafeFileName
{
    /// <summary>
    /// Union of Windows' and Unix's restrictions, so a name written on one
    /// platform stays valid on the other. Not <see cref="Path.GetInvalidFileNameChars"/>
    /// alone — see the type remarks for why that is nearly a no-op on Linux.
    /// </summary>
    private static readonly HashSet<char> InvalidChars = new(
        Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })
    );

    /// <summary>Longest name we will produce for cache keys.</summary>
    private const int DefaultKeyLength = 200;

    /// <summary>
    /// Most filesystems cap a single component at 255 bytes.
    /// </summary>
    private const int DefaultNameLength = 255;

    /// <summary>
    /// Flattens a string into a single safe filename component while preserving
    /// its identity — <c>/Books/Foo.epub</c> becomes <c>_Books_Foo.epub</c>.
    ///
    /// Used for cache keys, where the value on disk must stay stable: changing
    /// what this returns orphans every existing cached AI summary and silently
    /// re-bills regenerating them.
    /// </summary>
    public static string ForKey(string? input, int maxLength = DefaultKeyLength)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var flattened = new string(input.Select(c => InvalidChars.Contains(c) ? '_' : c).ToArray());
        return flattened.Length > maxLength ? flattened[..maxLength] : flattened;
    }

    /// <summary>
    /// Reduces untrusted text to a safe leaf filename: directory components are
    /// dropped, control characters removed, invalid characters replaced, and
    /// relative-path segments neutralised.
    /// </summary>
    /// <param name="input">Untrusted text — an uploaded filename, a book or video title.</param>
    /// <param name="maxLength">Cap on the result. The extension is preserved when truncating.</param>
    /// <param name="fallback">Returned when nothing survives sanitisation (e.g. input was "..." or all separators).</param>
    public static string ForUserInput(
        string? input,
        int maxLength = DefaultNameLength,
        string fallback = "untitled")
    {
        if (string.IsNullOrWhiteSpace(input))
            return fallback;

        // Keep only the leaf. Both separators are checked regardless of host OS,
        // since the string may well have come from a Windows client.
        var name = input;
        var lastSeparator = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (lastSeparator >= 0)
            name = name[(lastSeparator + 1)..];

        // Drop control characters, including the null byte used to truncate a
        // path early in the C libraries underneath us.
        name = new string(name.Where(c => c >= 0x20 && c != 0x7F).ToArray());

        name = new string(name.Select(c => InvalidChars.Contains(c) ? '_' : c).ToArray());

        // Collapse relative segments. Looping because a single pass over "...."
        // leaves ".." behind; each pass strictly shortens the string, so this
        // terminates.
        while (name.Contains(".."))
            name = name.Replace("..", "_");

        // A leading dot hides the file; a trailing one is stripped by some
        // filesystems, which would make the name we return differ from the name
        // on disk.
        name = name.Trim().Trim('.').Trim();

        if (name.Length > maxLength)
        {
            var ext = Path.GetExtension(name);
            // A pathological extension longer than the whole budget would make
            // the base length negative, so clamp before slicing.
            if (ext.Length >= maxLength)
                return ext[..maxLength].TrimStart('.');

            var baseName = Path.GetFileNameWithoutExtension(name);
            name = baseName[..Math.Min(baseName.Length, maxLength - ext.Length)] + ext;
        }

        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    /// <summary>
    /// A safe path segment for a folder a human will browse — an audiobook's
    /// <c>Author/Title (Year)</c>, say.
    ///
    /// Identical hardening to <see cref="ForUserInput"/>, and differs from it in exactly
    /// one respect: an invalid character collapses to a **space** rather than an
    /// underscore, and runs of spaces collapse to one. In a media library the result is
    /// read by a person and by Audiobookshelf's own scanner, and
    /// <c>Nineteen Eighty_Four_ A Novel</c> is materially worse than
    /// <c>Nineteen Eighty Four A Novel</c> for both.
    ///
    /// The input is external — a catalogue lookup or a model's answer — so the traversal
    /// and control-character handling is not decoration. This exists so that reasoning
    /// lives in one place instead of being re-derived by every caller that wants
    /// readable output.
    /// </summary>
    public static string ForReadablePathSegment(
        string? input,
        int maxLength = DefaultNameLength,
        string fallback = "untitled")
    {
        if (string.IsNullOrWhiteSpace(input))
            return fallback;

        // Substitute *before* delegating. Doing it afterwards cannot work: ForUserInput
        // has already turned every invalid character into '_', and at that point an
        // underscore this class introduced is indistinguishable from one the title
        // genuinely contains.
        var spaced = new string(input.Select(c => InvalidChars.Contains(c) ? ' ' : c).ToArray());
        var collapsed = string.Join(' ', spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var hardened = ForUserInput(collapsed, maxLength, fallback);
        if (hardened == fallback)
            return fallback;

        // ForUserInput neutralises ".." to "_", which is kept deliberately — it marks
        // that something was removed. Only the spacing needs settling again, since
        // truncation can leave a trailing space or dot that some filesystems strip,
        // making the name we return differ from the name on disk.
        var tidied = string.Join(' ', hardened.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .TrimEnd('.', ' ');

        // Nothing readable left means nothing useful was there: "..." survives
        // neutralisation as a bare "_", which is a legal folder name but tells a person
        // browsing the library less than "untitled" does.
        return tidied.Any(char.IsLetterOrDigit) ? tidied : fallback;
    }
}
