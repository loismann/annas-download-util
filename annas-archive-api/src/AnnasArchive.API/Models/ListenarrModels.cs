namespace AnnasArchive.API.Models;

/// <summary>
/// Narrow read-only contract for the Listenarr endpoints used during the
/// infrastructure gate. Keep these models intentionally smaller than
/// Listenarr's complete API; unknown upstream properties are ignored by
/// System.Text.Json.
/// </summary>
public sealed record ListenarrReady(
    bool IsReady,
    string? Status,
    bool DatabaseConnected,
    bool MigrationsCurrent);

public sealed record ListenarrServiceHealth(
    string? Status,
    string? Version,
    string? Uptime,
    ListenarrDownloadClientHealth? DownloadClients,
    ListenarrExternalApiHealth? ExternalApis);

public sealed record ListenarrDownloadClientHealth(
    string? Status,
    int Connected,
    int Total,
    IReadOnlyList<ListenarrNamedStatus>? Clients);

public sealed record ListenarrExternalApiHealth(
    string? Status,
    int Connected,
    int Total,
    IReadOnlyList<ListenarrNamedStatus>? Apis);

public sealed record ListenarrNamedStatus(string? Name, string? Status, string? Type);

public sealed record ListenarrSystemInfo(
    string? Version,
    string? OperatingSystem,
    string? Runtime,
    string? Uptime,
    DateTimeOffset StartTime);

public sealed record ListenarrRootFolder(int Id, string? Name, string? Path, bool IsDefault);

public sealed record ListenarrQualityProfile(int Id, string? Name, bool IsDefault);

public sealed record ListenarrIndexer(
    int Id,
    string? Name,
    string? Type,
    string? Implementation,
    bool IsEnabled,
    bool EnableInteractiveSearch,
    bool? LastTestSuccessful);

public sealed record ListenarrDownloadClient(
    string? Id,
    string? Name,
    string? Type,
    bool IsEnabled);

public sealed record ListenarrLibraryItem(
    int Id,
    string? Asin,
    string? Title,
    IReadOnlyList<string>? Authors,
    IReadOnlyList<string>? Narrators,
    IReadOnlyList<string>? Isbn,
    bool Monitored,
    string? FilePath);

public sealed record ListenarrAudibleSearchResponse(
    IReadOnlyList<ListenarrAudibleSearchResult>? Results,
    int? TotalResults);

public sealed record ListenarrAudibleSearchResult(
    string? Asin,
    string? Title,
    string? Subtitle,
    IReadOnlyList<ListenarrAudibleAuthor>? Authors,
    IReadOnlyList<ListenarrAudibleNarrator>? Narrators,
    IReadOnlyList<ListenarrAudibleSeries>? Series,
    IReadOnlyList<ListenarrAudibleGenre>? Genres,
    string? ImageUrl,
    int? RuntimeLengthMin,
    int? LengthMinutes,
    int? RuntimeMinutes,
    string? Language,
    string? BookFormat,
    string? Publisher,
    string? ReleaseDate,
    string? Isbn,
    string? Region);

public sealed record ListenarrAudibleAuthor(string? Asin, string? Name, string? Region);
public sealed record ListenarrAudibleNarrator(string? Name);
public sealed record ListenarrAudibleSeries(string? Asin, string? Name, string? Position);
public sealed record ListenarrAudibleGenre(string? Asin, string? Name, string? Type);

public sealed record ListenarrAudibleBook(
    string? Asin,
    string? Title,
    string? Subtitle,
    IReadOnlyList<ListenarrAudibleAuthor>? Authors,
    IReadOnlyList<ListenarrAudibleNarrator>? Narrators,
    string? Publisher,
    string? PublishDate,
    string? Description,
    string? ImageUrl,
    int? LengthMinutes,
    string? Language,
    IReadOnlyList<ListenarrAudibleGenre>? Genres,
    IReadOnlyList<ListenarrAudibleSeries>? Series,
    bool? Explicit,
    string? ReleaseDate,
    string? Isbn,
    string? Region,
    string? BookFormat);

public sealed record ListenarrAudiobookSeriesMembership(
    string? SeriesName,
    string? SeriesNumber,
    string? SeriesAsin,
    bool IsPrimary,
    int SortOrder);

