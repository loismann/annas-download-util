using AnnasArchive.API.Constants;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Infrastructure;
using AnnasArchive.API.Services;
using AnnasArchive.API.Services.Library;
using AnnasArchive.API.Services.PhotoPrint;
using AnnasArchive.API.Services.Spotify;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Serilog;

namespace AnnasArchive.API.Configuration;

/// <summary>
/// Application service registrations, grouped by the feature they belong to.
/// Split out of ServiceConfiguration, where this was a single 259-line method.
/// </summary>
public static class CoreServiceConfiguration
{
    /// <summary>
    /// Registers core application services.
    /// </summary>
    public static IServiceCollection AddCoreServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddInfrastructureServices(configuration);
        services.AddDateNightServices(configuration);
        services.AddLibraryServices(configuration);
        services.AddPlatformServices(configuration);
        services.AddAiServices(configuration);
        services.AddPhotoPrintServices(configuration);
        services.AddSpotifyServices(configuration);
        services.AddReaderServices(configuration);

        return services;
    }

    /// <summary>
    /// Caches, clock, database stores and the background services.
    ///     ///
    ///     /// Hosted services are started in registration order, and
    ///     /// HouseholdIdentityMigration is documented as needing to be first, so
    ///     /// the order within this method is load-bearing.
    /// </summary>
    private static void AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Memory cache (required by OpenLibraryService for author suggestions caching)
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.AddSingleton<TimeProvider>(TimeProvider.System);

        var configuredDataProtectionPath = configuration["DataProtection:KeysPath"];
        var dataProtectionPath = string.IsNullOrWhiteSpace(configuredDataProtectionPath)
            ? Path.Combine(Directory.GetCurrentDirectory(), "state", "data-protection-keys")
            : configuredDataProtectionPath;
        Directory.CreateDirectory(dataProtectionPath);
        services.AddDataProtection()
            .SetApplicationName("AnnasArchive")
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));

        // First hosted service on purpose: it rewrites the owner key that every
        // other owner-scoped read depends on, and IHostedService.StartAsync runs
        // in registration order, before the server accepts a request.
        services.AddHostedService<HouseholdIdentityMigration>();

        // Background services
        var watcherEnabled = configuration.GetValue<bool>("LibraryWatcher:Enabled", false);
        if (watcherEnabled)
        {
            services.AddHostedService<LibraryWatcherService>();
        }

        // Audiobook enrichment/rename service — registered as both a singleton
        // (so the admin endpoint can call RunScanAsync directly, bypassing its
        // internal weekly timer) and a hosted service (so the timer loop runs
        // in the background). The service itself checks AudiobookWatcher:Enabled
        // internally and no-ops if disabled — always registered so the admin
        // endpoint can still resolve it for manual dry-run/subset triggering
        // even when the automatic weekly scan is off.
        services.AddSingleton<AudiobookEnrichmentService>();
        services.AddHostedService(sp => sp.GetRequiredService<AudiobookEnrichmentService>());

        // User-state SQLite database (persistent /app/state mount in prod) + the
        // book personalization store — the write target for all user edits, kept
        // structurally separate from the enrichment sidecars. See DOCS/reference/PROJECT_AUDIT.md §8.6.
        services.AddSingleton<Data.AppDatabase>();
        services.AddSingleton<Data.BookPersonalizationStore>();
        services.AddSingleton<Data.AudiobookRequestStore>();
        services.AddSingleton<AudiobookRequestTokenStore>();

        // ─── Ebook Reader II (DOCS/features/EBOOK_READER_II.md) ───
        // Shares nothing with the existing reader: its own tables, its own text
        // root, its own namespace. Singletons because ContentHashCache is only
        // useful if the memoised hashes outlive a request.
        services.AddSingleton<Reader2.Domain.ILibraryBookSource, Reader2.Domain.LibraryBookSource>();
        services.AddSingleton<Reader2.Domain.ContentHashCache>();
        services.AddSingleton<Reader2.Storage.ChapterTextStore>();
        services.AddSingleton<Reader2.Storage.IArtifactStore, Reader2.Storage.SqliteArtifactStore>();
        services.AddSingleton<Reader2.Domain.IBookRegistry, Reader2.Domain.BookRegistry>();
        services.AddSingleton<Reader2.Epub.BookIngestor>();
        services.AddSingleton<Reader2.Domain.IReaderContextResolver, Reader2.Domain.ReaderContextResolver>();
        services.AddSingleton<Reader2.Story.CastOverrideStore>();
        services.AddSingleton<Reader2.Story.StoryModelService>();

        // Book types. Adding one is this line and its class — nothing else in the
        // application changes, which is what the extensibility contract test in
        // AnnasArchive.Tests/Reader2 exists to keep true.
        services.AddSingleton<Reader2.Lenses.IReaderLens, Reader2.Lenses.LiteraryLens>();
        services.AddSingleton<Reader2.Lenses.IReaderLens, Reader2.Lenses.MilitaryLens>();
        services.AddSingleton<Reader2.Lenses.IReaderLens, Reader2.Lenses.FictionLens>();
        services.AddSingleton<Reader2.Lenses.ILensRegistry, Reader2.Lenses.LensRegistry>();

        // Read once. Every budget, model, and threshold in one object, so nothing
        // downstream reads IConfiguration and no key can be misspelled into a
        // silent default — the Reader I defect this whole type exists to prevent.
        services.AddSingleton(sp =>
            Reader2.Ai.Reader2Options.Load(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<Reader2.Ai.ModelCalls>();

        // Injected rather than a static field, unlike the other users of KeyedLocks
        // in this codebase: the gateway is what stops two tabs billing one summary
        // twice, so a test has to be able to see the lock it is using.
        services.AddSingleton<KeyedLocks>();
        services.AddSingleton<Reader2.Ai.ArtifactGateway>();
        services.AddSingleton<Reader2.Ai.IReaderAiPipeline, Reader2.Ai.ReaderAiPipeline>();
        services.AddSingleton<Reader2.Ai.ChapterLabeller>();
        services.AddSingleton<Reader2.Storage.ReaderStateStore>();
        services.AddSingleton<Reader2.Storage.BookmarkStore>();
        services.AddSingleton<Reader2.Vocabulary.VocabularyStore>();
        services.AddSingleton<Reader2.Vocabulary.VocabularyPipeline>();
        services.AddSingleton<Reader2.Vocabulary.FlashcardStore>();

        // Library services - LibraryIndexCache warms on startup via IHostedService
        services.AddSingleton<LibraryIndexCache>();
        services.AddHostedService(sp => sp.GetRequiredService<LibraryIndexCache>());

        // Video library services - VideoIndexCache warms on startup via IHostedService
        services.AddSingleton<VideoIndexCache>();
        services.AddHostedService(sp => sp.GetRequiredService<VideoIndexCache>());

        // Tracks progress of "send to library" downloads that run detached from
        // their originating HTTP request (see HandleSendToLibrary / HandleLibGenSendToLibrary)
        services.AddSingleton<Services.IBookDownloadJobService, Services.BookDownloadJobService>();
    }

    /// <summary>
    /// Date Night. See DOCS/features/DATE_NIGHT.md.
    /// </summary>
    private static void AddDateNightServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Date Night pool availability (see DOCS/features/DATE_NIGHT.md). Singleton so
        // its "only one scan at a time" lock is actually shared across requests —
        // a per-request instance would let every caller start a competing scan.
        // Resolves IRadarrService per-scope internally, since that's a typed HttpClient.
        services.AddSingleton<DateNightAvailabilityService>();

        // Weekly cycle, votes, and the four permanent lists (phase 3). Singleton for the
        // same reason as the availability service above — its lock guards against two
        // concurrent requests both deciding a new cycle is due.
        services.AddSingleton<DateNightCycleService>();
        services.AddHostedService<DateNightLifecycleService>();

        // Adopts library items nobody owns and prunes records whose item is gone.
        // Off by setting Ownership:BackfillEnabled=false; the member it adopts to is
        // Ownership:DefaultMember.
        services.AddHostedService<OwnershipBackfillService>();

        // Flyer AI pitch lines (phase 4) — singleton so its cache reads/writes go
        // through one instance, same reasoning as the two services above. Depends on
        // IAiResponseParser/ITokenUsageService/IModelSelectionService, registered
        // further down — registration order doesn't matter to the DI container, which
        // resolves the whole graph lazily on first use.
        services.AddSingleton<DateNightSummaryService>();
    }

    /// <summary>
    /// The ebook library: classification, deduplication, audiobook orchestration
    ///     /// and the external metadata lookups behind cover and description fallback.
    /// </summary>
    private static void AddLibraryServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IGenreClassificationService, GenreClassificationService>();
        services.AddSingleton<IDuplicateDetectionService, DuplicateDetectionService>();
        services.AddSingleton<IMetadataExtractionService, MetadataExtractionService>();
        services.AddSingleton<IEnrichmentStatsService, EnrichmentStatsService>();

        // Per-request orchestration over the typed Listenarr and Audiobookshelf
        // clients. It holds no state; scoped lifetime keeps those HTTP client
        // dependencies aligned with the request that initiated the search.
        services.AddScoped<AudiobookAvailabilityService>();
        services.AddScoped<AudiobookDiscoveryService>();
        services.AddScoped<AudiobookRequestReconciler>();
        services.AddScoped<AudiobookRequestService>();
        services.AddScoped<AudiobookSeriesService>();

        // External API services
        services.AddSingleton<IOpenLibraryService, OpenLibraryService>();
        services.AddSingleton<IGoogleBooksService, GoogleBooksService>();
        services.AddSingleton<Services.IDescriptionFetcherService, Services.DescriptionFetcherService>();
        // Scoped, not Singleton — CoverLookupService now depends on the
        // Scoped AnnasArchiveService (for the Anna's-Archive-thumbnail cover
        // path), and a Singleton can't safely consume a Scoped dependency
        // without capturing it forever across requests.
        services.AddScoped<Services.ICoverLookupService, Services.CoverLookupService>();
    }

    /// <summary>
    /// Cross-cutting application services — email, token accounting, download
    ///     /// tracking, media ownership and the daily library-review queue.
    /// </summary>
    private static void AddPlatformServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Email service
        services.AddSingleton<IEmailService, EmailService>();

        // Token usage tracking — storage path must be configurable: the default
        // (~/.annas-archive/ai-usage) lives on the container's ephemeral filesystem,
        // so every deploy silently reset everyone's monthly AI cost allowance.
        // docker-compose points this at the persistent /app/state mount.
        services.AddSingleton<ITokenUsageService>(provider =>
        {
            var cfg = provider.GetRequiredService<IConfiguration>();
            var configuredPath = cfg.GetValue<string>("TokenUsage:StoragePath");
            return new TokenUsageService(string.IsNullOrWhiteSpace(configuredPath) ? null : configuredPath);
        });

        // Download tracking service
        services.AddSingleton<IDownloadTrackingService>(provider =>
        {
            var cfg = provider.GetRequiredService<IConfiguration>();
            var downloadLimit = cfg.GetValue<int>("DownloadTracking:DownloadLimit", Limits.DefaultDownloadLimit);
            var rollingHours = cfg.GetValue<double>("DownloadTracking:RollingWindowHours", Limits.DefaultDownloadWindowHours);
            var configuredPath = cfg.GetValue<string>("DownloadTracking:StoragePath");
            var storagePath = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(Directory.GetCurrentDirectory(), "download-tracking.json")
                : (Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.Combine(Directory.GetCurrentDirectory(), configuredPath));
            return new DownloadTrackingService(downloadLimit, rollingHours, storagePath);
        });

        // Media (Sonarr/Radarr) owner(s) + custom genre tags — who requested
        // what, and user-created genre tags independent of Sonarr/Radarr's own.
        // Persisted in the app SQLite database; the old MediaMetadata:StoragePath
        // JSON file (if it exists) is imported once and then left alone.
        services.AddSingleton<Services.IMediaMetadataService>(provider =>
        {
            var cfg = provider.GetRequiredService<IConfiguration>();
            var configuredPath = cfg.GetValue<string>("MediaMetadata:StoragePath");
            var legacyPath = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(Directory.GetCurrentDirectory(), "media-metadata.json")
                : (Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.Combine(Directory.GetCurrentDirectory(), configuredPath));
            return new Services.MediaMetadataService(provider.GetRequiredService<Data.AppDatabase>(), legacyPath);
        });

        // Daily library-review modal — forced cull-then-genre triage of Paul's untagged books.
        // Progress lives in the app SQLite database; LibraryReview:StoragePath is only the
        // legacy JSON file imported once if present.
        services.AddSingleton<Services.ILibraryReviewService>(provider =>
        {
            var cfg = provider.GetRequiredService<IConfiguration>();
            var configuredPath = cfg.GetValue<string>("LibraryReview:StoragePath");
            var legacyPath = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(Directory.GetCurrentDirectory(), "library-review-progress.json")
                : (Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.Combine(Directory.GetCurrentDirectory(), configuredPath));
            return new Services.LibraryReviewService(
                provider.GetRequiredService<LibraryIndexCache>(),
                provider.GetRequiredService<Data.AppDatabase>(),
                provider.GetRequiredService<Data.BookPersonalizationStore>(),
                legacyPath);
        });
    }

    /// <summary>
    /// The AI stack: the two completion clients every AI endpoint shares, model
    ///     /// selection, response parsing, and the validation and quiz services on top.
    /// </summary>
    private static void AddAiServices(this IServiceCollection services, IConfiguration configuration)
    {
        // AI-related services
        services.AddSingleton<IOpenAiModelHelper, OpenAiModelHelper>();
        services.AddSingleton<IAiResponseParser, AiResponseParser>();
        services.AddSingleton<IModelSelectionService, ModelSelectionService>();

        // The chat-completion round trip every AI endpoint shares. Singleton
        // because all four of its dependencies are.
        services.AddSingleton<Services.Ai.IAiChatCompletion, Services.Ai.AiChatCompletion>();

        // Its Responses-API counterpart. Two clients rather than one because the
        // two OpenAI APIs disagree about payload shape, token-budget field and
        // usage field names — a single client would be a switch statement.
        services.AddSingleton<Services.Ai.IAiResponsesCompletion, Services.Ai.AiResponsesCompletion>();

        // Scoped, not singleton: it holds the scoped AnnasArchiveService, which
        // owns a per-request Playwright transport.
        services.AddScoped<Services.BookDiscovery.IRelatedBooksEnricher, Services.BookDiscovery.RelatedBooksEnricher>();

        // Validation services
        services.AddSingleton<IValidationService, ValidationService>();

        // Quiz services
        services.AddSingleton<IQuizValidationService, QuizValidationService>();
        services.AddSingleton<IQuizStorageService, QuizStorageService>();
    }

    /// <summary>
    /// Photo prints (Immich → CVS pickup). See
    ///     /// DOCS/features/google-photos-cvs-print-automation-spec.md.
    /// </summary>
    private static void AddPhotoPrintServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Photo print pipeline (Immich → CVS pickup prints). See
        // DOCS/features/google-photos-cvs-print-automation-spec.md. Registered
        // unconditionally — the endpoints are what PhotoPrint:Enabled gates, and
        // the store/preparation services are harmless and testable without it.
        services.Configure<PhotoPrintConfiguration>(configuration.GetSection(PhotoPrintConfiguration.SectionName));
        services.AddSingleton<Data.IPhotoPrintOrderStore, Data.PhotoPrintOrderStore>();
        services.AddSingleton<IPrintImagePreparationService, PrintImagePreparationService>();
        services.AddSingleton<IImmichService, ImmichService>();
        services.AddSingleton<IPhotoPrintRunService, PhotoPrintRunService>();
    }

    /// <summary>
    /// Spotify: connection state, the read-only inspector, and the reviewed
    ///     /// change-plan pipeline that is the only thing allowed to write.
    /// </summary>
    private static void AddSpotifyServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Spotify configuration
        services.Configure<SpotifyConfiguration>(configuration.GetSection(SpotifyConfiguration.SectionName));
        services.AddSingleton<ISpotifyConnectionStore, SpotifyConnectionStore>();
        services.AddSingleton<ISpotifyOAuthStateStore, SpotifyOAuthStateStore>();
        services.AddSingleton<ISpotifyCurrentUser, SpotifyCurrentUser>();
        services.AddSingleton<SpotifyAuthorizationService>();
        services.AddSingleton<ISpotifyAuthorizationService>(provider =>
            provider.GetRequiredService<SpotifyAuthorizationService>());
        services.AddSingleton<ISpotifyAccessTokenProvider>(provider =>
            provider.GetRequiredService<SpotifyAuthorizationService>());

        // Read-only conversational inspector. The parser only classifies intent;
        // the conversation service owns every fact and every sentence about them.
        services.AddSingleton<ISpotifyCommandParser, SpotifyCommandParser>();
        services.AddScoped<ISpotifyConversationService, SpotifyConversationService>();

        // Spotify inventory is persisted in the shared SQLite database but every row
        // is keyed by a one-way hash of the application owner. The scoped reader can
        // use the current request identity; the singleton job runner creates scopes
        // and supplies an explicit owner key after the browser disconnects.
        services.AddSingleton<ISpotifyInventoryStore, SpotifyInventoryStore>();
        services.AddScoped<ISpotifyInventoryService, SpotifyInventoryService>();

        // Phase 6/7 — reviewed change plans. Nothing writes to Spotify except by
        // executing a confirmed plan through SpotifyPlanExecutor.
        services.AddSingleton<ISpotifyPlanStore, SpotifyPlanStore>();
        services.AddSingleton<ISpotifyAuditService, SpotifyAuditService>();
        services.AddScoped<ISpotifyPlanService, SpotifyPlanService>();
        services.AddScoped<ISpotifyPlanExecutor, SpotifyPlanExecutor>();
        services.AddSingleton<ISpotifyInventoryJobService, SpotifyInventoryJobService>();
        services.AddScoped<ISpotifyKnownMusicService, SpotifyKnownMusicService>();
        services.AddSingleton<ISpotifyDiscoveryStore, SpotifyDiscoveryStore>();
        services.AddScoped<ISpotifyDiscoveryService, SpotifyDiscoveryService>();
    }

    /// <summary>
    /// The book reader: text processing, the EPUB chapter cache, flashcards and
    ///     /// the activity feed.
    /// </summary>
    private static void AddReaderServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Text processing
        services.AddSingleton<ITextProcessingService, TextProcessingService>();

        // EPUB cache path provider

        // Flashcard service

        // User activity tracking
        services.AddSingleton<IUserActivityService, UserActivityService>();
    }
}
