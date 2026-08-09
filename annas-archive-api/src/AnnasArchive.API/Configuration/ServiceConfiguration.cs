using System.Net;
using AnnasArchive.API.Services.Spotify;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using AnnasArchive.API.Constants;
using AnnasArchive.API.Endpoints;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Infrastructure;
using AnnasArchive.API.Services;
using AnnasArchive.API.Services.Library;
using AnnasArchive.API.Services.PhotoPrint;
using AnnasArchive.Core.Services;
using Dropbox.Api;
using Microsoft.AspNetCore.DataProtection;
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
    /// Cookie carrying the bearer token for ebook cover requests, and the path the
    /// browser scopes it to. Both halves are duplicated in the frontend's
    /// <c>AuthService</c> — they are a wire contract, so a change here needs the
    /// matching change there or covers silently fall back to the placeholder.
    /// </summary>
    public const string LibraryCoverCookieName = "annas_cover_token";

    /// <summary>Path prefix the cover cookie is scoped to. Nothing else receives it.</summary>
    public const string LibraryCoverCookiePath = "/api/library/cover";


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

        // AuthorSuggestionCacheSize was configured and documented but never
        // actually read, leaving that cache unbounded. Now wired up.
        AiBookSearchEndpoints.ConfigureCache(cacheConfig.AuthorSuggestionCacheSize);

        Log.Information("[Caching] Caches configured - ChapterContent: {ChapterSize} items, AuthorSuggestions: {AuthorSize} items",
            cacheConfig.ChapterContentCacheSize,
            cacheConfig.AuthorSuggestionCacheSize);

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
                // Tokens live 30 days, so tokens issued while NameIdentifier still
                // carried the access code are in circulation after this deploy.
                // Their claim is rewritten to the current owner id here — one place,
                // before any handler reads it — so a session neither breaks nor
                // points at data the startup migration has already moved.
                OnTokenValidated = context =>
                {
                    HouseholdIdentity.NormalizeIdentity(context.Principal, configuration);
                    return Task.CompletedTask;
                },

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

                    // Photo print picker thumbnails — hundreds of <img> tags per
                    // page, same header limitation as audiobook covers above.
                    // Scoped to the thumbnail route only: the full-resolution
                    // original is never fetched by the browser.
                    var isPhotoPrintThumbnail =
                        path.StartsWith("/api/photo-print/photos/", StringComparison.OrdinalIgnoreCase) &&
                        path.EndsWith("/thumbnail", StringComparison.OrdinalIgnoreCase);

                    if (isAudiobookStreamOrCover || isMediaDownload || isMediaStream || isPhotoPrintThumbnail)
                    {
                        var token = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token))
                            context.Token = token;
                    }

                    // Ebook covers take the token from a cookie rather than the
                    // query string, unlike every route above. The difference is
                    // where the URL is built: those are assembled in TypeScript,
                    // so a token can be appended at the call site, while a library
                    // cover URL is minted server-side by LibraryHelpers and handed
                    // out inside a *cached* DTO (LibraryIndexCache). Baking a
                    // per-user token into a shared cache is not an option, and
                    // rewriting it in eight places on the way out is one missed
                    // method away from a screen of broken covers.
                    //
                    // The cookie is scoped to this path by the browser (see
                    // AuthService.setToken), so it is never sent anywhere else and
                    // widens nothing. It carries the same JWT the Authorization
                    // header would; it is not a second credential.
                    if (path.StartsWith(LibraryCoverCookiePath, StringComparison.OrdinalIgnoreCase))
                    {
                        var cookieToken = context.Request.Cookies[LibraryCoverCookieName];
                        if (!string.IsNullOrEmpty(cookieToken))
                            context.Token = cookieToken;
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
                out var apiLimit) ? apiLimit : Limits.DefaultApiRateLimit;
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
                opt.PermitLimit = Limits.MediaRateLimit;
                opt.Window = TimeSpan.FromMinutes(1);
                opt.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
                opt.QueueLimit = 0;
            });

            // Stricter rate limit for login: 5 attempts per minute per IP (configurable)
            var loginRateLimit = int.TryParse(
                configuration["LOGIN_RATE_LIMIT"] ?? configuration["E2E_LOGIN_RATE_LIMIT"],
                out var loginLimit) ? loginLimit : Limits.LoginRateLimit;
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
    /// Configures the Dropbox client with refresh token authentication.
    /// Skips creation in test environment to avoid HTTP calls.
    /// </summary>
    public static IServiceCollection AddDropboxClient(this IServiceCollection services, IConfiguration configuration)
    {
        // Skip Dropbox client in test environment to avoid HTTP calls
        if (TestEnvironment.IsTest(configuration))
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
