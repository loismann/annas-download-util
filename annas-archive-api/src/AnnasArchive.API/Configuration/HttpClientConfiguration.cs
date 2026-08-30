using System.Net;
using System.Net.Http.Headers;
using AnnasArchive.API.Constants;
using AnnasArchive.API.Services;
using AnnasArchive.API.Services.PhotoPrint;
using AnnasArchive.API.Services.Spotify;
using AnnasArchive.Core.Services;
using Microsoft.Extensions.Caching.Memory;

namespace AnnasArchive.API.Configuration;

/// <summary>
/// HTTP clients for every external service, with their resilience policies.
/// Split out of ServiceConfiguration, where this was a single 256-line method.
/// </summary>
public static class HttpClientConfiguration
{
    /// <summary>
    /// Configures all HTTP clients for external services with resilience policies.
    /// </summary>
    public static IServiceCollection AddHttpClients(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddBookSourceClients(configuration);
        services.AddAiClients(configuration);
        services.AddBookMetadataClients(configuration);
        services.AddPhotoLibraryClient(configuration);
        services.AddMusicClients(configuration);
        services.AddMediaServerClients(configuration);

        return services;
    }

    /// <summary>
    /// Anna's Archive and LibGen, plus the Cloudflare bypass and the VPN proxy
    ///     /// toggle that only Anna's Archive traffic goes through.
    /// </summary>
    private static void AddBookSourceClients(this IServiceCollection services, IConfiguration configuration)
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
        // (AnnasArchiveProxy:Url, e.g. http://gluetun:8888) when configured
        // AND the live VPN toggle (IVpnSettingsService) is enabled —
        // everything else the app calls (OpenAI, Wikipedia, LibGen, Seq)
        // stays on a normal direct connection. DynamicVpnProxy checks the
        // toggle on every request, not just once at startup, so flipping
        // it in the UI takes effect on the very next request — no restart.
        .ConfigurePrimaryHttpMessageHandler(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var proxyUrl = configuration["AnnasArchiveProxy:Url"];
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

        // Anna's Archive transport with Playwright integration — the mirror
        // fallback everything else on Anna's Archive is built on.
        services.AddScoped<AnnasArchiveTransport>(provider =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("AnnasArchive");
            var bypassService = provider.GetRequiredService<ICloudflareBypassService>();

            // Create delegate that uses Playwright for HTML fetching
            Func<string, Task<string>> playwrightFetcher = url => bypassService.FetchHtmlAsync(url);

            return new AnnasArchiveTransport(httpClient, playwrightFetcher);
        });

        // Search + covers, and fast downloads. Separate registrations so a
        // caller that only downloads never pulls in the search service.
        // Constructed by hand rather than by convention: AnnasArchiveService
        // keeps a second (HttpClient, IMemoryCache, ...) constructor for tests,
        // and letting the container choose between the two is exactly the kind
        // of ambiguity that only shows up at runtime.
        services.AddScoped<AnnasArchiveService>(provider => new AnnasArchiveService(
            provider.GetRequiredService<AnnasArchiveTransport>(),
            provider.GetRequiredService<IMemoryCache>()));
        services.AddScoped<AnnasArchiveDownloads>(provider => new AnnasArchiveDownloads(
            provider.GetRequiredService<AnnasArchiveTransport>()));

