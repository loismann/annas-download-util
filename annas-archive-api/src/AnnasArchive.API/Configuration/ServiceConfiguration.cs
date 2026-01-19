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
        // Anna's Archive HTTP client (scraping with domain fallback)
        services.AddHttpClient<AnnaArchiveService>(c =>
        {
            c.BaseAddress = new Uri("https://annas-archive.org");
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            c.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            c.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        })
        .AddScrapingResilience("AnnasArchive");

        // LibGen HTTP client (scraping with domain fallback)
        services.AddHttpClient<LibGenService>(c =>
        {
            c.BaseAddress = new Uri("https://libgen.rs");
            c.Timeout = HttpTimeouts.ScrapingTimeout;
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

        // Ebook Cover Service (with HTTP client and standard resilience)
        services.AddHttpClient<IEbookCoverService, EbookCoverService>()
            .AddStandardResilience("EbookCover");

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

        // Library services
        services.AddSingleton<IGenreClassificationService, GenreClassificationService>();
        services.AddSingleton<IDuplicateDetectionService, DuplicateDetectionService>();
        services.AddSingleton<IMetadataExtractionService, MetadataExtractionService>();
        services.AddSingleton<IEnrichmentStatsService, EnrichmentStatsService>();

        // External API services
        services.AddSingleton<IOpenLibraryService, OpenLibraryService>();
        services.AddSingleton<IGoogleBooksService, GoogleBooksService>();
        services.AddSingleton<Services.IDescriptionFetcherService, Services.DescriptionFetcherService>();
        services.AddSingleton<Services.ICoverLookupService, Services.CoverLookupService>();

        // Email service
        services.AddSingleton<IEmailService, EmailService>();

        // Token usage tracking
        services.AddSingleton<ITokenUsageService, TokenUsageService>();

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

        // AI-related services
        services.AddSingleton<IOpenAiModelHelper, OpenAiModelHelper>();
        services.AddSingleton<IAiResponseParser, AiResponseParser>();
        services.AddSingleton<IModelSelectionService, ModelSelectionService>();

        // Validation services
        services.AddSingleton<IValidationService, ValidationService>();

        // Quiz services
        services.AddSingleton<IQuizValidationService, QuizValidationService>();
        services.AddSingleton<IQuizStorageService, QuizStorageService>();

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