public sealed record ListenarrLibraryMetadata(
    string Asin,
    string Source,
    string Region,
    string Title,
    string? Subtitle,
    IReadOnlyList<string> Authors,
    string? ImageUrl,
    string? PublishYear,
    string? PublishedDate,
    string? Series,
    string? SeriesNumber,
    IReadOnlyList<ListenarrAudiobookSeriesMembership> SeriesMemberships,
    string? Description,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Narrators,
    IReadOnlyList<string> Isbn,
    string? Publisher,
    string? Language,
    int? Runtime,
    string? Edition,
    string? Version,
    bool Explicit,
    bool Abridged);

public sealed record ListenarrAddToLibraryRequest(
    ListenarrLibraryMetadata Metadata,
    bool Monitored,
    int? QualityProfileId,
    bool AutoSearch,
    string? DestinationPath = null,
    object? SearchResult = null);

public sealed record ListenarrLibraryAddResponse(
    string? Message,
    ListenarrLibraryItem? Audiobook,
    bool AlreadyExisted);

public sealed record ListenarrIndexerSearchResult(
    string? Id,
    string? Title,
    string? Artist,
    string? Source,
    string? PublishedDate,
    string? Format,
    int Score,
    long Size,
    int? Seeders,
    int? Leechers,
    int Grabs,
    int Files,
    string? DownloadType,
    string? Quality,
    string? Language,
    string? DownloadReference);

public sealed record ListenarrSendDownloadResponse(string? DownloadId, string? Message);

public sealed record ListenarrDownload(
    string Id,
    int? AudiobookId,
    string? Title,
    string? Artist,
    string? Status,
    decimal Progress,
    long TotalSize,
    long DownloadedSize,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage,
    string? DownloadClientId,
    string? DownloadClientName,
    string? ImportBlockReason,
    IReadOnlyList<string>? ImportBlockMessages,
    int ImportAttempts);

public sealed record AudiobookRequestStatusResponse(
    int ListenarrId,
    string Asin,
    string Title,
    string State,
    decimal Progress,
    string? DownloadId,
    long? TotalSize,
    long? DownloadedSize,
    string? DownloadClient,
    string? Error,
    IReadOnlyList<string> ImportBlockMessages,
    string? AudiobookshelfItemId,
    bool CanCancel,
    bool CanRetryImport,
    DateTimeOffset UpdatedAt);

public sealed record AudiobookRequestCancelRequest(bool RemoveFromClient = true);

/// <summary>Preferences the user stated themselves. They never widen what the
/// app will do — they only force an automatic acquisition back to manual
/// release review, because no automatic release match can prove a narrator or
/// language preference before the file arrives.</summary>
public sealed record AudiobookRequestPreviewRequest(
    string? Asin,
    string? Region,
    string? NarratorPreference = null,
    string? LanguagePreference = null);

public sealed record AudiobookRequestConfirmRequest(string? PreviewToken);

public sealed record AudiobookRequestPreviewResponse(
    string PreviewToken,
    DateTimeOffset ExpiresAt,
    string Asin,
    string Title,
    IReadOnlyList<string> Authors,
    IReadOnlyList<string> Narrators,
    string? Language,
    string? Format,
    bool Abridged,
    string QualityProfile,
    bool AutoSearch,
    string AutoSearchReason,
    bool AlreadyRequested);

public sealed record AudiobookRequestResponse(
    int ListenarrId,
    string Asin,
    string Title,
    string Status,
    bool AlreadyExisted,
    bool RequesterAdded);

public sealed record AudiobookReleaseOption(
    string SelectionToken,
    DateTimeOffset ExpiresAt,
    string Title,
    string Source,
    string DownloadType,
    string? Format,
    string? Quality,
    string? Language,
    long Size,
    int? Seeders,
    int? Leechers,
    int Grabs,
    int Files,
    int Score);

public sealed record AudiobookReleaseSearchResponse(
    int ListenarrId,
    string Asin,
    string Title,
    IReadOnlyList<AudiobookReleaseOption> Releases);

public sealed record AudiobookReleaseGrabResponse(
    int ListenarrId,
    string Asin,
    string DownloadId,
    string Status);

