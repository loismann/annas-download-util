namespace AnnasArchive.API.Services.PhotoPrint;

/// <summary>
/// A print size CVS sells, measured in inches. Stored as short/long edge rather
/// than width/height because a print has no inherent orientation — the same 4x6
/// sheet is used for both a landscape and a portrait photo. Which edge becomes
/// the width is decided per-photo by <see cref="PrintLayout"/>.
/// </summary>
public sealed record PrintSize(string Code, string DisplayName, double ShortInches, double LongInches)
{
    /// <summary>Square sizes have no orientation to resolve — both edges are equal.</summary>
    public bool IsSquare => Math.Abs(LongInches - ShortInches) < 0.001;

    /// <summary>
    /// Sizes offered in the print UI. Codes are the stable identifier persisted in
    /// the order manifest and sent to the CVS layer; display names are what the
    /// UI shows. Adding a size here is enough to surface it everywhere.
    /// </summary>
    public static readonly IReadOnlyList<PrintSize> Catalog =
    [
        new("4x6",     "4×6",     4.0,  6.0),
        new("5x7",     "5×7",     5.0,  7.0),
        new("8x10",    "8×10",    8.0,  10.0),
        new("4x4",     "4×4",     4.0,  4.0),
        new("8x8",     "8×8",     8.0,  8.0),
        new("wallet",  "Wallet",       2.5,  3.5),
        new("11x14",   "11×14",   11.0, 14.0),
        new("16x20",   "16×20",   16.0, 20.0)
    ];

    private static readonly Dictionary<string, PrintSize> ByCode =
        Catalog.ToDictionary(size => size.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves a persisted size code. Unknown codes are a caller error.</summary>
    public static PrintSize FromCode(string code) =>
        TryFromCode(code, out var size)
            ? size
            : throw new ArgumentException($"Unknown print size code '{code}'.", nameof(code));

    public static bool TryFromCode(string? code, out PrintSize size)
    {
        if (!string.IsNullOrWhiteSpace(code))
            return ByCode.TryGetValue(code, out size!);

        size = null!;
        return false;
    }
}
