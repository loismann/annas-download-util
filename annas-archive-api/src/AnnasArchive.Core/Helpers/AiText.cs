// AnnasArchive.Core has neither implicit usings nor a nullable context enabled.
#nullable enable

using System;
using System.Linq;

namespace AnnasArchive.Core.Helpers;

/// <summary>
/// Pure text handling for model output.
///
/// This lives as a static helper rather than only on
/// <see cref="Services.IAiResponseParser"/> because three of the call sites —
/// SpotifyEndpoints and the two background services — have no injected parser,
/// and threading one into a BackgroundService purely to reach a pure string
/// function would be worse than the duplication it removes.
/// <c>AiResponseParser.StripCodeFences</c> delegates here, so there is still
/// exactly one implementation.
/// </summary>
public static class AiText
{
    /// <summary>
    /// Removes the markdown code fence models wrap JSON in, returning the
    /// payload inside. Text that is not fenced is returned trimmed, unchanged.
    /// </summary>
    public static string StripCodeFences(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```"))
            return trimmed;

        // Drop the opening fence line (``` or ```json), then keep everything up
        // to the closing fence. Stopping at the closing fence, rather than just
        // dropping the final line, also discards commentary a model sometimes
        // adds after the block.
        var lines = trimmed.Split('\n');
        var body = lines
            .Skip(1)
            .TakeWhile(line => !line.TrimStart().StartsWith("```"));

        return string.Join('\n', body).Trim();
    }
}