public sealed record AudiobookRequestRecord(
    int ListenarrId,
    string Asin,
    string Title,
    string Author,
    string Status,
    string? AbsItemId,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AudiobookSearchResult(
    string Asin,
    string Title,
    string? Subtitle,
    IReadOnlyList<string> Authors,
    IReadOnlyList<string> Narrators,
    string? Publisher,
    string? ReleaseDate,
    string? Language,
    string? Format,
    int? RuntimeMinutes,
    string? ImageUrl,
    IReadOnlyList<string> Genres,
    IReadOnlyList<AudiobookSeriesMembership> Series,
    string Availability,
    string? AvailabilityReason,
    string? OwnedAudiobookshelfId,
    int? ListenarrId,
    bool RequestTracked = false);

public sealed record AudiobookSeriesMembership(string? Asin, string? Name, string? Position);

public sealed record AudiobookSearchResponse(
    string Query,
    string Region,
    string? Language,
    int TotalResults,
    IReadOnlyList<AudiobookSearchResult> Results);

// ─── Series requests (phase 6) ───────────────────────────────────────────

public sealed record AudiobookSeriesPreviewRequest(string? SeriesAsin, string? Region);

/// <summary>ConfirmLarge is the deliberate second confirmation required above
/// the standard ceiling; it is honoured for administrators only.</summary>
public sealed record AudiobookSeriesConfirmRequest(
    string? PreviewToken,
    IReadOnlyList<string>? Asins,
    bool ConfirmLarge = false);

/// <summary>Classification is "owned", "requested", "requestable",
/// "ambiguous" (a member the catalog did not return a single edition for) or
/// "unavailable". Only "requestable" members may be confirmed.</summary>
public sealed record AudiobookSeriesMember(
    string Classification,
    string? Position,
    string Title,
    string? Asin,
    AudiobookSearchResult? Edition,
    string? Note);

public sealed record AudiobookSeriesPreviewResponse(
    string PreviewToken,
    DateTimeOffset ExpiresAt,
    string SeriesAsin,
    string? SeriesName,
    string Region,
    int OwnedCount,
    int RequestedCount,
    int RequestableCount,
    int UnavailableCount,
    int RequestCeiling,
    bool ExceedsCeiling,
    IReadOnlyList<AudiobookSeriesMember> Members);

public sealed record AudiobookSeriesRequestOutcome(
    string Asin,
    string Title,
    string Outcome,
    int? ListenarrId,
    string? Error);

public sealed record AudiobookSeriesConfirmResponse(
    string SeriesAsin,
    int RequestedCount,
    int AlreadyExistedCount,
    int FailedCount,
    IReadOnlyList<AudiobookSeriesRequestOutcome> Outcomes);

// ─── AI discovery (phase 5) ──────────────────────────────────────────────

public sealed record AiAudiobookSearchRequest(string? Query, int? Count, string? Region);

/// <summary>One AI-proposed work. It deliberately carries no ASIN, release,
/// or URL: identity is established only by deterministic catalog resolution,
/// so a hallucinated identifier can never reach Listenarr.</summary>
public sealed record AudiobookDiscoveryCandidate(
    string Title,
    string? Author,
    int? Year,
    string? Series,
    string? SeriesNumber,
    string? NarratorPreference,
    string? Reason);

/// <summary>Resolution is "resolved" (one confident edition), "ambiguous"
/// (the user must pick an edition) or "notFound". Only a resolved or
/// user-selected edition carries a requestable ASIN.</summary>
public sealed record AudiobookDiscoveryResult(
    string Resolution,
    string SuggestedTitle,
    string? SuggestedAuthor,
    string? Reason,
    AudiobookSearchResult? Match,
    IReadOnlyList<AudiobookSearchResult> Choices,
    string? ResolutionNote = null);

public sealed record AudiobookDiscoveryResponse(
    string? Summary,
    string Region,
    int ResolvedCount,
    int AmbiguousCount,
    int NotFoundCount,
    int OwnedCount,
    IReadOnlyList<AudiobookDiscoveryResult> Results);

/// <summary>Safe app-facing status. It deliberately contains no upstream URL,
/// API key, credentials, download paths, or raw provider responses.</summary>
public sealed record ListenarrIntegrationStatus(
    bool Enabled,
    bool Configured,
    bool Reachable,
    bool Ready,
    string? ReadyStatus,
    bool DatabaseConnected,
    bool MigrationsCurrent,
    string? ServiceHealth,
    string? Version,
    int RootFolderCount,
    int QualityProfileCount,
    int EnabledIndexerCount,
    int EnabledDownloadClientCount,
    int LibraryItemCount,
    bool ReadOnlyGatePassed,
    IReadOnlyList<string> GateFailures);
