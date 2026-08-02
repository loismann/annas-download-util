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

public sealed record AudiobookRequestPreviewRequest(string? Asin, string? Region);
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