        // What /api/anna/book actually searches: LibGen for the md5, Anna's only
        // as a fallback. Registered after both because it holds them; scoped
        // because they are. See BookSearch for why the md5s are interchangeable.
        services.AddScoped<BookSearch>();

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
    }

    /// <summary>
    /// OpenAI. Longer timeouts than anything else here.
    /// </summary>
    private static void AddAiClients(this IServiceCollection services, IConfiguration configuration)
    {
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
    }

    /// <summary>
    /// OpenLibrary, Google Books, Wikipedia and covers — the description and
    ///     /// cover fallbacks, in the order they are tried.
    /// </summary>
    private static void AddBookMetadataClients(this IServiceCollection services, IConfiguration configuration)
    {
        // OpenLibrary HTTP Client (external API)
        services.AddHttpClient("OpenLibrary", client =>
        {
            client.BaseAddress = new Uri("https://openlibrary.org/");
            client.Timeout = HttpTimeouts.StandardApiTimeout;
            client.DefaultRequestHeaders.Add("User-Agent", "AnnasArchive/1.0");
        })
        .AddStandardResilience("OpenLibrary");

        // Google Books HTTP Client (external API)
        services.AddTransient<GoogleBooksApiKeyHandler>();
        services.AddHttpClient("GoogleBooks", client =>
        {
            client.BaseAddress = new Uri("https://www.googleapis.com/");
            client.Timeout = HttpTimeouts.StandardApiTimeout;
            client.DefaultRequestHeaders.Add("User-Agent", "AnnasArchive/1.0");
        })
        // Before the resilience handler, so a retried request still carries the key.
        .AddHttpMessageHandler<GoogleBooksApiKeyHandler>()
        .AddStandardResilience("GoogleBooks");

        // Wikipedia HTTP Client (external API) — real-data fallback for
        // descriptions, free and not subject to the rate limits that made
        // OpenLibrary/Google Books unreliable. Wikipedia's API etiquette
        // requires a descriptive User-Agent or it may reject requests.
        services.AddHttpClient("Wikipedia", client =>
        {
            client.Timeout = HttpTimeouts.StandardApiTimeout;
            client.DefaultRequestHeaders.Add("User-Agent", "AnnasArchiveApp/1.0 (personal self-hosted library tool)");
        })
        .AddStandardResilience("Wikipedia");
        services.AddSingleton<IWikipediaService, WikipediaService>();

        // Ebook Cover Service (with HTTP client and standard resilience)
        services.AddHttpClient<IEbookCoverService, EbookCoverService>()
            .AddStandardResilience("EbookCover");
    }

    /// <summary>
    /// Immich, for the photo-print pipeline. Registered only when configured.
    /// </summary>
    private static void AddPhotoLibraryClient(this IServiceCollection services, IConfiguration configuration)
    {
        // Immich — the household photo library, on the internal compose network.
        // No resilience pipeline: it is a LAN-local service, and a retry storm
        // against a multi-megabyte original download costs more than it saves.
        var immichBaseUrl = configuration["PhotoPrint:Immich:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(immichBaseUrl))
        {
            services.AddHttpClient("Immich", client =>
            {
                client.BaseAddress = new Uri(immichBaseUrl.TrimEnd('/') + "/");
                // Originals run to tens of megabytes; the standard API timeout
                // would abort a large print-resolution download mid-stream.
                client.Timeout = TimeSpan.FromMinutes(5);
                client.DefaultRequestHeaders.Add("User-Agent", "AnnasArchive/1.0");
                var apiKey = configuration["PhotoPrint:Immich:ApiKey"];
                if (!string.IsNullOrWhiteSpace(apiKey))
                    client.DefaultRequestHeaders.Add("x-api-key", apiKey);
            });
        }
    }

    /// <summary>
    /// Spotify.
    /// </summary>
    private static void AddMusicClients(this IServiceCollection services, IConfiguration configuration)
    {
        // Spotify's Development Mode QUOTA_EXCEEDED response must not be
        // automatically retried. Phase 9 can add a Spotify-specific policy for
        // selected network/5xx failures; for now all retries remain explicit.
        services.AddHttpClient("SpotifyAccounts");
        services.AddHttpClient<ISpotifyService, SpotifyService>();
    }

    /// <summary>
    /// Sonarr, Radarr, Jellyfin, Audiobookshelf and Listenarr — the self-hosted
    ///     /// media stack, each with its own resilience story.
    /// </summary>
    private static void AddMediaServerClients(this IServiceCollection services, IConfiguration configuration)
    {
        // Sonarr/Radarr — the API is local, but interactive release searches fan
        // out to external indexers and can legitimately take longer than quick
        // metadata calls. Give both HttpClient and Polly the same *arr-specific
        // budget so neither layer cuts a successful search off early.
        services.AddHttpClient<ISonarrService, SonarrService>(c =>
        {
            c.Timeout = HttpTimeouts.ArrOperationTimeout;
        }).AddStandardResilience("Sonarr", HttpTimeouts.ArrOperationTimeout);

        services.AddHttpClient<IRadarrService, RadarrService>(c =>
        {
            c.Timeout = HttpTimeouts.ArrOperationTimeout;
        }).AddStandardResilience("Radarr", HttpTimeouts.ArrOperationTimeout);

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

        // Listenarr has a versioned v1 contract that is deliberately kept
        // separate from ArrServiceBase. Do not attach the generic retry policy:
        // this same client performs non-idempotent add/grab mutations, and
        // Listenarr does not publish an idempotency-key contract. The request
        // service preflights and reconciles mutations by ASIN instead.
        services.AddHttpClient<IListenarrService, ListenarrService>(c =>
        {
            // Interactive indexer fan-out is the longest operation on this
            // client. Ordinary metadata/health calls return much sooner.
            c.Timeout = HttpTimeouts.ArrOperationTimeout;
        });
    }
}
