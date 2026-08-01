namespace AnnasArchive.API.Constants;

/// <summary>
/// Shared identifiers for the Date Night feature (see DOCS/features/DATE_NIGHT.md).
///
/// The pool tag lives in Radarr rather than in our own database on purpose: Radarr
/// is already the source of truth for which movies exist, and a tag there survives
/// our app being redeployed, reinstalled, or its database reset. It also means the
/// pool is inspectable and fixable from Radarr's own UI without this app running.
/// </summary>
public static class DateNight
{
    /// <summary>Radarr tag marking a movie as part of the Date Night pool — added
    /// when the pool CSV is imported, removed when a movie graduates to the regular
    /// library after being watched.</summary>
    public const string PoolTag = "date-night-pool";
}
