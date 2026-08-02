namespace AnnasArchive.API.Infrastructure;

/// <summary>
/// Settings for the Google Photos → CVS print pipeline
/// (DOCS/features/google-photos-cvs-print-automation-spec.md).
/// </summary>
public class PhotoPrintConfiguration
{
    public const string SectionName = "PhotoPrint";

    /// <summary>
    /// Feature switch. Off by default so the endpoints stay off the surface until
    /// Immich is actually running and configured — same pattern as Gaming/YouTube.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Where print-ready renders are written, one subdirectory per run. Should sit
    /// on the media mount, not the container filesystem — renders of a 16x20 are
    /// large and a rebuild would otherwise discard an in-progress order.
    /// </summary>
    public string OutputRoot { get; set; } = string.Empty;

    /// <summary>Render resolution. 300 is standard photographic print quality.</summary>
    public int Dpi { get; set; } = PrintLayoutDefaults.Dpi;

    /// <summary>
    /// JPEG quality for print output. Deliberately higher than a web default —
    /// compression artefacts that are invisible on screen become visible on paper,
    /// and the file is uploaded once and then discarded.
    /// </summary>
    public int JpegQuality { get; set; } = 95;

    /// <summary>Zip code of the CVS store used for pickup.</summary>
    public string PickupZipCode { get; set; } = string.Empty;

    /// <summary>
    /// Ceiling on prints in a single run. A runaway selection is real money, so
    /// the server rejects oversized orders rather than trusting the UI.
    /// </summary>
    public int MaxPrintsPerRun { get; set; } = 200;
}

/// <summary>
/// Mirrors <c>PrintLayout.DefaultDpi</c> for use in a property initializer, which
/// cannot reference the Services layer from Infrastructure without inverting the
/// dependency direction the rest of the project uses.
/// </summary>
internal static class PrintLayoutDefaults
{
    public const int Dpi = 300;
}
