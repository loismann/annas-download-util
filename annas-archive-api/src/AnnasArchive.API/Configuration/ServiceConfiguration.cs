using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using AnnasArchive.API.Constants;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Infrastructure;
using AnnasArchive.API.Services;
using AnnasArchive.API.Services.Library;
using AnnasArchive.Core.Services;
using Dropbox.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

namespace AnnasArchive.API.Configuration;

/// <summary>
/// Extension methods for configuring dependency injection services.
/// Extracted from Program.cs to improve maintainability.
/// </summary>
public static class ServiceConfiguration
{
    /// <summary>
    /// Registers all application services with the DI container.
    /// </summary>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthenticationServices(configuration);
        services.AddAuthorizationServices();
        services.AddRateLimitingServices(configuration);
        services.AddSwaggerServices();
        services.AddHttpClients(configuration);
        services.AddCoreServices(configuration);
        services.AddDropboxClient(configuration);
        services.AddMiscServices();
        services.ConfigureCaches(configuration);

        return services;
    }

    /// <summary>
    /// Configures application caches with sizes from configuration.
    /// </summary>
    public static IServiceCollection ConfigureCaches(this IServiceCollection services, IConfiguration configuration)
    {
        var cacheConfig = configuration.GetSection(CacheConfiguration.SectionName).Get<CacheConfiguration>()
            ?? new CacheConfiguration();

        // Configure LibraryEpubCache chapter content cache
        LibraryEpubCache.ConfigureCache(cacheConfig.ChapterContentCacheSize);

        Log.Information("[Caching] Caches configured - ChapterContent: {ChapterSize} items",
            cacheConfig.ChapterContentCacheSize);

        return services;
    }

    /// <summary>
    /// Configures JWT Bearer authentication.
    /// </summary>
    public static IServiceCollection AddAuthenticationServices(this IServiceCollection services, IConfiguration configuration)
    {
        var jwtSecret = configuration["Auth:JwtSecret"]
            ?? throw new InvalidOperationException("Missing Auth:JwtSecret.");
        var jwtKey = Encoding.UTF8.GetBytes(jwtSecret);

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(jwtKey),
                ValidateIssuer = true,
                ValidIssuer = "AnnasArchiveAPI",
                ValidateAudience = true,
                ValidAudience = "AnnasArchiveApp",
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };

            // Native <audio>/<img> elements can't attach an Authorization header
            // (unlike HttpClient requests, which get it from auth.interceptor.ts
            // on the frontend), and a movie/episode download is a plain browser
            // navigation (window.location), same limitation — so these route
            // shapes accept the token via ?access_token= as a scoped fallback.
            // Everything else still requires the header.
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var path = context.HttpContext.Request.Path.Value ?? "";
                    var isAudiobookStreamOrCover =
                        path.StartsWith("/api/audiobooks/", StringComparison.OrdinalIgnoreCase) &&
                        (path.Contains("/stream/", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith("/cover", StringComparison.OrdinalIgnoreCase));
                    var isMediaDownload =
                        path.Equals("/api/media/movies/download", StringComparison.OrdinalIgnoreCase) ||
                        path.Equals("/api/media/tv/download", StringComparison.OrdinalIgnoreCase);
                    var isMediaStream =
                        path.Equals("/api/media/movies/stream", StringComparison.OrdinalIgnoreCase) ||
                        path.Equals("/api/media/tv/stream", StringComparison.OrdinalIgnoreCase) ||
                        path.Equals("/api/media/movies/subtitles", StringComparison.OrdinalIgnoreCase) ||
                        path.Equals("/api/media/tv/subtitles", StringComparison.OrdinalIgnoreCase) ||
                        path.Equals("/api/media/movies/hls/master.m3u8", StringComparison.OrdinalIgnoreCase) ||
                        path.Equals("/api/media/tv/hls/master.m3u8", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("/api/media/hls/", StringComparison.OrdinalIgnoreCase);

                    if (isAudiobookStreamOrCover || isMediaDownload || isMediaStream)
                    {
                        var token = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token))
                            context.Token = token;
                    }

                    return Task.CompletedTask;
                }
            };
        });

        return services;
    }

    /// <summary>
    /// Configures authorization policies.
    /// </summary>
    public static IServiceCollection AddAuthorizationServices(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
        });

        return services;
    }

    /// <summary>
    /// Configures rate limiting policies.
    /// </summary>
    public static IServiceCollection AddRateLimitingServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddRateLimiter(options =>
        {
            // Global API rate limit: 60 requests per minute per IP (configurable)
            var apiRateLimit = int.TryParse(
                configuration["API_RATE_LIMIT"] ?? configuration["E2E_API_RATE_LIMIT"],
                out var apiLimit) ? apiLimit : 60;
            options.AddFixedWindowLimiter("api", opt =>
            {
                opt.PermitLimit = apiRateLimit;
                opt.Window = TimeSpan.FromMinutes(1);
                opt.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
                opt.QueueLimit = 0;
            });

            // Media proxy rate limit (cover art / audio streaming): per IP. Kept separate
            // from "api" because the audiobook catalog fires a cover request per tile —
            // sharing the 60/min "api" budget meant covers alone exhausted it. Covers are
            // lazy-loaded and browser-cached now, but a fast full scroll through a large
            // library (~1000 items) must still fit inside one window, so the ceiling is
            // sized to the library, not to typical traffic — this is an anti-runaway
            // guard on a Tailscale-only app, not an abuse defense.
            options.AddFixedWindowLimiter("media", opt =>
            {
                opt.PermitLimit = 2000;
                opt.Window = TimeSpan.FromMinutes(1);
                opt.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
                opt.QueueLimit = 0;
            });

            // Stricter rate limit for login: 5 attempts per minute per IP (configurable)
            var loginRateLimit = int.TryParse(
                configuration["LOGIN_RATE_LIMIT"] ?? configuration["E2E_LOGIN_RATE_LIMIT"],
                out var loginLimit) ? loginLimit : 5;
            options.AddFixedWindowLimiter("login", opt =>
            {
                opt.PermitLimit = loginRateLimit;
                opt.Window = TimeSpan.FromMinutes(1);
                opt.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
                opt.QueueLimit = 0;
            });
        });

        return services;
    }

    /// <summary>
    /// Configures Swagger/OpenAPI documentation.
    /// </summary>
    public static IServiceCollection AddSwaggerServices(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = "Anna's Archive Proxy API", Version = "v1" });
        });

        return services;
    }

    /// <summary>
    /// Configures all HTTP clients for external services with resilience policies.
    /// </summary>
    public static IServiceCollection AddHttpClients(this IServiceCollection services, IConfiguration configuration)
    {
        // Cloudflare bypass service using Playwright (singleton to manage browser lifecycle)
        services.AddSingleton<ICloudflareBypassService, CloudflareBypassService>();

        // Anna's Archive HTTP client (named client for fallback/downloads)
        services.AddHttpClient("AnnasArchive", c =>
        {
            c.BaseAddress = new Uri("https://annas-archive.org");
            // HttpClient.Timeout covers the whole request including reading the
            // response body — the default 100s was killing "send to library"
            // downloads of large books (100MB+) partway through CopyToAsync.
            // Same reasoning as the Jellyfin/Audiobookshelf clients below.
            c.Timeout = HttpTimeouts.MediaStreamingTimeout;
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            c.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
            c.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            c.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            c.DefaultRequestHeaders.Add("sec-ch-ua", "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
            c.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
            c.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
            c.DefaultRequestHeaders.Add("sec-fetch-dest", "document");
            c.DefaultRequestHeaders.Add("sec-fetch-mode", "navigate");
            c.DefaultRequestHeaders.Add("sec-fetch-site", "none");
            c.DefaultRequestHeaders.Add("sec-fetch-user", "?1");
            c.DefaultRequestHeaders.Add("upgrade-insecure-requests", "1");
            c.DefaultRequestHeaders.Add("Cache-Control", "max-age=0");
        })
        // Routes only Anna's Archive traffic through the Gluetun/PIA proxy
        // (AnnaArchiveProxy:Url, e.g. http://gluetun:8888) when configured
        // AND the live VPN toggle (IVpnSettingsService) is enabled —
        // everything else the app calls (OpenAI, Wikipedia, LibGen, Seq)
        // stays on a normal direct connection. DynamicVpnProxy checks the
        // toggle on every request, not just once at startup, so flipping
        // it in the UI takes effect on the very next request — no restart.
        .ConfigurePrimaryHttpMessageHandler(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var proxyUrl = configuration["AnnaArchiveProxy:Url"];
            var handler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(proxyUrl))
            {
                var vpnSettings = provider.GetRequiredService<Services.IVpnSettingsService>();
                handler.Proxy = new Services.DynamicVpnProxy(vpnSettings, new Uri(proxyUrl));
                handler.UseProxy = true;
            }
            return handler;
        })
        .AddScrapingResilience("AnnasArchive");

        // VPN on/off + region toggle state, and the client used to talk to
        // Gluetun's own control API to actually change region live.
        services.AddSingleton<Services.IVpnSettingsService, Services.VpnSettingsService>();
        services.AddHttpClient("GluetunControl", c =>
        {
            var controlUrl = configuration["Gluetun:ControlUrl"];
            if (!string.IsNullOrWhiteSpace(controlUrl))
            {
                c.BaseAddress = new Uri(controlUrl);
            }
            c.Timeout = TimeSpan.FromSeconds(15);
        });

        // Anna's Archive service with Playwright integration
        services.AddScoped<AnnaArchiveService>(provider =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("AnnasArchive");
            var bypassService = provider.GetRequiredService<ICloudflareBypassService>();
            var cache = provider.GetRequiredService<IMemoryCache>();

            // Create delegate that uses Playwright for HTML fetching
            Func<string, Task<string>> playwrightFetcher = url => bypassService.FetchHtmlAsync(url);

            return new AnnaArchiveService(httpClient, cache, playwrightFetcher);
        });

        // LibGen HTTP client (scraping with domain fallback)
        services.AddHttpClient<LibGenService>(c =>
        {
            c.BaseAddress = new Uri("https://libgen.rs");
            // Same client handles both quick search scraping and full-book
            // downloads via "send to library" — needs to cover the slower case
            // (whole request including response body, per HttpClient.Timeout
            // semantics), a fast search request finishing early doesn't care
            // that the ceiling is generous.
            c.Timeout = HttpTimeouts.MediaStreamingTimeout;
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            c.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            c.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        })
        .AddScrapingResilience("LibGen");

        // OpenAI HTTP client (AI service with longer timeouts)
        services.AddHttpClient("OpenAI", (serviceProvider, client) =>
        {
            var cfg = serviceProvider.GetRequiredService<IConfiguration>();
            var apiKey = cfg["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey is not configured");

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            client.DefaultRequestHeaders.Add("OpenAI-Beta", "responses=v1");
            client.Timeout = HttpTimeouts.AiOperationTimeout;
        })
        .AddAiResilience("OpenAI");

        // OpenLibrary HTTP Client (external API)
        services.AddHttpClient("OpenLibrary", client =>
        {
            client.BaseAddress = new Uri("https://openlibrary.org/");
            client.Timeout = HttpTimeouts.StandardApiTimeout;
            client.DefaultRequestHeaders.Add("User-Agent", "AnnaArchive/1.0");
        })
        .AddStandardResilience("OpenLibrary");

        // Google Books HTTP Client (external API)
        services.AddHttpClient("GoogleBooks", client =>
        {
            client.BaseAddress = new Uri("https://www.googleapis.com/");
            client.Timeout = HttpTimeouts.StandardApiTimeout;
            client.DefaultRequestHeaders.Add("User-Agent", "AnnaArchive/1.0");
        })
        .AddStandardResilience("GoogleBooks");

        // Wikipedia HTTP Client (external API) — real-data fallback for
        // descriptions, free and not subject to the rate limits that made
        // OpenLibrary/Google Books unreliable. Wikipedia's API etiquette
        // requires a descriptive User-Agent or it may reject requests.
        services.AddHttpClient("Wikipedia", client =>
        {
            client.Timeout = HttpTimeouts.StandardApiTimeout;
            client.DefaultRequestHeaders.Add("User-Agent", "AnnaArchiveApp/1.0 (personal self-hosted library tool)");
        })
        .AddStandardResilience("Wikipedia");
        services.AddSingleton<IWikipediaService, WikipediaService>();

        // Ebook Cover Service (with HTTP client and standard resilience)
        services.AddHttpClient<IEbookCoverService, EbookCoverService>()
            .AddStandardResilience("EbookCover");

        // Spotify HTTP client (external API)
        services.AddHttpClient<ISpotifyService, SpotifyService>()
            .AddStandardResilience("Spotify");

        // Sonarr/Radarr — internal Docker network calls (see docker-compose.yml),
        // so a shorter, LAN-appropriate timeout rather than StandardApiTimeout's
        // 30s meant for external services.
        services.AddHttpClient<ISonarrService, SonarrService>(c =>
        {
            c.Timeout = HttpTimeouts.MetadataLookupTimeout;
        }).AddStandardResilience("Sonarr");

        services.AddHttpClient<IRadarrService, RadarrService>(c =>
        {
            c.Timeout = HttpTimeouts.MetadataLookupTimeout;
        }).AddStandardResilience("Radarr");

        // Catalog/matching calls are quick, but this same typed client also
        // proxies movie/episode file downloads (MediaLibraryEndpoints' download
        // routes), which needs a much longer timeout since HttpClient.Timeout
        // covers the whole request including reading the response body — same
        // reasoning, and same no-circuit-breaker media-proxy profile, as
        // AudiobookshelfService below.
        services.AddHttpClient<IJellyfinService, JellyfinService>(c =>
        {
            c.Timeout = HttpTimeouts.MediaStreamingTimeout;
        }).AddMediaProxyResilience("Jellyfin");

        // Separate, unauthenticated-by-default named client for per-person Jellyfin
        // calls (AuthenticateByName, then streaming/UserData as that specific user).
        // Kept apart from the typed client above because that one carries the shared
        // admin API key on every request via DefaultRequestHeaders — reusing it here
        // would mean a personal request goes out with two conflicting X-Emby-Token
        // headers. JellyfinService adds the right per-user token per-request instead.
        services.AddHttpClient("JellyfinUser", (provider, c) =>
        {
            var cfg = provider.GetRequiredService<IConfiguration>();
            var baseUrl = cfg["Jellyfin:BaseUrl"];
            if (!string.IsNullOrWhiteSpace(baseUrl))
                c.BaseAddress = new Uri(baseUrl);
            c.Timeout = HttpTimeouts.MediaStreamingTimeout;
        }).AddMediaProxyResilience("JellyfinUser");

        // Audiobookshelf — catalog/metadata calls are quick like the above,
        // but this same typed client also proxies audio file/cover streaming
        // (AudiobookLibraryEndpoints), which needs a much longer timeout
        // since HttpClient.Timeout governs the whole request including
        // reading the response body. Uses the media-proxy resilience profile
        // (no circuit breaker) — the standard profile's breaker was tripped
        // by routine browser-aborted cover/stream requests, intermittently
        // blacking out the whole audiobook section for 30s+ at a time.
        services.AddHttpClient<IAudiobookshelfService, AudiobookshelfService>(c =>
        {
            c.Timeout = HttpTimeouts.MediaStreamingTimeout;
        }).AddMediaProxyResilience("Audiobookshelf");

        return services;
    }

    /// <summary>
    /// Registers core application services.
    /// </summary>
    public static IServiceCollection AddCoreServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Memory cache (required by OpenLibraryService for author suggestions caching)
        services.AddMemoryCache();

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
        // structurally separate from the enrichment sidecars. See DOCS/PROJECT_AUDIT.md §8.6.
        services.AddSingleton<Data.AppDatabase>();
        services.AddSingleton<Data.BookPersonalizationStore>();

        // Library services - LibraryIndexCache warms on startup via IHostedService
        services.AddSingleton<LibraryIndexCache>();
        services.AddHostedService(sp => sp.GetRequiredService<LibraryIndexCache>());

        // Video library services - VideoIndexCache warms on startup via IHostedService
        services.AddSingleton<VideoIndexCache>();
        services.AddHostedService(sp => sp.GetRequiredService<VideoIndexCache>());

        // Tracks progress of "send to library" downloads that run detached from
        // their originating HTTP request (see HandleSendToLibrary / HandleLibGenSendToLibrary)
        services.AddSingleton<Services.IBookDownloadJobService, Services.BookDownloadJobService>();

        // Date Night pool availability (see DOCS/DATE_NIGHT_FEATURE.md). Singleton so
        // its "only one scan at a time" lock is actually shared across requests —
        // a per-request instance would let every caller start a competing scan.
        // Resolves IRadarrService per-scope internally, since that's a typed HttpClient.
        services.AddSingleton<DateNightAvailabilityService>();

        // Weekly cycle, votes, and the four permanent lists (phase 3). Singleton for the
        // same reason as the availability service above — its lock guards against two
        // concurrent requests both deciding a new cycle is due.
        services.AddSingleton<DateNightCycleService>();

        // Flyer AI pitch lines (phase 4) — singleton so its cache reads/writes go
        // through one instance, same reasoning as the two services above. Depends on
        // IAiResponseParser/ITokenUsageService/IModelSelectionService, registered
        // further down — registration order doesn't matter to the DI container, which
        // resolves the whole graph lazily on first use.
        services.AddSingleton<DateNightSummaryService>();

        services.AddSingleton<IGenreClassificationService, GenreClassificationService>();
        services.AddSingleton<IDuplicateDetectionService, DuplicateDetectionService>();
        services.AddSingleton<IMetadataExtractionService, MetadataExtractionService>();
        services.AddSingleton<IEnrichmentStatsService, EnrichmentStatsService>();

        // External API services
        services.AddSingleton<IOpenLibraryService, OpenLibraryService>();
        services.AddSingleton<IGoogleBooksService, GoogleBooksService>();
        services.AddSingleton<Services.IDescriptionFetcherService, Services.DescriptionFetcherService>();
        // Scoped, not Singleton — CoverLookupService now depends on the
        // Scoped AnnaArchiveService (for the Anna's-Archive-thumbnail cover
        // path), and a Singleton can't safely consume a Scoped dependency
        // without capturing it forever across requests.
        services.AddScoped<Services.ICoverLookupService, Services.CoverLookupService>();

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
            var downloadLimit = cfg.GetValue<int>("DownloadTracking:DownloadLimit", 50);
            var rollingHours = cfg.GetValue<double>("DownloadTracking:RollingWindowHours", 18);
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

        // AI-related services
        services.AddSingleton<IOpenAiModelHelper, OpenAiModelHelper>();
        services.AddSingleton<IAiResponseParser, AiResponseParser>();
        services.AddSingleton<IModelSelectionService, ModelSelectionService>();

        // Validation services
        services.AddSingleton<IValidationService, ValidationService>();

        // Quiz services
        services.AddSingleton<IQuizValidationService, QuizValidationService>();
        services.AddSingleton<IQuizStorageService, QuizStorageService>();

        // YouTube download service
        services.AddSingleton<IYouTubeDownloadService, YouTubeDownloadService>();

        // Spotify configuration
        services.Configure<SpotifyConfiguration>(configuration.GetSection(SpotifyConfiguration.SectionName));

        // Text processing
        services.AddSingleton<ITextProcessingService, TextProcessingService>();

        // EPUB cache path provider
        services.AddSingleton<IEpubCachePathProvider>(provider => new EpubCachePathProviderAdapter());

        // Flashcard service
        services.AddSingleton<IFlashcardService, FlashcardService>();

        // User activity tracking
        services.AddSingleton<IUserActivityService, UserActivityService>();

        return services;
    }

    /// <summary>
    /// Configures the Dropbox client with refresh token authentication.
    /// Skips creation in test environment to avoid HTTP calls.
    /// </summary>
    public static IServiceCollection AddDropboxClient(this IServiceCollection services, IConfiguration configuration)
    {
        // Skip Dropbox client in test environment to avoid HTTP calls
        if (IsTestEnvironment(configuration))
        {
            // Register a null factory - services using DropboxClient should be mocked in tests
            services.AddSingleton<DropboxClient>(provider => null!);
            return services;
        }

        services.AddSingleton<DropboxClient>(provider =>
        {
            var cfg = provider.GetRequiredService<IConfiguration>();
            var appKey = cfg["Dropbox:AppKey"];
            var appSecret = cfg["Dropbox:AppSecret"];
            var refreshToken = cfg["Dropbox:RefreshToken"];

            if (string.IsNullOrWhiteSpace(appKey) ||
                string.IsNullOrWhiteSpace(appSecret) ||
                string.IsNullOrWhiteSpace(refreshToken))
                throw new InvalidOperationException("Dropbox is not configured. Please set Dropbox:AppKey, Dropbox:AppSecret, and Dropbox:RefreshToken in appsettings.json");

            Log.Information("Dropbox client initialized with refresh-token auth");
            return new DropboxClient(refreshToken, appKey, appSecret);
        });

        return services;
    }

    /// <summary>
    /// Checks if we're running in a test environment.
    /// </summary>
    private static bool IsTestEnvironment(IConfiguration? configuration)
    {
        // Check configuration flag
        var isTestConfig = configuration?.GetValue<bool>("Testing:DisableHealthChecks") ?? false;

        // Check environment variable
        var isTestEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Test";

        // Check if running under test host
        var isTestHost = AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => a.FullName?.Contains("testhost") == true ||
                      a.FullName?.Contains("xunit") == true);

        return isTestConfig || isTestEnv || isTestHost;
    }

    /// <summary>
    /// Configures miscellaneous services (JSON options, CORS base setup).
    /// </summary>
    public static IServiceCollection AddMiscServices(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        services.AddCors();

        // AI job lock service for preventing duplicate concurrent AI operations
        services.AddSingleton<IAiJobLockService, AiJobLockService>();

        return services;
    }
}
