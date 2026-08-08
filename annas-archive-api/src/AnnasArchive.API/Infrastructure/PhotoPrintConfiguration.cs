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

    /// <summary>Where the photo library is read from.</summary>
    public ImmichOptions Immich { get; set; } = new();

    /// <summary>How the order reaches the store.</summary>
    public CvsOptions Cvs { get; set; } = new();
}

/// <summary>
/// The CVS checkout leg. See spec §7 — the automation drives your own signed-in
/// session to the order review page and stops there; the purchase click is
/// manual, and no bot-detection evasion is performed.
/// </summary>
public class CvsOptions
{
    /// <summary>
    /// Playwright storage state (cookies + localStorage) for a signed-in
    /// cvs.com session. Produced by <c>scripts/cvs-session-import.sh</c>, which
    /// converts cookies exported from a real Chrome — CVS fingerprints
    /// Playwright's own Chromium and refuses to show it the login page at all.
    ///
    /// There is no username/password pair to configure: CVS signs in with a
    /// passkey or an emailed one-time code, neither of which can run
    /// unattended. A human therefore signs in once and the automation carries
    /// the resulting session.
    ///
    /// This file is a live credential — whoever holds it is signed in as Paul.
    /// It belongs on the persistent state mount, never in source control.
    /// </summary>
    public string SessionStatePath { get; set; } = string.Empty;

    /// <summary>
    /// Where the review-page screenshot is written when a run parks for your
    /// approval. Defaults alongside the run's print-ready files.
    /// </summary>
    public string ScreenshotDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Sessions expire. Below this age the driver runs; above it, it reports
    /// "sign in again" up front rather than failing deep inside checkout with a
    /// confusing selector error.
    /// </summary>
    public int SessionMaxAgeDays { get; set; } = 30;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SessionStatePath);
}

/// <summary>
/// Connection to the household Immich instance. The print pipeline reads the
/// library through Immich's REST API rather than by walking its storage folders,
/// because that on-disk layout is Immich's private implementation detail and
/// changes between versions.
/// </summary>
public class ImmichOptions
{
    /// <summary>
    /// Internal Docker network address — the app and Immich share a compose
    /// network, so this never leaves the host and needs no TLS. Not the tailnet
    /// URL the browser uses.
    /// </summary>
    public string BaseUrl { get; set; } = "http://immich-server:2283";

    /// <summary>
    /// Immich API key (Account Settings → API Keys). Read-only in practice — the
    /// print flow never writes to Immich.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
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
