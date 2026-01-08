using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using AnnasArchive.API.Services;
using AnnasArchive.API.Models;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Services;
using Dropbox.Api;
using Dropbox.Api.Files;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.RateLimiting;
using System.Threading;
using BCrypt.Net;
using System.Diagnostics;
using VersOne.Epub;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Xml.Linq;
using ICSharpCode.SharpZipLib.Zip;
using ICSharpCode.SharpZipLib.Checksum;
using System.Net.Http.Headers;
using HtmlAgilityPack;
using NReadability;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

// ─── configuration ───────────────────────────────────────────────────────
builder.Configuration
       .SetBasePath(Directory.GetCurrentDirectory())
       .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
       .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
       .AddEnvironmentVariables();

var memberKey   = builder.Configuration["Anna:MemberKey"]
               ?? throw new InvalidOperationException("Missing Anna:MemberKey.");
var searchLimit = builder.Configuration.GetValue<int>("Anna:SearchLimit", 25);  // Reduced from 50 to 25 for better performance

// ─── JWT authentication ──────────────────────────────────────────────────
var jwtSecret = builder.Configuration["Auth:JwtSecret"]
             ?? throw new InvalidOperationException("Missing Auth:JwtSecret.");
var jwtKey = Encoding.UTF8.GetBytes(jwtSecret);

builder.Services.AddAuthentication(options =>
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

builder.Services.AddAuthorization(options =>
{
    // Admin policy for future use
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

// ─── rate limiting ───────────────────────────────────────────────────────
builder.Services.AddRateLimiter(options =>
{
    // Global API rate limit: 60 requests per minute per IP
    options.AddFixedWindowLimiter("api", opt =>
    {
        opt.PermitLimit = 60;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 0; // No queueing
    });

    // Stricter rate limit for login: 5 attempts per minute per IP
    options.AddFixedWindowLimiter("login", opt =>
    {
        opt.PermitLimit = 5;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 0;
    });
});

// ─── swagger ─────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Anna's Archive Proxy API", Version = "v1" });
});

builder.Services.AddHostedService<LibraryWatcherService>();

// ─── Anna's Archive HTTP client ──────────────────────────────────────────
builder.Services.AddHttpClient<AnnaArchiveService>(c =>
{
    c.BaseAddress = new Uri("https://annas-archive.org");
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.Add("Accept",
        "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    c.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
});

// ─── LibGen HTTP client ──────────────────────────────────────────────────
builder.Services.AddHttpClient<LibGenService>(c =>
{
    c.BaseAddress = new Uri("https://libgen.rs");
    c.Timeout = TimeSpan.FromSeconds(15); // 15 second timeout per domain attempt
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.Add("Accept",
        "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    c.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
});

// ─── OpenAI HTTP client ──────────────────────────────────────────────────
builder.Services.AddHttpClient("OpenAI", (serviceProvider, client) =>
{
    var cfg = serviceProvider.GetRequiredService<IConfiguration>();
    var apiKey = cfg["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey is not configured");

    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    client.DefaultRequestHeaders.Add("OpenAI-Beta", "responses=v1");
    client.Timeout = TimeSpan.FromMinutes(5);
});

// ─── Email Service ───────────────────────────────────────────────────────
builder.Services.AddSingleton<IEmailService, EmailService>();

// ─── Token Usage Service ─────────────────────────────────────────────────
builder.Services.AddSingleton<ITokenUsageService, TokenUsageService>();

// ─── Download Tracking Service ───────────────────────────────────────────────
builder.Services.AddSingleton<IDownloadTrackingService>(provider =>
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

// ─── OpenAI Model Helper Service ─────────────────────────────────────────────
builder.Services.AddSingleton<IOpenAiModelHelper, AnnasArchive.Core.Services.OpenAiModelHelper>();

// ─── AI Response Parser Service ──────────────────────────────────────────────
builder.Services.AddSingleton<IAiResponseParser, AnnasArchive.Core.Services.AiResponseParser>();

// ─── Model Selection Service ─────────────────────────────────────────────────
builder.Services.AddSingleton<IModelSelectionService, ModelSelectionService>();

// ─── Validation Service ──────────────────────────────────────────────────────
builder.Services.AddSingleton<IValidationService, ValidationService>();

// ─── Quiz Services ───────────────────────────────────────────────────────────
builder.Services.AddSingleton<IQuizValidationService, QuizValidationService>();
builder.Services.AddSingleton<IQuizStorageService, QuizStorageService>();

// ─── Text Processing Service ─────────────────────────────────────────────────
builder.Services.AddSingleton<ITextProcessingService, TextProcessingService>();

// ─── EPUB Cache Path Provider (Adapter for static DropboxEpubCache) ──────────
builder.Services.AddSingleton<IEpubCachePathProvider>(provider =>
    new EpubCachePathProviderAdapter());

// ─── Flashcard Service ───────────────────────────────────────────────────────
builder.Services.AddSingleton<IFlashcardService, FlashcardService>();

// ─── Ebook Cover Service ─────────────────────────────────────────────────────
builder.Services.AddHttpClient<IEbookCoverService, EbookCoverService>();

// ─── dropbox client (refresh token auth) ─────────────────────────────────────
builder.Services.AddSingleton<DropboxClient>(provider =>
{
    var cfg = provider.GetRequiredService<IConfiguration>();
    var appKey       = cfg["Dropbox:AppKey"];
    var appSecret    = cfg["Dropbox:AppSecret"];
    var refreshToken = cfg["Dropbox:RefreshToken"];

    if (string.IsNullOrWhiteSpace(appKey) ||
        string.IsNullOrWhiteSpace(appSecret) ||
        string.IsNullOrWhiteSpace(refreshToken))
        throw new InvalidOperationException("Dropbox is not configured. Please set Dropbox:AppKey, Dropbox:AppSecret, and Dropbox:RefreshToken in appsettings.json");

    Console.WriteLine("✅ Dropbox client initialized with refresh-token auth");
    return new DropboxClient(refreshToken, appKey, appSecret);
});

// ─── misc ────────────────────────────────────────────────────────────────
builder.Services.ConfigureHttpJsonOptions(o =>
        o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.AddCors();

var app = builder.Build();

// ─── AI job locks ───────────────────────────────────────────────────────────
var aiJobLocks = new ConcurrentDictionary<string, byte>();

bool TryStartAiJob(string key) => aiJobLocks.TryAdd(key, 0);
void EndAiJob(string key) => aiJobLocks.TryRemove(key, out _);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Anna's Archive v1"));
}

app.UseCors(p => p
    .WithOrigins(
        "https://fs01pfbooks.synology.me",      // Production HTTPS
        "http://fs01pfbooks.synology.me",       // Production HTTP (fallback)
        "http://localhost:4200",                // Local dev
        "https://localhost:4200"                // Local dev HTTPS
    )
    .AllowAnyHeader()
    .AllowAnyMethod());

// ─── security headers ────────────────────────────────────────────────
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

// ─── request body size limit (10MB for JSON payloads) ────────────────
app.Use(async (context, next) =>
{
    const long maxBodySize = 10 * 1024 * 1024; // 10 MB
    if (context.Request.ContentLength > maxBodySize)
    {
        context.Response.StatusCode = 413; // Payload Too Large
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Request body too large. Maximum size is 10 MB."
        });
        return;
    }
    await next();
});

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

static IResult? CheckTokenLimit(IConfiguration cfg, ITokenUsageService tokenUsage)
{
    var allowance = cfg.GetValue<long?>("OpenAI:MonthlyTokenAllowance");
    if (allowance.HasValue && tokenUsage.IsOverLimit(allowance.Value))
    {
        return Results.Problem(
            detail: "Monthly token allowance has been exceeded. The service will reset at the beginning of next month.",
            statusCode: 429,
            title: "Token Limit Exceeded"
        );
    }

    return null;
}

static bool IsTokenLimitExceeded(IConfiguration cfg, ITokenUsageService tokenUsage)
{
    var allowance = cfg.GetValue<long?>("OpenAI:MonthlyTokenAllowance");
    return allowance.HasValue && tokenUsage.IsOverLimit(allowance.Value);
}

var openLibraryAuthorCacheTtl = TimeSpan.FromHours(6);
var openLibraryAuthorCache = new Dictionary<string, (DateTime fetchedAt, List<AuthorSuggestion> authors)>();
var openLibraryAuthorCacheLock = new object();

bool TryGetOpenLibraryAuthorCache(string title, out List<AuthorSuggestion> authors)
{
    var key = title.Trim().ToLowerInvariant();
    lock (openLibraryAuthorCacheLock)
    {
        if (openLibraryAuthorCache.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow - entry.fetchedAt <= openLibraryAuthorCacheTtl)
            {
                authors = entry.authors;
                return true;
            }
            openLibraryAuthorCache.Remove(key);
        }
    }

    authors = new List<AuthorSuggestion>();
    return false;
}

void SetOpenLibraryAuthorCache(string title, List<AuthorSuggestion> authors)
{
    var key = title.Trim().ToLowerInvariant();
    lock (openLibraryAuthorCacheLock)
    {
        openLibraryAuthorCache[key] = (DateTime.UtcNow, authors);
    }
}

async Task<List<AuthorSuggestion>> FetchAuthorsFromOpenLibraryAsync(string title, IHttpClientFactory httpFactory)
{
    if (string.IsNullOrWhiteSpace(title)) return new List<AuthorSuggestion>();

    if (TryGetOpenLibraryAuthorCache(title, out var cached))
        return cached;

    try
    {
        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(3);

        var query = Uri.EscapeDataString(title.Trim());
        var url = $"https://openlibrary.org/search.json?title={query}&limit=10";
        using var response = await http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return new List<AuthorSuggestion>();

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        if (!doc.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
            return new List<AuthorSuggestion>();

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in docs.EnumerateArray())
        {
            if (!item.TryGetProperty("author_name", out var authorNames) || authorNames.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var author in authorNames.EnumerateArray())
            {
                var name = author.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var key = name.Trim();
                counts[key] = counts.TryGetValue(key, out var existing) ? existing + 1 : 1;
            }
        }

        if (counts.Count == 0) return new List<AuthorSuggestion>();

        var max = counts.Values.Max();
        string ConfidenceFromScore(int score)
        {
            var ratio = score / (double)max;
            if (ratio >= 0.66) return "high";
            if (ratio >= 0.34) return "medium";
            return "low";
        }

        var results = counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(5)
            .Select(kv => new AuthorSuggestion(kv.Key, ConfidenceFromScore(kv.Value)))
            .ToList();
        SetOpenLibraryAuthorCache(title, results);
        return results;
    }
    catch
    {
        return new List<AuthorSuggestion>();
    }
}

async Task<string?> FetchGoogleBooksCoverAsync(string title, string? author, IHttpClientFactory httpFactory)
{
    if (string.IsNullOrWhiteSpace(title)) return null;

    try
    {
        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(3);

        var candidateTitles = BuildCoverTitleCandidates(title);
        foreach (var candidate in candidateTitles)
        {
            var titleQuery = Uri.EscapeDataString(candidate);
            var authorQuery = string.IsNullOrWhiteSpace(author) ? "" : $"+inauthor:{Uri.EscapeDataString(author.Trim())}";
            var url = $"https://www.googleapis.com/books/v1/volumes?q=intitle:{titleQuery}{authorQuery}&maxResults=3";

            using var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode) continue;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("volumeInfo", out var volumeInfo)) continue;
                if (!volumeInfo.TryGetProperty("imageLinks", out var imageLinks)) continue;

                string? urlValue = null;
                if (imageLinks.TryGetProperty("thumbnail", out var thumb))
                    urlValue = thumb.GetString();
                else if (imageLinks.TryGetProperty("smallThumbnail", out var smallThumb))
                    urlValue = smallThumb.GetString();

                if (string.IsNullOrWhiteSpace(urlValue)) continue;
                return urlValue.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!string.IsNullOrWhiteSpace(author))
        {
            return await FetchGoogleBooksCoverAsync(title, null, httpFactory);
        }
    }
    catch
    {
        return null;
    }

    return null;
}

async Task<string?> FetchOpenLibraryCoverAsync(string title, string? author, IHttpClientFactory httpFactory)
{
    if (string.IsNullOrWhiteSpace(title)) return null;

    try
    {
        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(3);

        var candidateTitles = BuildCoverTitleCandidates(title);
        foreach (var candidate in candidateTitles)
        {
            var titleQuery = Uri.EscapeDataString(candidate);
            var authorQuery = string.IsNullOrWhiteSpace(author) ? "" : $"&author={Uri.EscapeDataString(author.Trim())}";
            var url = $"https://openlibrary.org/search.json?title={titleQuery}{authorQuery}&limit=10";

            using var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode) continue;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
                continue;

            int bestScore = -1;
            int bestCoverId = -1;

            foreach (var item in docs.EnumerateArray())
            {
                if (!item.TryGetProperty("cover_i", out var coverProp) || coverProp.ValueKind != JsonValueKind.Number)
                    continue;

                var coverId = coverProp.GetInt32();
                var editionCount = item.TryGetProperty("edition_count", out var editionProp) && editionProp.ValueKind == JsonValueKind.Number
                    ? editionProp.GetInt32()
                    : 0;

                if (editionCount > bestScore)
                {
                    bestScore = editionCount;
                    bestCoverId = coverId;
                }
            }

            if (bestCoverId > 0)
                return $"https://covers.openlibrary.org/b/id/{bestCoverId}-L.jpg";
        }

        if (!string.IsNullOrWhiteSpace(author))
        {
            return await FetchOpenLibraryCoverAsync(title, null, httpFactory);
        }
    }
    catch
    {
        return null;
    }

    return null;
}

async Task<List<string>> FetchOpenLibraryCoverCandidatesAsync(string title, string? author, IHttpClientFactory httpFactory, int limit = 12)
{
    if (string.IsNullOrWhiteSpace(title))
        return new List<string>();

    try
    {
        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(4);

        var coverScores = new Dictionary<int, int>();
        var candidateTitles = BuildCoverTitleCandidates(title);

        foreach (var candidate in candidateTitles)
        {
            var titleQuery = Uri.EscapeDataString(candidate);
            var authorQuery = string.IsNullOrWhiteSpace(author) ? "" : $"&author={Uri.EscapeDataString(author.Trim())}";
            var url = $"https://openlibrary.org/search.json?title={titleQuery}{authorQuery}&limit=20";

            using var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode) continue;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in docs.EnumerateArray())
            {
                if (!item.TryGetProperty("cover_i", out var coverProp) || coverProp.ValueKind != JsonValueKind.Number)
                    continue;

                var coverId = coverProp.GetInt32();
                var editionCount = item.TryGetProperty("edition_count", out var editionProp) && editionProp.ValueKind == JsonValueKind.Number
                    ? editionProp.GetInt32()
                    : 0;

                if (coverScores.TryGetValue(coverId, out var existing))
                {
                    if (editionCount > existing)
                        coverScores[coverId] = editionCount;
                }
                else
                {
                    coverScores[coverId] = editionCount;
                }
            }
        }

        var covers = coverScores
            .OrderByDescending(kvp => kvp.Value)
            .Take(limit)
            .Select(kvp => $"https://covers.openlibrary.org/b/id/{kvp.Key}-L.jpg")
            .ToList();

        if (covers.Count == 0 && !string.IsNullOrWhiteSpace(author))
            return await FetchOpenLibraryCoverCandidatesAsync(title, null, httpFactory, limit);

        return covers;
    }
    catch
    {
        return new List<string>();
    }
}

async Task<List<string>> FetchGoogleBooksCoverCandidatesAsync(string title, string? author, IHttpClientFactory httpFactory, int limit = 12)
{
    if (string.IsNullOrWhiteSpace(title))
        return new List<string>();

    try
    {
        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(5);

        var query = string.IsNullOrWhiteSpace(author)
            ? $"intitle:{title}"
            : $"intitle:{title} inauthor:{author}";
        var url = $"https://www.googleapis.com/books/v1/volumes?q={Uri.EscapeDataString(query)}&maxResults={Math.Max(limit, 5)}";

        using var response = await http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            return new List<string>();

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return new List<string>();

        var results = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("volumeInfo", out var info)) continue;
            if (!info.TryGetProperty("imageLinks", out var imageLinks)) continue;

            string? urlValue = null;
            if (imageLinks.TryGetProperty("thumbnail", out var thumb))
                urlValue = thumb.GetString();
            else if (imageLinks.TryGetProperty("smallThumbnail", out var smallThumb))
                urlValue = smallThumb.GetString();

            if (string.IsNullOrWhiteSpace(urlValue)) continue;
            urlValue = urlValue.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
            results.Add(urlValue);
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
    }
    catch
    {
        return new List<string>();
    }
}

async Task EnrichBookCoversAsync(List<BookDto> books, IHttpClientFactory httpFactory, int maxToEnrich = 30)
{
    var targets = books
        .Where(b => string.IsNullOrWhiteSpace(b.Isbn))
        .Take(maxToEnrich)
        .ToList();

    if (targets.Count == 0) return;

    Console.WriteLine($"🖼️ Cover enrichment: {targets.Count}/{books.Count} candidates");

    var sem = new SemaphoreSlim(4);
    var tasks = targets.Select(async book =>
    {
        await sem.WaitAsync();
        try
        {
            var author = book.Authors?.FirstOrDefault();
            var title = book.Title ?? string.Empty;

            var cover = await FetchOpenLibraryCoverAsync(title, author, httpFactory)
                        ?? await FetchGoogleBooksCoverAsync(title, author, httpFactory);

            if (string.IsNullOrWhiteSpace(cover)) return;

            if (!book.CoverCandidates.Contains(cover, StringComparer.OrdinalIgnoreCase))
                book.CoverCandidates.Add(cover);

            Console.WriteLine($"✅ Cover enriched: {title} | {author} -> {cover}");
        }
        finally
        {
            sem.Release();
        }
    });

    await Task.WhenAll(tasks);
}

static List<string> BuildCoverTitleCandidates(string title)
{
    var candidates = new List<string>();
    var trimmed = title.Trim();
    if (string.IsNullOrWhiteSpace(trimmed)) return candidates;

    string Simplify(string value)
    {
        var withoutBracket = Regex.Replace(value, @"\[[^\]]+\]", "").Trim();
        var withoutParens = Regex.Replace(withoutBracket, @"\([^)]+\)", "").Trim();
        var withoutSeries = Regex.Replace(withoutParens, @"\bbook\s+\d+\b", "", RegexOptions.IgnoreCase).Trim();
        var withoutDash = Regex.Replace(withoutSeries, @"\s*-\s*\d+\s*-\s*", " ").Trim();
        return Regex.Replace(withoutDash, @"\s{2,}", " ").Trim();
    }

    var baseTitle = Simplify(trimmed);
    candidates.Add(baseTitle);

    var colonSplit = baseTitle.Split(':')[0].Trim();
    if (!string.Equals(colonSplit, baseTitle, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(colonSplit))
        candidates.Add(colonSplit);

    if (!string.Equals(trimmed, baseTitle, StringComparison.OrdinalIgnoreCase))
        candidates.Add(trimmed);

    return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

const int MinCoverWidth = 400;
const int MinCoverHeight = 600;
const double TargetCoverRatio = 1.6;
const double CoverRatioTolerance = 0.3;

static bool IsCoverSizeValid(int width, int height)
{
    if (width < MinCoverWidth || height < MinCoverHeight)
        return false;

    var ratio = height / (double)width;
    return Math.Abs(ratio - TargetCoverRatio) <= CoverRatioTolerance;
}

static bool TryGetImageSize(byte[] data, out int width, out int height)
{
    width = 0;
    height = 0;

    if (data.Length < 10)
        return false;

    // PNG: 89 50 4E 47 0D 0A 1A 0A, IHDR at offset 12
    if (data.Length >= 24 &&
        data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
    {
        width = ReadInt32BigEndian(data, 16);
        height = ReadInt32BigEndian(data, 20);
        return width > 0 && height > 0;
    }

    // GIF: "GIF87a" or "GIF89a"
    if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
    {
        width = data[6] | (data[7] << 8);
        height = data[8] | (data[9] << 8);
        return width > 0 && height > 0;
    }

    // JPEG
    if (data[0] == 0xFF && data[1] == 0xD8)
    {
        return TryGetJpegSize(data, out width, out height);
    }

    return false;
}

static int ReadInt32BigEndian(byte[] data, int offset)
{
    return (data[offset] << 24)
         | (data[offset + 1] << 16)
         | (data[offset + 2] << 8)
         | data[offset + 3];
}

static bool TryGetJpegSize(byte[] data, out int width, out int height)
{
    width = 0;
    height = 0;

    int index = 2;
    while (index + 9 < data.Length)
    {
        if (data[index] != 0xFF)
        {
            index++;
            continue;
        }

        byte marker = data[index + 1];
        if (marker == 0xD9 || marker == 0xDA)
            break;

        if (index + 3 >= data.Length)
            break;

        int length = (data[index + 2] << 8) + data[index + 3];
        if (length < 2 || index + 2 + length > data.Length)
            break;

        if (marker == 0xC0 || marker == 0xC2)
        {
            height = (data[index + 5] << 8) + data[index + 6];
            width = (data[index + 7] << 8) + data[index + 8];
            return width > 0 && height > 0;
        }

        index += 2 + length;
    }

    return false;
}

static string DetermineImageExtension(string url, byte[] imageData)
{
    var urlLower = url.ToLowerInvariant();
    if (urlLower.EndsWith(".jpg") || urlLower.EndsWith(".jpeg"))
        return ".jpg";
    if (urlLower.EndsWith(".png"))
        return ".png";
    if (urlLower.EndsWith(".gif"))
        return ".gif";

    if (imageData.Length >= 4)
    {
        if (imageData[0] == 0x89 && imageData[1] == 0x50 && imageData[2] == 0x4E && imageData[3] == 0x47)
            return ".png";
        if (imageData[0] == 0xFF && imageData[1] == 0xD8 && imageData[2] == 0xFF)
            return ".jpg";
        if (imageData[0] == 0x47 && imageData[1] == 0x49 && imageData[2] == 0x46)
            return ".gif";
    }

    return ".jpg";
}

static async Task<List<SeriesBook>> FetchBookCovers(List<SeriesBook> books, string author, IHttpClientFactory httpFactory)
{
    if (books == null || books.Count == 0)
        return books ?? new List<SeriesBook>();

    var result = new List<SeriesBook>();
    using var http = httpFactory.CreateClient();
    http.Timeout = TimeSpan.FromSeconds(5); // Short timeout for cover fetching

    foreach (var book in books)
    {
        string? coverUrl = null;
        try
        {
            // Google Books API - free, no auth required
            var encodedTitle = Uri.EscapeDataString(book.Title);
            var encodedAuthor = Uri.EscapeDataString(author);
            var googleBooksUrl = $"https://www.googleapis.com/books/v1/volumes?q=intitle:{encodedTitle}+inauthor:{encodedAuthor}&maxResults=1";

            var response = await http.GetAsync(googleBooksUrl);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                // Navigate: root.items[0].volumeInfo.imageLinks.thumbnail
                if (doc.RootElement.TryGetProperty("items", out var items) &&
                    items.GetArrayLength() > 0)
                {
                    var firstItem = items[0];
                    if (firstItem.TryGetProperty("volumeInfo", out var volumeInfo) &&
                        volumeInfo.TryGetProperty("imageLinks", out var imageLinks) &&
                        imageLinks.TryGetProperty("thumbnail", out var thumbnail))
                    {
                        coverUrl = thumbnail.GetString();
                        // Upgrade HTTP to HTTPS if needed
                        if (!string.IsNullOrEmpty(coverUrl) && coverUrl.StartsWith("http:"))
                        {
                            coverUrl = coverUrl.Replace("http:", "https:");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to fetch cover for '{book.Title}': {ex.Message}");
        }

        result.Add(new SeriesBook(book.Title, book.Order, book.Description, coverUrl));
    }

    return result;
}

// ─── 1) search ───────────────────────────────────────────────────────────
app.MapGet("/api/anna/book", async (
    [FromQuery] string name,
    AnnaArchiveService svc,
    IValidationService validation,
    IHttpClientFactory httpFactory,
    [FromQuery] bool exact = false) =>
{
    if (!validation.IsValidSearchQuery(name))
        return Results.BadRequest(new {
            error = "Query parameter 'name' is required and must be between 1 and 500 characters."
        });

    var books = (await svc.SearchAsync(name, searchLimit, exact)).ToList();

    if (exact)
        books = books
            .Where(b => string.Equals(b.Title?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

    return books.Any()
        ? books.Count == 1 ? Results.Ok(books[0]) : Results.Ok(books)
        : ApiResponse.NotFound("No books found matching that name.");
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 1a) Cover lookup (OpenLibrary -> Google Books) ───────────────────────
app.MapGet("/api/anna/book/cover", async (
    [FromQuery] string title,
    [FromQuery] string? author,
    IHttpClientFactory httpFactory) =>
{
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest(new { error = "title is required." });

    Console.WriteLine($"🖼️ Cover lookup: title='{title}', author='{author}'");
    var cover = await FetchOpenLibraryCoverAsync(title, author, httpFactory)
                ?? await FetchGoogleBooksCoverAsync(title, author, httpFactory);

    Console.WriteLine(cover is null
        ? $"⚠️ Cover lookup failed for '{title}'"
        : $"✅ Cover lookup found for '{title}': {cover}");

    return Results.Ok(new { coverUrl = cover });
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 2) non-member download ──────────────────────────────────────────────
app.MapGet("/api/anna/book/{md5}/download", async (
    [FromRoute] string md5,
    AnnaArchiveService svc,
    IValidationService validation) =>
{
    if (!validation.IsValidMd5(md5))
        return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

    var links = await svc.GetDownloadLinksAsync(md5);
    return links.Any()
        ? Results.Ok(new { id = md5, downloadLinks = links })
        : ApiResponse.NotFound("No download links found.");
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3) member download (url + counters) ─────────────────────────────────
app.MapPost("/api/anna/book/{md5}/download/member", async (
    [FromRoute] string md5,
    [FromQuery] string? title,
    [FromQuery] string? coverUrl,
    [FromQuery] string? authors,
    [FromQuery] string? format,
    [FromQuery] string? fileSize,
    [FromQuery] string? source,
    AnnaArchiveService anna,
    IValidationService validation,
    IEbookCoverService coverService,
    IDownloadTrackingService downloadTracking,
    HttpContext context) =>
{
    if (!validation.IsValidMd5(md5))
        return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

    if (!validation.IsValidTitle(title))
        return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

    // Get user name from auth context
    var userName = context.User?.FindFirst(ClaimTypes.Email)?.Value
        ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
        ?? "unknown";

    // Use shared helper to download book from Anna's Archive
    var (resp, fileName, acctInfo, errorMessage) = await DownloadBookFromAnnaArchiveAsync(md5, title, anna, memberKey);

    if (errorMessage != null)
    {
        // Get current download status even on failure
        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
        return Results.Ok(new { success = false, message = errorMessage, accountFastInfo = trackingInfo });
    }

    if (resp == null || fileName == null)
    {
        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
        return Results.Ok(new { success = false, message = "Failed to download book.", accountFastInfo = trackingInfo });
    }

    // Record successful download in our tracking system
    downloadTracking.RecordDownload(md5, userName);
    Console.WriteLine($"[download-member] Recorded download for user {userName}, MD5: {md5}");

    // Get updated download status
    var (currentDownloadsLeft, currentDownloadsPerDay) = downloadTracking.GetDownloadStatus();

    using (resp)
    {
        Stream ebookStream = await resp.Content.ReadAsStreamAsync();

        // Attempt cover replacement if coverUrl is provided and format is supported
        if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            var ext = Path.GetExtension(fileName).TrimStart('.');
            if (coverService.IsFormatSupported(ext))
            {
                Console.WriteLine($"[download-member] Attempting cover replacement for {fileName}");
                ebookStream = await coverService.ReplaceCoverAsync(ebookStream, coverUrl, ext);
            }
            else
            {
                Console.WriteLine($"[download-member] Format {ext} not supported for cover replacement, skipping");
            }
        }

        // Set content type based on file extension
        var contentType = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".epub" => "application/epub+zip",
            ".pdf" => "application/pdf",
            ".mobi" => "application/x-mobipocket-ebook",
            ".azw3" => "application/vnd.amazon.ebook",
            ".fb2" => "text/xml",
            _ => "application/octet-stream"
        };

        // Stream the file back to the client
        return Results.Stream(ebookStream, contentType, fileName);
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3a) send-to-library (save to Synology disk) ──────────────────────────
app.MapPost("/api/anna/book/{md5}/send-to-library", async (
    [FromRoute] string md5,
    [FromQuery] string? title,
    [FromQuery] string? coverUrl,
    [FromQuery] string? authors,
    [FromQuery] string? format,
    [FromQuery] string? fileSize,
    [FromQuery] string? source,
    AnnaArchiveService anna,
    IValidationService validation,
    IEbookCoverService coverService,
    IDownloadTrackingService downloadTracking,
    HttpContext context) =>
{
    if (!validation.IsValidMd5(md5))
        return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

    if (!validation.IsValidTitle(title))
        return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

    // Get user name from auth context
    var userName = context.User?.FindFirst(ClaimTypes.Email)?.Value
        ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
        ?? "unknown";
    var userTag = ResolveUserLibraryTag(context);

    // Use shared helper to download book from Anna's Archive
    var (resp, fileName, acctInfo, errorMessage) = await DownloadBookFromAnnaArchiveAsync(md5, title, anna, memberKey);

    if (errorMessage != null)
    {
        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
        return Results.Ok(new { success = false, message = errorMessage, accountFastInfo = trackingInfo });
    }

    if (resp == null || fileName == null)
    {
        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
        return Results.Ok(new { success = false, message = "Failed to download book.", accountFastInfo = trackingInfo });
    }

    // Record successful download in our tracking system
    downloadTracking.RecordDownload(md5, userName);
    Console.WriteLine($"[library-anna] Recorded download for user {userName}, MD5: {md5}");

    // Get updated download status
    var (currentDownloadsLeft, currentDownloadsPerDay) = downloadTracking.GetDownloadStatus();
    var currentTrackingInfo = new AccountFastDownloadInfoDto(currentDownloadsLeft, currentDownloadsPerDay);

    var libraryRoot = ResolveLibraryRoot();
    Directory.CreateDirectory(libraryRoot);

    using (resp)
    {
        Stream ebookStream = await resp.Content.ReadAsStreamAsync();

        // Attempt cover replacement if coverUrl is provided and format is supported
        if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            var ext = Path.GetExtension(fileName).TrimStart('.');
            if (coverService.IsFormatSupported(ext))
            {
                Console.WriteLine($"[library-anna] Attempting cover replacement for {fileName}");
                ebookStream = await coverService.ReplaceCoverAsync(ebookStream, coverUrl, ext);
            }
            else
            {
                Console.WriteLine($"[library-anna] Format {ext} not supported for cover replacement, skipping");
            }
        }

        var destinationPath = Path.Combine(libraryRoot, fileName);
        if (File.Exists(destinationPath))
        {
            return Results.Ok(new
            {
                success = true,
                message = "File already exists in library.",
                fileName,
                path = destinationPath,
                accountFastInfo = currentTrackingInfo
            });
        }

        await using var outStream = File.Create(destinationPath);
        await ebookStream.CopyToAsync(outStream);

        await WriteLibraryMetadataAsync(libraryRoot, fileName, md5, title, authors, format, fileSize, coverUrl, source, userTag);

        return Results.Ok(new
        {
            success = true,
            message = "Saved to library.",
            fileName,
            path = destinationPath,
            accountFastInfo = currentTrackingInfo
        });
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3.5) Check download counter status ────────────────────────────────────────
app.MapGet("/api/anna/download-status", (IDownloadTrackingService downloadTracking) =>
{
    try
    {
        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        var acctInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);

        return Results.Ok(new { accountFastInfo = acctInfo });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { accountFastInfo = (AccountFastDownloadInfoDto?)null, error = ex.Message });
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ═══════════════════════════════════════════════════════════════════════════
// ═══ LIBGEN ENDPOINTS ══════════════════════════════════════════════════════
// ═══════════════════════════════════════════════════════════════════════════

// ─── LibGen search ───────────────────────────────────────────────────────
app.MapGet("/api/libgen/book", async (
    [FromQuery] string name,
    LibGenService svc,
    IValidationService validation,
    [FromQuery] bool exact = false) =>
{
    Console.WriteLine($"[API LibGen Search] Received request: name='{name}', exact={exact}");

    if (!validation.IsValidSearchQuery(name))
    {
        Console.WriteLine($"[API LibGen Search] Validation failed for query: '{name}'");
        return Results.BadRequest(new {
            error = "Query parameter 'name' is required and must be between 1 and 500 characters."
        });
    }

    Console.WriteLine($"[API LibGen Search] Calling LibGenService.SearchAsync...");
    var books = (await svc.SearchAsync(name, searchLimit, exact)).ToList();
    Console.WriteLine($"[API LibGen Search] Service returned {books.Count} books");

    if (exact)
    {
        var originalCount = books.Count;
        books = books
            .Where(b => string.Equals(b.Title?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        Console.WriteLine($"[API LibGen Search] After exact filter: {books.Count} books (was {originalCount})");
    }

    if (books.Any())
    {
        var result = books.Count == 1 ? Results.Ok(books[0]) : Results.Ok(books);
        Console.WriteLine($"[API LibGen Search] Returning {books.Count} books");
        return result;
    }
    else
    {
        Console.WriteLine($"[API LibGen Search] No books found, returning 404");
        return ApiResponse.NotFound("No books found matching that name.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── LibGen download ─────────────────────────────────────────────────────
app.MapPost("/api/libgen/book/{md5}/download/member", async (
    [FromRoute] string md5,
    [FromQuery] string? title,
    [FromQuery] string? coverUrl,
    [FromQuery] string? authors,
    [FromQuery] string? format,
    [FromQuery] string? fileSize,
    [FromQuery] string? source,
    LibGenService libgen,
    IValidationService validation,
    IEbookCoverService coverService,
    IDownloadTrackingService downloadTracking,
    HttpContext context) =>
{
    if (!validation.IsValidMd5(md5))
        return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

    if (!validation.IsValidTitle(title))
        return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

    // Get user name from auth context
    var userName = context.User?.FindFirst(ClaimTypes.Email)?.Value
        ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
        ?? "unknown";

    Console.WriteLine($"📚 [LibGen] Downloading book {md5} for user {userName}...");

    // Download the book from LibGen
    var resp = await libgen.GetDownloadResponseAsync(md5, HttpCompletionOption.ResponseHeadersRead);

    if (resp == null || !resp.IsSuccessStatusCode)
    {
        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
        Console.WriteLine($"❌ [LibGen] Failed to download book {md5}");
        return Results.Ok(new { success = false, message = "Failed to download book from LibGen.", accountFastInfo = trackingInfo });
    }

    // Sanitize title and determine file extension
    var rawTitle = !string.IsNullOrWhiteSpace(title) ? title : md5;
    var safeTitle = Regex.Replace(rawTitle, $"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]", "_");

    var downloadUrl = await libgen.GetDownloadUrlAsync(md5);
    var ext = !string.IsNullOrEmpty(downloadUrl) ? Path.GetExtension(new Uri(downloadUrl).AbsolutePath) : "";

    if (string.IsNullOrEmpty(ext))
        ext = resp.Content.Headers.ContentType?.MediaType switch
        {
            "application/pdf"                 => ".pdf",
            "application/epub+zip"            => ".epub",
            "application/x-mobipocket-ebook"  => ".mobi",
            _                                 => ".bin"
        };

    var fileName = $"{safeTitle}{ext}";

    Console.WriteLine($"✅ [LibGen] Downloaded: {fileName}");

    // Record successful download in our tracking system
    downloadTracking.RecordDownload(md5, userName);
    Console.WriteLine($"[download-libgen] Recorded download for user {userName}, MD5: {md5}");

    // Get updated download status
    var (currentDownloadsLeft, currentDownloadsPerDay) = downloadTracking.GetDownloadStatus();

    using (resp)
    {
        Stream ebookStream = await resp.Content.ReadAsStreamAsync();

        // Attempt cover replacement if coverUrl is provided and format is supported
        if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            var extNoDot = ext.TrimStart('.');
            if (coverService.IsFormatSupported(extNoDot))
            {
                Console.WriteLine($"[download-libgen] Attempting cover replacement for {fileName}");
                ebookStream = await coverService.ReplaceCoverAsync(ebookStream, coverUrl, extNoDot);
            }
            else
            {
                Console.WriteLine($"[download-libgen] Format {extNoDot} not supported for cover replacement, skipping");
            }
        }

        // Set content type based on file extension
        var contentType = ext.ToLowerInvariant() switch
        {
            ".epub" => "application/epub+zip",
            ".pdf" => "application/pdf",
            ".mobi" => "application/x-mobipocket-ebook",
            ".azw3" => "application/vnd.amazon.ebook",
            ".fb2" => "text/xml",
            _ => "application/octet-stream"
        };

        // Stream the file back to the client
        return Results.Stream(ebookStream, contentType, fileName);
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── LibGen send-to-library ──────────────────────────────────────────────
app.MapPost("/api/libgen/book/{md5}/send-to-library", async (
    [FromRoute] string md5,
    [FromQuery] string? title,
    [FromQuery] string? coverUrl,
    [FromQuery] string? authors,
    [FromQuery] string? format,
    [FromQuery] string? fileSize,
    [FromQuery] string? source,
    LibGenService libgen,
    IValidationService validation,
    IEbookCoverService coverService,
    IDownloadTrackingService downloadTracking,
    HttpContext context) =>
{
    if (!validation.IsValidMd5(md5))
        return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

    if (!validation.IsValidTitle(title))
        return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

    // Get user name from auth context
    var userName = context.User?.FindFirst(ClaimTypes.Email)?.Value
        ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
        ?? "unknown";
    var userTag = ResolveUserLibraryTag(context);

    Console.WriteLine($"📚 [LibGen] Saving book {md5} to library for user {userName}...");

    // Download the book from LibGen
    var resp = await libgen.GetDownloadResponseAsync(md5, HttpCompletionOption.ResponseHeadersRead);

    if (resp == null || !resp.IsSuccessStatusCode)
    {
        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
        Console.WriteLine($"❌ [LibGen] Failed to download book {md5}");
        return Results.Ok(new { success = false, message = "Failed to download book from LibGen.", accountFastInfo = trackingInfo });
    }

    // Sanitize title and determine file extension
    var rawTitle = !string.IsNullOrWhiteSpace(title) ? title : md5;
    var safeTitle = Regex.Replace(rawTitle, $"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]", "_");

    var downloadUrl = await libgen.GetDownloadUrlAsync(md5);
    var ext = !string.IsNullOrEmpty(downloadUrl) ? Path.GetExtension(new Uri(downloadUrl).AbsolutePath) : "";

    if (string.IsNullOrEmpty(ext))
        ext = resp.Content.Headers.ContentType?.MediaType switch
        {
            "application/pdf"                 => ".pdf",
            "application/epub+zip"            => ".epub",
            "application/x-mobipocket-ebook"  => ".mobi",
            _                                 => ".bin"
        };

    var fileName = $"{safeTitle}{ext}";

    // Record successful download in our tracking system
    downloadTracking.RecordDownload(md5, userName);
    Console.WriteLine($"[library-libgen] Recorded download for user {userName}, MD5: {md5}");

    // Get updated download status
    var (currentDownloadsLeft, currentDownloadsPerDay) = downloadTracking.GetDownloadStatus();
    var currentTrackingInfo = new AccountFastDownloadInfoDto(currentDownloadsLeft, currentDownloadsPerDay);

    var libraryRoot = ResolveLibraryRoot();
    Directory.CreateDirectory(libraryRoot);

    using (resp)
    {
        Stream ebookStream = await resp.Content.ReadAsStreamAsync();

        // Attempt cover replacement if coverUrl is provided and format is supported
        if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            var extNoDot = ext.TrimStart('.');
            if (coverService.IsFormatSupported(extNoDot))
            {
                Console.WriteLine($"[library-libgen] Attempting cover replacement for {fileName}");
                ebookStream = await coverService.ReplaceCoverAsync(ebookStream, coverUrl, extNoDot);
            }
            else
            {
                Console.WriteLine($"[library-libgen] Format {extNoDot} not supported for cover replacement, skipping");
            }
        }

        var destinationPath = Path.Combine(libraryRoot, fileName);
        if (File.Exists(destinationPath))
        {
            return Results.Ok(new
            {
                success = true,
                message = "File already exists in library.",
                fileName,
                path = destinationPath,
                accountFastInfo = currentTrackingInfo
            });
        }

        await using var outStream = File.Create(destinationPath);
        await ebookStream.CopyToAsync(outStream);

        await WriteLibraryMetadataAsync(libraryRoot, fileName, md5, title, authors, format, fileSize, coverUrl, source, userTag);

        return Results.Ok(new
        {
            success = true,
            message = "Saved to library.",
            fileName,
            path = destinationPath,
            accountFastInfo = currentTrackingInfo
        });
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ═══════════════════════════════════════════════════════════════════════════
// ═══ END LIBGEN ENDPOINTS ══════════════════════════════════════════════════
// ═══════════════════════════════════════════════════════════════════════════

// ─── SLUM Health Status Proxy ───────────────────────────────────────────
app.MapGet("/api/anna/slum-health", async (IHttpClientFactory httpFactory) =>
{
    try
    {
        using var http = httpFactory.CreateClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        Console.WriteLine("[slum-health] Fetching status page data...");
        var statusResponse = await http.GetAsync("https://open-slum.org/api/status-page/slum");
        if (!statusResponse.IsSuccessStatusCode)
        {
            Console.WriteLine($"❌ [slum-health] Failed to fetch status page: {statusResponse.StatusCode}");
            return Results.Json(new { success = false, error = "Failed to fetch status page data" });
        }

        Console.WriteLine("[slum-health] Fetching heartbeat data...");
        var heartbeatResponse = await http.GetAsync("https://open-slum.org/api/status-page/heartbeat/slum");
        if (!heartbeatResponse.IsSuccessStatusCode)
        {
            Console.WriteLine($"❌ [slum-health] Failed to fetch heartbeat: {heartbeatResponse.StatusCode}");
            return Results.Json(new { success = false, error = "Failed to fetch heartbeat data" });
        }

        var statusJson = await statusResponse.Content.ReadAsStringAsync();
        var heartbeatJson = await heartbeatResponse.Content.ReadAsStringAsync();

        using var statusDoc = JsonDocument.Parse(statusJson);
        using var heartbeatDoc = JsonDocument.Parse(heartbeatJson);

        // Build result array with Anna's Archive monitors
        var result = new List<object>();
        var heartbeats = heartbeatDoc.RootElement.GetProperty("heartbeatList");

        // Find Anna's Archive group
        if (statusDoc.RootElement.TryGetProperty("publicGroupList", out var groups))
        {
            foreach (var group in groups.EnumerateArray())
            {
                if (group.TryGetProperty("name", out var groupName) &&
                    groupName.GetString()?.Contains("Anna's Archive") == true)
                {
                    foreach (var monitor in group.GetProperty("monitorList").EnumerateArray())
                    {
                        var name = monitor.GetProperty("name").GetString() ?? "";
                        var id = monitor.GetProperty("id").GetInt32().ToString();

                        // Calculate health percentage from heartbeats
                        double health = 0;
                        if (heartbeats.TryGetProperty(id, out var monitorHeartbeats))
                        {
                            int upCount = 0, totalCount = 0;
                            foreach (var heartbeat in monitorHeartbeats.EnumerateArray())
                            {
                                totalCount++;
                                if (heartbeat.GetProperty("status").GetInt32() == 1) upCount++;
                            }
                            health = totalCount > 0 ? Math.Round((double)upCount / totalCount * 100, 2) : 0;
                        }

                        // Get cert expiry
                        int? certExpDays = null;
                        if (monitor.TryGetProperty("certExpiryDaysRemaining", out var cert))
                        {
                            certExpDays = cert.GetInt32();
                        }

                        result.Add(new
                        {
                            name = name,
                            health = $"{health}%",
                            cert_exp = certExpDays.HasValue ? $"{certExpDays} days" : null
                        });
                    }
                    break;
                }
            }
        }

        Console.WriteLine($"✅ [slum-health] Returning {result.Count} monitors");
        return Results.Json(result);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ [slum-health] Error: {ex.Message}");
        return Results.Json(new { success = false, error = ex.Message });
    }
});

// ─── 3b) Anna's Archive mirror health (direct probe) ──────────────────────
app.MapGet("/api/anna/mirror-health", async (IHttpClientFactory httpFactory) =>
{
    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(4);

    var mirrors = new[]
    {
        ("org", "https://annas-archive.org"),
        ("se",  "https://annas-archive.se"),
        ("li",  "https://annas-archive.li"),
        ("pm",  "https://annas-archive.pm"),
        ("in",  "https://annas-archive.in")
    };

    var results = new List<object>();

    foreach (var (extension, url) in mirrors)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            sw.Stop();

            var statusCode = (int)resp.StatusCode;
            var ok = statusCode >= 200 && statusCode < 500;
            results.Add(new
            {
                name = $"Anna's Archive {extension.ToUpperInvariant()}",
                extension,
                health = ok ? 100 : 0,
                statusCode,
                responseMs = sw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            results.Add(new
            {
                name = $"Anna's Archive {extension.ToUpperInvariant()}",
                extension,
                health = (int?)null,
                statusCode = (int?)null,
                responseMs = sw.ElapsedMilliseconds,
                error = ex.GetType().Name
            });
        }
    }

    return Results.Json(results);
});

// ─── 3c) Library listing ─────────────────────────────────────────────────
app.MapGet("/api/library/books", (HttpContext context) =>
{
    var libraryRoot = ResolveLibraryRoot();
    if (!Directory.Exists(libraryRoot))
        return Results.Json(Array.Empty<LibraryBookDto>());

    var metaFiles = Directory.GetFiles(libraryRoot, "*.meta.json");
    var jsonOptions = CreateLibraryJsonOptions();
    var books = new List<LibraryBookDto>();
    var metaLookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

    foreach (var metaFile in metaFiles)
    {
        try
        {
            var json = File.ReadAllText(metaFile);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);
            if (meta == null)
                continue;

            metaLookup.Add(meta.FileName);
            var coverUrl = NormalizeLibraryCoverUrl(meta.CoverUrl, baseUrl)
                ?? FindLocalCoverUrl(libraryRoot, meta.FileName, baseUrl);

            var genres = meta.Genres ?? Array.Empty<string>();
            var tags = meta.Tags ?? genres;
            var primaryGenre = meta.PrimaryGenre ?? genres.FirstOrDefault() ?? tags.FirstOrDefault();

        books.Add(new LibraryBookDto(
            meta.Title ?? Path.GetFileNameWithoutExtension(meta.FileName),
            meta.Authors ?? Array.Empty<string>(),
            meta.Format ?? Path.GetExtension(meta.FileName).TrimStart('.').ToUpperInvariant(),
            meta.FileSize ?? "",
            meta.FileName,
            coverUrl,
            meta.Source,
            meta.Md5,
            meta.SavedAt,
            primaryGenre,
            tags,
            meta.Series,
            genres,
            meta.PublishedDate,
            meta.Pages,
            meta.GoodreadsRating,
            meta.PersonalRating,
            meta.ReaderEnabled
        ));
    }
        catch
        {
            // ignore malformed meta files
        }
    }

    var supportedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { ".epub", ".pdf", ".mobi", ".azw3", ".azw", ".kfx", ".pobi", ".fb2" };

    foreach (var filePath in Directory.GetFiles(libraryRoot))
    {
        try
        {
            var ext = Path.GetExtension(filePath);
            if (!supportedExts.Contains(ext))
                continue;

            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(fileName) || metaLookup.Contains(fileName))
                continue;

            var info = new FileInfo(filePath);
            books.Add(new LibraryBookDto(
                Path.GetFileNameWithoutExtension(fileName),
                Array.Empty<string>(),
                ext.TrimStart('.').ToUpperInvariant(),
                FormatFileSize(info.Length),
                fileName,
                null,
                null,
                null,
                info.LastWriteTimeUtc,
                null,
                Array.Empty<string>(),
                null,
                Array.Empty<string>(),
                null,
                null,
                null,
                null,
                null
            ));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[library] Skipping file {filePath}: {ex.Message}");
        }
    }

    var ordered = books
        .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
        .ToList();

    return Results.Json(ordered);
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3d) Library cover file ──────────────────────────────────────────────
app.MapGet("/api/library/cover/{*path}", (HttpContext context, string path) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.NotFound();

    var libraryRoot = ResolveLibraryRoot();
    var fullPath = Path.GetFullPath(Path.Combine(libraryRoot, path));
    if (!fullPath.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Invalid path." });

    if (!File.Exists(fullPath))
        return Results.NotFound();

    var ext = Path.GetExtension(fullPath).ToLowerInvariant();
    var contentType = ext switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        _ => "application/octet-stream"
    };

    return Results.File(fullPath, contentType);
})
.RequireRateLimiting("api");

// ─── Library cover candidates ────────────────────────────────────────────
app.MapGet("/api/library/book/cover-candidates", async (
    [FromQuery] string title,
    [FromQuery] string? author,
    IHttpClientFactory httpFactory) =>
{
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest(new { error = "title is required." });

    var openLibrary = await FetchOpenLibraryCoverCandidatesAsync(title, author, httpFactory);
    var google = await FetchGoogleBooksCoverCandidatesAsync(title, author, httpFactory);
    var covers = openLibrary.Concat(google)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    return Results.Ok(new { covers });
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3e) Library send-to-kindle ──────────────────────────────────────────
app.MapPost("/api/library/book/send-to-kindle", async (
    [FromQuery] string fileName,
    [FromQuery] string target,
    [FromQuery] string? title,
    [FromQuery] bool toDropbox,
    IEmailService emailService,
    DropboxClient dropbox,
    IConfiguration cfg) =>
{
    if (string.IsNullOrWhiteSpace(fileName))
        return Results.BadRequest(new { error = "fileName is required." });

    if (string.IsNullOrWhiteSpace(target) || (target != "dad" && target != "mom"))
        return Results.BadRequest(new { error = "Invalid target. Must be 'dad' or 'mom'." });

    var safeFileName = Path.GetFileName(fileName);
    if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Invalid fileName." });

    if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Reader supports EPUB files only." });

    var libraryRoot = ResolveLibraryRoot();
    var fullPath = Path.Combine(libraryRoot, safeFileName);
    if (!File.Exists(fullPath))
        return Results.NotFound(new { error = "File not found." });

    var kindleEmail = target == "dad"
        ? cfg["Email:DadsKindleEmail"] ?? throw new InvalidOperationException("Email:DadsKindleEmail not configured")
        : cfg["Email:MomsKindleEmail"] ?? throw new InvalidOperationException("Email:MomsKindleEmail not configured");

    if (toDropbox)
    {
        var dropboxPath = $"/KindleSync/{safeFileName}";
        await using var fileStream = File.OpenRead(fullPath);
        await dropbox.Files.UploadAsync(
            dropboxPath,
            WriteMode.Overwrite.Instance,
            body: fileStream);
    }
    else
    {
        var subject = "Book from Library";
        var body = $"Sent from Library: {title ?? safeFileName}";
        await emailService.SendEmailWithAttachmentAsync(kindleEmail, subject, body, fullPath, safeFileName);
    }

    return Results.Ok(new { success = true, message = toDropbox ? "Sent to Dropbox." : "Sent to Kindle." });
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3f) Update library book metadata ────────────────────────────────────
app.MapPatch("/api/library/book/{fileName}/metadata", async (
    [FromRoute] string fileName,
    [FromBody] LibraryBookMetadataUpdate update,
    HttpContext context) =>
{
    var safeFileName = Path.GetFileName(fileName);
    if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Invalid fileName." });

    var libraryRoot = ResolveLibraryRoot();
    var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

    if (!File.Exists(metaPath))
        return Results.NotFound(new { error = "Metadata file not found." });

    try
    {
        var jsonOptions = CreateLibraryJsonOptions();
        var json = await File.ReadAllTextAsync(metaPath);
        var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

        if (meta == null)
            return Results.BadRequest(new { error = "Invalid metadata file." });

        // Update fields
        meta.PrimaryGenre = update.PrimaryGenre;
        meta.Tags = update.Tags ?? Array.Empty<string>();
        meta.Series = update.Series;
        if (!string.IsNullOrWhiteSpace(update.Title))
            meta.Title = update.Title;
        if (update.Authors != null)
            meta.Authors = update.Authors;

        // Save back to file
        var updatedJson = JsonSerializer.Serialize(meta, jsonOptions);
        await File.WriteAllTextAsync(metaPath, updatedJson);

        Console.WriteLine($"[library] Updated metadata for {safeFileName}: Genre={meta.PrimaryGenre}, Tags={string.Join(", ", meta.Tags)}, Series={meta.Series}");

        return Results.Ok(new { success = true, message = "Metadata updated successfully." });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[library] Failed to update metadata for {safeFileName}: {ex.Message}");
        return Results.Problem("Failed to update metadata.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3f-1) Update library book ratings ───────────────────────────────────
app.MapPatch("/api/library/book/{fileName}/ratings", async (
    [FromRoute] string fileName,
    [FromBody] LibraryBookRatingsUpdate update) =>
{
    var safeFileName = Path.GetFileName(fileName);
    if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Invalid fileName." });

    var libraryRoot = ResolveLibraryRoot();
    var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

    if (!File.Exists(metaPath))
        return Results.NotFound(new { error = "Metadata file not found." });

    try
    {
        var jsonOptions = CreateLibraryJsonOptions();
        var json = await File.ReadAllTextAsync(metaPath);
        var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

        if (meta == null)
            return Results.BadRequest(new { error = "Invalid metadata file." });

        if (update.GoodreadsRating.HasValue)
        {
            var gr = Math.Clamp(update.GoodreadsRating.Value, 0, 5);
            meta.GoodreadsRating = gr;
        }

        if (update.PersonalRating.HasValue)
        {
            var pr = Math.Clamp(update.PersonalRating.Value, 0, 5);
            meta.PersonalRating = pr;
        }

        var updatedJson = JsonSerializer.Serialize(meta, jsonOptions);
        await File.WriteAllTextAsync(metaPath, updatedJson);

        Console.WriteLine($"[library] Updated ratings for {safeFileName}: Goodreads={meta.GoodreadsRating}, Personal={meta.PersonalRating}");

        return Results.Ok(new { success = true, message = "Ratings updated successfully." });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[library] Failed to update ratings for {safeFileName}: {ex.Message}");
        return Results.Problem("Failed to update ratings.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3f-1) Toggle library reader inclusion ──────────────────────────────
app.MapPost("/api/library/book/{fileName}/reader", async (
    [FromRoute] string fileName,
    [FromBody] LibraryBookReaderUpdate update) =>
{
    var safeFileName = Path.GetFileName(fileName);
    if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Invalid fileName." });

    var libraryRoot = ResolveLibraryRoot();
    var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

    if (!File.Exists(metaPath))
        return Results.NotFound(new { error = "Metadata file not found." });

    try
    {
        var jsonOptions = CreateLibraryJsonOptions();
        var json = await File.ReadAllTextAsync(metaPath);
        var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

        if (meta == null)
            return Results.BadRequest(new { error = "Invalid metadata file." });

        var enabled = update?.Enabled ?? true;
        var updated = meta with { ReaderEnabled = enabled };
        var updatedJson = JsonSerializer.Serialize(updated, jsonOptions);
        await File.WriteAllTextAsync(metaPath, updatedJson);

        return Results.Ok(new { success = true, enabled });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[library] Failed to update reader flag for {safeFileName}: {ex.Message}");
        return Results.Problem("Failed to update reader flag.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapPost("/api/library/book/reader", async (
    [FromQuery] string fileName,
    [FromBody] LibraryBookReaderUpdate update) =>
{
    var safeFileName = Path.GetFileName(fileName);
    if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(fileName, safeFileName, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Invalid fileName." });

    if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Reader supports EPUB files only." });

    var libraryRoot = ResolveLibraryRoot();
    var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

    if (!File.Exists(metaPath))
        return Results.NotFound(new { error = "Metadata file not found." });

    try
    {
        var jsonOptions = CreateLibraryJsonOptions();
        var json = await File.ReadAllTextAsync(metaPath);
        var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

        if (meta == null)
            return Results.BadRequest(new { error = "Invalid metadata file." });

        var enabled = update?.Enabled ?? true;
        var updated = meta with { ReaderEnabled = enabled };
        var updatedJson = JsonSerializer.Serialize(updated, jsonOptions);
        await File.WriteAllTextAsync(metaPath, updatedJson);

        return Results.Ok(new { success = true, enabled });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[library] Failed to update reader flag for {safeFileName}: {ex.Message}");
        return Results.Problem("Failed to update reader flag.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3f-2) Wipe all library genres ───────────────────────────────────────
app.MapPost("/api/library/books/genres/wipe", async () =>
{
    var libraryRoot = ResolveLibraryRoot();
    if (!Directory.Exists(libraryRoot))
        return Results.Ok(new { success = true, updated = 0 });

    var metaFiles = Directory.GetFiles(libraryRoot, "*.meta.json");
    var jsonOptions = CreateLibraryJsonOptions();
    var updatedCount = 0;

    foreach (var metaPath in metaFiles)
    {
        try
        {
            var json = await File.ReadAllTextAsync(metaPath);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);
            if (meta == null)
                continue;

            var updated = meta with
            {
                PrimaryGenre = null,
                Tags = Array.Empty<string>(),
                Genres = Array.Empty<string>()
            };

            var updatedJson = JsonSerializer.Serialize(updated, jsonOptions);
            await File.WriteAllTextAsync(metaPath, updatedJson);
            updatedCount++;
        }
        catch
        {
            // ignore individual file failures
        }
    }

    return Results.Ok(new { success = true, updated = updatedCount });
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3g) Update library book cover ───────────────────────────────────────
app.MapPost("/api/library/book/{fileName}/cover", async (
    [FromRoute] string fileName,
    [FromBody] LibraryBookCoverUpdate update,
    HttpContext context,
    IHttpClientFactory httpFactory) =>
{
    if (update == null || string.IsNullOrWhiteSpace(update.CoverUrl))
        return Results.BadRequest(new { error = "coverUrl is required." });

    if (!Uri.TryCreate(update.CoverUrl, UriKind.Absolute, out var coverUri) ||
        (coverUri.Scheme != Uri.UriSchemeHttp && coverUri.Scheme != Uri.UriSchemeHttps))
        return Results.BadRequest(new { error = "coverUrl must be an http(s) URL." });

    var safeFileName = Path.GetFileName(fileName);
    if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Invalid fileName." });

    var libraryRoot = ResolveLibraryRoot();
    var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");

    if (!File.Exists(metaPath))
        return Results.NotFound(new { error = "Metadata file not found." });

    byte[] coverBytes;
    try
    {
        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(6);
        using var request = new HttpRequestMessage(HttpMethod.Get, coverUri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        request.Headers.Referrer = new Uri(coverUri.GetLeftPart(UriPartial.Authority));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        coverBytes = await response.Content.ReadAsByteArrayAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[library] Failed to download cover: {ex.Message}");
        return Results.Problem("Failed to download cover image.");
    }

    if (!TryGetImageSize(coverBytes, out var width, out var height))
        return Results.BadRequest(new { error = "Unsupported cover image format." });

    if (!IsCoverSizeValid(width, height))
    {
        return Results.BadRequest(new
        {
            error = $"Cover image must be at least {MinCoverWidth}x{MinCoverHeight} pixels and 1:{TargetCoverRatio:0.##} (width:height) ratio."
        });
    }

    var coverExt = DetermineImageExtension(coverUri.ToString(), coverBytes);
    var coverDir = Path.Combine(libraryRoot, "_covers");
    Directory.CreateDirectory(coverDir);

    foreach (var existing in Directory.GetFiles(coverDir, $"{safeFileName}.cover.*"))
    {
        try { File.Delete(existing); } catch { /* ignore */ }
    }

    var coverFileName = $"{safeFileName}.cover{coverExt}";
    var coverDiskPath = Path.Combine(coverDir, coverFileName);
    await File.WriteAllBytesAsync(coverDiskPath, coverBytes);

    try
    {
        var jsonOptions = CreateLibraryJsonOptions();
        var json = await File.ReadAllTextAsync(metaPath);
        var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);

        if (meta == null)
            return Results.BadRequest(new { error = "Invalid metadata file." });

        var updated = meta with { CoverUrl = $"_covers/{coverFileName}" };
        var updatedJson = JsonSerializer.Serialize(updated, jsonOptions);
        await File.WriteAllTextAsync(metaPath, updatedJson);

        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
        var normalized = NormalizeLibraryCoverUrl(updated.CoverUrl, baseUrl);

        return Results.Ok(new { success = true, coverUrl = normalized });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[library] Failed to update cover metadata for {safeFileName}: {ex.Message}");
        return Results.Problem("Failed to update cover metadata.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3f-4) Library reader list ──────────────────────────────────────────
app.MapGet("/api/library/reader/books", (HttpContext context) =>
{
    var libraryRoot = ResolveLibraryRoot();
    if (!Directory.Exists(libraryRoot))
        return Results.Json(Array.Empty<ReaderBookDto>());

    var metaFiles = Directory.GetFiles(libraryRoot, "*.meta.json");
    var jsonOptions = CreateLibraryJsonOptions();
    var results = new List<ReaderBookDto>();
    var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

    var existingKeys = AiContentCache.GetExistingSummaryKeys();

    foreach (var metaFile in metaFiles)
    {
        try
        {
            var json = File.ReadAllText(metaFile);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);
            if (meta == null)
                continue;

            var ext = Path.GetExtension(meta.FileName);
            if (!string.Equals(ext, ".epub", StringComparison.OrdinalIgnoreCase))
                continue;

            var readerKey = ResolveReaderKey(meta.FileName, existingKeys);
            var hasSummaries = AiContentCache.HasAnySummaries(readerKey, existingKeys);
            var include = meta.ReaderEnabled == true || hasSummaries;
            if (!include)
                continue;

            var coverUrl = NormalizeLibraryCoverUrl(meta.CoverUrl, baseUrl)
                ?? FindLocalCoverUrl(libraryRoot, meta.FileName, baseUrl);

            results.Add(new ReaderBookDto(
                meta.FileName,
                readerKey,
                meta.Title ?? Path.GetFileNameWithoutExtension(meta.FileName),
                meta.Authors ?? Array.Empty<string>(),
                meta.Format ?? Path.GetExtension(meta.FileName).TrimStart('.').ToUpperInvariant(),
                coverUrl,
                hasSummaries
            ));
        }
        catch
        {
            // ignore malformed meta files
        }
    }

    return Results.Json(results.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList());
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5b-alt) Library EPUB reader endpoints ───────────────────────────────
app.MapGet("/api/library/reader/epub/chapters", async (
    [FromQuery] string fileName,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    CancellationToken cancellationToken,
    HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(fileName))
        return Results.BadRequest(new { error = "fileName is required." });

    var libraryRoot = ResolveLibraryRoot();
    var safeFileName = Path.GetFileName(fileName);
    if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Reader supports EPUB files only." });
    var fullPath = Path.Combine(libraryRoot, safeFileName);
    if (!File.Exists(fullPath))
        return Results.NotFound(new { error = "Book file not found." });

    var readerKey = ResolveReaderKey(safeFileName, AiContentCache.GetExistingSummaryKeys());
    var (index, cacheDir) = await LibraryEpubCache.GetOrBuildChapterIndexQuickAsync(fullPath, readerKey);
    index = await ChapterLabelingHelper.EnsureGptChapterLabelsAsync(
        index,
        cacheDir,
        httpFactory,
        cfg,
        modelHelper,
        aiResponseParser,
        cancellationToken);
    var response = new DropboxEpubChaptersResponse(
        index.Title,
        index.Chapters.Select(ch => new DropboxChapterDto(
            ch.Id,
            ch.Title,
            ch.Level,
            ch.WordCount,
            ch.DisplayLabel,
            ch.IsMainChapter)).ToList());
    return Results.Ok(response);
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapGet("/api/library/reader/epub/chapter", async (
    [FromQuery] string fileName,
    [FromQuery] int chapterId,
    HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(fileName))
        return Results.BadRequest(new { error = "fileName is required." });

    var libraryRoot = ResolveLibraryRoot();
    var safeFileName = Path.GetFileName(fileName);
    if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Reader supports EPUB files only." });
    var fullPath = Path.Combine(libraryRoot, safeFileName);
    if (!File.Exists(fullPath))
        return Results.NotFound(new { error = "Book file not found." });

    var readerKey = ResolveReaderKey(safeFileName, AiContentCache.GetExistingSummaryKeys());
    var (index, cacheDir) = await LibraryEpubCache.GetOrBuildChapterIndexQuickAsync(fullPath, readerKey);
    var chapter = index.Chapters.FirstOrDefault(ch => ch.Id == chapterId);
    if (chapter == null)
        return Results.NotFound(new { error = "Chapter not found." });

    var contentPath = Path.Combine(cacheDir, chapter.FileName);
    if (!File.Exists(contentPath))
    {
        _ = LibraryEpubCache.EnsureCacheBuildAsync(fullPath, readerKey, cacheDir);
        var fallback = await LibraryEpubCache.ReadChapterContentCachedAsync(fullPath, chapterId);
        if (fallback == null)
            return Results.NotFound(new { error = "Chapter content not ready yet." });

        try
        {
            Directory.CreateDirectory(cacheDir);
            await File.WriteAllTextAsync(contentPath, fallback);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[library] Failed to persist chapter cache for {safeFileName} chapter {chapterId}: {ex.Message}");
        }

        var fallbackResponse = new DropboxChapterContentDto(
            chapter.Id,
            chapter.Title,
            fallback,
            chapter.CharacterCount,
            chapter.WordCount);
        return Results.Ok(fallbackResponse);
    }

    var content = await File.ReadAllTextAsync(contentPath);
    var response = new DropboxChapterContentDto(chapter.Id, chapter.Title, content, chapter.CharacterCount, chapter.WordCount);
    return Results.Ok(response);
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapGet("/api/library/reader/epub/status", async (
    [FromQuery] string fileName) =>
{
    if (string.IsNullOrWhiteSpace(fileName))
        return Results.BadRequest(new { error = "fileName is required." });

    var libraryRoot = ResolveLibraryRoot();
    var safeFileName = Path.GetFileName(fileName);
    if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Reader supports EPUB files only." });
    var fullPath = Path.Combine(libraryRoot, safeFileName);
    if (!File.Exists(fullPath))
        return Results.NotFound(new { error = "Book file not found." });

    var readerKey = ResolveReaderKey(safeFileName, AiContentCache.GetExistingSummaryKeys());
    var status = await LibraryEpubCache.GetCacheStatusAsync(fullPath, readerKey);
    return Results.Ok(status);
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapPost("/api/library/reader/epub/index", async (
    [FromBody] LibraryReaderIndexRequest request) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.FileName))
        return Results.BadRequest(new { error = "fileName is required." });

    var fileName = Path.GetFileName(request.FileName);
    if (!string.Equals(Path.GetExtension(fileName), ".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Reader supports EPUB files only." });
    var libraryRoot = ResolveLibraryRoot();
    var fullPath = Path.Combine(libraryRoot, fileName);
    if (!File.Exists(fullPath))
        return Results.NotFound(new { error = "Book file not found." });

    var readerKey = ResolveReaderKey(fileName, AiContentCache.GetExistingSummaryKeys());
    var (_, cacheDir) = await LibraryEpubCache.GetOrBuildChapterIndexAsync(fullPath, readerKey);
    await LibraryEpubCache.EnsureCacheBuildAsync(fullPath, readerKey, cacheDir);
    return Results.Ok(new { started = true });
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapDelete("/api/library/reader/epub/index", ([FromBody] LibraryReaderIndexRequest request) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.FileName))
        return Results.BadRequest(new { error = "fileName is required." });

    var fileName = Path.GetFileName(request.FileName);
    if (!string.Equals(Path.GetExtension(fileName), ".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Reader supports EPUB files only." });
    var readerKey = ResolveReaderKey(fileName, AiContentCache.GetExistingSummaryKeys());
    var removed = LibraryEpubCache.DeleteCache(readerKey);
    return Results.Ok(new { success = removed });
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapGet("/api/library/reader/epub/search", async (
    [FromQuery] string fileName,
    [FromQuery] string? query,
    [FromQuery] string? q) =>
{
    if (string.IsNullOrWhiteSpace(fileName))
        return Results.BadRequest(new { error = "fileName is required." });

    var libraryRoot = ResolveLibraryRoot();
    var safeFileName = Path.GetFileName(fileName);
    if (!string.Equals(Path.GetExtension(safeFileName), ".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Reader supports EPUB files only." });
    var fullPath = Path.Combine(libraryRoot, safeFileName);
    if (!File.Exists(fullPath))
        return Results.NotFound(new { error = "Book file not found." });

    var normalizedQuery = (query ?? q)?.Trim();
    if (string.IsNullOrWhiteSpace(normalizedQuery) || normalizedQuery.Length < 10)
        return Results.BadRequest(new { error = "Search query must be at least 10 characters." });

    var readerKey = ResolveReaderKey(safeFileName, AiContentCache.GetExistingSummaryKeys());
    var results = await LibraryEpubCache.SearchAsync(fullPath, readerKey, normalizedQuery);
    return Results.Ok(results);
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3h) Delete library book ─────────────────────────────────────────────
app.MapDelete("/api/library/book/{fileName}", async (
    [FromRoute] string fileName) =>
{
    var safeFileName = Path.GetFileName(fileName);
    if (!string.Equals(fileName, safeFileName, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Invalid fileName." });

    var libraryRoot = ResolveLibraryRoot();
    var bookPath = Path.Combine(libraryRoot, safeFileName);
    var metaPath = Path.Combine(libraryRoot, safeFileName + ".meta.json");
    var coverDir = Path.Combine(libraryRoot, "_covers");
    var coverMatches = Directory.Exists(coverDir)
        ? Directory.GetFiles(coverDir, $"{safeFileName}.cover.*")
        : Array.Empty<string>();

    if (!File.Exists(bookPath) && !File.Exists(metaPath) && coverMatches.Length == 0)
        return Results.NotFound(new { error = "Book not found." });

    try
    {
        if (File.Exists(bookPath))
            File.Delete(bookPath);

        if (File.Exists(metaPath))
            File.Delete(metaPath);

        foreach (var cover in coverMatches)
        {
            try { File.Delete(cover); } catch { /* ignore */ }
        }

        return Results.Ok(new { success = true });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[library] Failed to delete book {safeFileName}: {ex.Message}");
        return Results.Problem("Failed to delete book.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 4) send-to-boox (via Dropbox) ──────────────────────────────────────
app.MapPost("/api/anna/book/{md5}/send-to-boox", async (
    HttpContext context,
    [FromRoute] string md5,
    [FromQuery] string? title,
    [FromQuery] string? coverUrl,
    IValidationService validation,
    AnnaArchiveService anna,
    IEbookCoverService coverService,
    DropboxClient dropbox,
    IConfiguration cfg,
    IDownloadTrackingService downloadTracking) =>
{
    if (!validation.IsValidMd5(md5))
        return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

    if (!validation.IsValidTitle(title))
        return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

    // Use shared helper to download book from Anna's Archive
    var (resp, fileName, acctInfo, errorMessage) = await DownloadBookFromAnnaArchiveAsync(md5, title, anna, memberKey);

    if (errorMessage != null)
    {
        // Return current tracking status on error
        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
        return Results.Ok(new { success = false, message = errorMessage, accountFastInfo = trackingInfo });
    }

    if (resp == null || fileName == null)
    {
        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
        return Results.Ok(new { success = false, message = "Failed to download book.", accountFastInfo = trackingInfo });
    }

    using (resp)
    {
        var uploadPath = $"{cfg["Dropbox:UploadFolderPath"]}/{fileName}";
        Stream ebookStream = await resp.Content.ReadAsStreamAsync();

        // Attempt cover replacement if coverUrl is provided and format is supported
        if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            var ext = Path.GetExtension(fileName).TrimStart('.');
            if (coverService.IsFormatSupported(ext))
            {
                Console.WriteLine($"[send-to-boox] Attempting cover replacement for {fileName}");
                ebookStream = await coverService.ReplaceCoverAsync(ebookStream, coverUrl, ext);
            }
            else
            {
                Console.WriteLine($"[send-to-boox] Format {ext} not supported for cover replacement, skipping");
            }
        }

        using var stream = ebookStream;

    try
    {
        Console.WriteLine($"Uploading '{fileName}' to Dropbox: {uploadPath}");

        var uploaded = await dropbox.Files.UploadAsync(
            uploadPath,
            WriteMode.Overwrite.Instance,
            body: stream
        );

        Console.WriteLine($"✅ Dropbox upload successful! File: {uploaded.PathDisplay}");

        // Get user name from auth context
        var userName = context.User?.FindFirst(ClaimTypes.Email)?.Value
            ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
            ?? "unknown";

        // Record successful download in our tracking system
        downloadTracking.RecordDownload(md5, userName);
        Console.WriteLine($"[send-to-boox] Recorded download for user {userName}, MD5: {md5}");

        // Get updated download tracking status
        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        var counterInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);

        return Results.Ok(new
        {
            success         = true,
            dropboxPath     = uploaded.PathDisplay,
            dropboxFileId   = uploaded.Id,
            accountFastInfo = counterInfo
        });
    }
    catch (ApiException<UploadError> ex)
    {
        var details = ex.ErrorResponse?.ToString() ?? ex.ToString();
        Console.WriteLine($"❌ Dropbox upload failed: {ex.Message}{(string.IsNullOrWhiteSpace(details) ? "" : $" | Details: {details}")}");

        return Results.Ok(new
        {
            success         = false,
            message         = "Failed to upload file to Dropbox. Please try again.",
            accountFastInfo = acctInfo
        });
    }
    catch (HttpException ex)
    {
        var details = ex.ToString();
        Console.WriteLine($"❌ Dropbox upload failed (HTTP {ex.StatusCode}): {ex.Message} | Uri: {ex.RequestUri} | Details: {details}");
        return Results.Ok(new
        {
            success         = false,
            message         = "Failed to upload file to Dropbox. Please try again.",
            accountFastInfo = acctInfo
        });
    }
    catch (DropboxException ex)
    {
        Console.WriteLine($"❌ Dropbox upload failed (DropboxException): {ex}");
        return Results.Ok(new
        {
            success         = false,
            message         = "Failed to upload file to Dropbox. Please try again.",
            accountFastInfo = acctInfo
        });
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"❌ Dropbox upload failed (HTTP): {ex}");
        return Results.Ok(new
        {
            success         = false,
            message         = "Failed to upload file to Dropbox. Please try again.",
            accountFastInfo = acctInfo
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Dropbox upload failed: {ex.Message}");

        return Results.Ok(new
        {
            success         = false,
            message         = "Failed to upload file to Dropbox. Please try again.",
            accountFastInfo = acctInfo
        });
    }
    } // end using (resp)
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5b) Dropbox EPUB reader endpoints ─────────────────────────────────
app.MapGet("/api/anna/dropbox/epubs", async (
    DropboxClient dropbox,
    IConfiguration cfg) =>
{
    try
    {
        var folderPath = cfg["Dropbox:UploadFolderPath"] ?? string.Empty;
        var epubs = await DropboxEpubCache.ListDropboxEpubsAsync(dropbox, folderPath);
        return Results.Ok(epubs);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to list Dropbox EPUBs: {ex.Message}");
        return ApiResponse.InternalError("Unable to list Dropbox files right now.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapGet("/api/anna/dropbox/epub/chapters", async (
    [FromQuery] string path,
    IValidationService validation,
    DropboxClient dropbox,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    CancellationToken cancellationToken) =>
{
    if (!validation.IsValidDropboxPath(path))
        return Results.BadRequest(new {
            error = "Invalid Dropbox path. Must start with '/', end with '.epub', and be less than 500 characters."
        });

    try
    {
        var (index, cacheDir) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, path);
        index = await ChapterLabelingHelper.EnsureGptChapterLabelsAsync(
            index,
            cacheDir,
            httpFactory,
            cfg,
            modelHelper,
            aiResponseParser,
            cancellationToken);

        var response = new DropboxEpubChaptersResponse(
            index.Title,
            index.Chapters
                .Where(ch => ch.WordCount >= 50)
                .Select(ch => new DropboxChapterDto(
                    ch.Id,
                    ch.Title,
                    ch.Level,
                    ch.WordCount,
                    ch.DisplayLabel,
                    ch.IsMainChapter))
                .ToList()
        );

        return Results.Ok(response);
    }
    catch (ApiException<DownloadError> ex)
    {
        Console.WriteLine($"❌ Dropbox download failed: {ex.ErrorResponse}");
        return ApiResponse.InternalError("Unable to download EPUB from Dropbox.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to load EPUB: {ex.Message}");
        return ApiResponse.InternalError("Unable to read the EPUB file.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapGet("/api/anna/dropbox/epub/chapter", async (
    [FromQuery] string path,
    [FromQuery] int chapterId,
    IValidationService validation,
    DropboxClient dropbox) =>
{
    if (!validation.IsValidDropboxPath(path))
        return Results.BadRequest(new {
            error = "Invalid Dropbox path. Must start with '/', end with '.epub', and be less than 500 characters."
        });

    if (!validation.IsValidChapterId(chapterId))
        return Results.BadRequest(new { error = "Chapter ID must be between 0 and 9999." });

    try
    {
        var (index, cacheDir) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, path);
        var chapter = index.Chapters.FirstOrDefault(c => c.Id == chapterId);

        if (chapter is null || string.IsNullOrWhiteSpace(chapter.FileName))
            return ApiResponse.NotFound("Chapter not found.");

        var chapterPath = Path.Combine(cacheDir, chapter.FileName);
        if (!File.Exists(chapterPath))
        {
            await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, path, cacheDir);
        }

        var contentText = await File.ReadAllTextAsync(chapterPath);

        var content = new DropboxChapterContentDto(
            chapter.Id,
            chapter.Title,
            contentText,
            contentText.Length,
            chapter.WordCount);

        return Results.Ok(content);
    }
    catch (ApiException<DownloadError> ex)
    {
        Console.WriteLine($"❌ Dropbox download failed: {ex.ErrorResponse}");
        return ApiResponse.InternalError("Unable to download EPUB from Dropbox.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to load EPUB: {ex.Message}");
        return ApiResponse.InternalError("Unable to read the EPUB file.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapGet("/api/anna/dropbox/epub/status", async (
    [FromQuery] string path,
    DropboxClient dropbox) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only .epub files are supported." });

    try
    {
        var status = await DropboxEpubCache.GetCacheStatusAsync(dropbox, path);
        return Results.Ok(status);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to read cache status: {ex.Message}");
        return ApiResponse.InternalError("Unable to fetch cache status.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapPost("/api/anna/dropbox/epub/index", async (
    [FromQuery] string path,
    DropboxClient dropbox) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only .epub files are supported." });

    try
    {
        var (_, cacheDir) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, path);
        // fire and forget rebuild to ensure freshness
        await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, path, cacheDir);
        return Results.Ok(new { started = true });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to start indexing: {ex.Message}");
        return ApiResponse.InternalError("Unable to start indexing for this book.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapDelete("/api/anna/dropbox/epub/index", (
    [FromQuery] string path) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only .epub files are supported." });

    try
    {
        // Delete EPUB cache (chapter content)
        var epubRemoved = DropboxEpubCache.DeleteCache(path);

        // Delete AI cache (summaries, vocab, chunk boundaries, character graph)
        var aiRemoved = AiContentCache.DeleteAllAiCacheForBook(path);

        Console.WriteLine($"🗑️ Cache deletion: EPUB={epubRemoved}, AI={aiRemoved}");
        return Results.Ok(new { epubCacheRemoved = epubRemoved, aiCacheRemoved = aiRemoved });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to delete cache: {ex.Message}");
        return ApiResponse.InternalError("Unable to delete cache for this book.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5e) Summarize via OpenAI ────────────────────────────────────────────
app.MapPost("/api/ai/summarize", async (
    [FromBody] SummarizeRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection,
    IValidationService validation,
    ITextProcessingService textProcessing) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Text))
        return Results.BadRequest(new { error = "Text is required." });

    if (!validation.IsValidTextLength(request.Text))
        return Results.BadRequest(new { error = "Text too long. Maximum 1,000,000 characters." });

    var tokenLimitResult = CheckTokenLimit(cfg, tokenUsage);
    if (tokenLimitResult is not null) return tokenLimitResult;

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelFast();

        string? previousAnalyses = null;
        string? cacheDirForSummary = null;

        if (!string.IsNullOrWhiteSpace(request.DropboxPath))
        {
            cacheDirForSummary = Path.Combine(DropboxEpubCache.GetCacheRoot(), DropboxEpubCache.ComputeHashPublic(request.DropboxPath));
            Directory.CreateDirectory(cacheDirForSummary);

            if (request.ChapterId.HasValue)
            {
                // Load ALL previous analyses for this chapter (sorted chronologically by word offset)
                var existingFiles = Directory.EnumerateFiles(cacheDirForSummary, $"summary-{request.ChapterId.Value}-*.txt")
                    .Select(f => new
                    {
                        Path = f,
                        Offset = textProcessing.ExtractWordOffset(Path.GetFileNameWithoutExtension(f))
                    })
                    .Where(x => x.Offset < (request.WordOffset ?? int.MaxValue)) // Only include analyses from earlier in the chapter
                    .OrderBy(x => x.Offset)
                    .ToList();

                if (existingFiles.Any())
                {
                    var analyses = new List<string>();
                    foreach (var file in existingFiles)
                    {
                        var content = await File.ReadAllTextAsync(file.Path);
                        if (!string.IsNullOrWhiteSpace(content))
                            analyses.Add(content);
                    }

                    if (analyses.Count > 0)
                        previousAnalyses = string.Join("\n\n---\n\n", analyses);
                }
            }
        }

        var contextParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.BookTitle))
            contextParts.Add($"Title: {request.BookTitle}");
        if (!string.IsNullOrWhiteSpace(request.Author))
            contextParts.Add($"Author: {request.Author}");
        if (request.Year.HasValue)
            contextParts.Add($"Year: {request.Year.Value}");
        if (!string.IsNullOrWhiteSpace(request.Premise))
            contextParts.Add($"Premise: {request.Premise}");

        var contextBlock = contextParts.Count > 0
            ? $"Book context -> {string.Join(" | ", contextParts)}"
            : "Book context -> (not provided)";

        // Build the system prompt with known words exclusion
        var systemPromptBase = @"You are an advanced literary analysis assistant with deep knowledge of philosophy, critical theory, and cultural studies. Provide a rich, thoughtful analysis (max 200 words) that goes beyond surface-level reading:

**Analysis should include:**
- What's happening narratively and conceptually
- Philosophical undertones and implicit arguments the author is making
- Literary techniques and their rhetorical effect
- How this passage connects to broader themes in the work
- Academic interpretations and critical perspectives (if applicable)
- Cultural, historical, or political context that enriches understanding
- Connections to other philosophical or literary traditions

Then add a 'Definitions:' section. BE EXTREMELY THOROUGH with definitions - include ALL words/phrases a typical high school student might not know: archaic terms, foreign words/phrases, technical jargon, sophisticated vocabulary, philosophical concepts, brand names, historical items, British/European terms, proper nouns needing context, academic terminology. Err on the side of over-defining.";

        string systemPrompt;
        if (request.KnownWords != null && request.KnownWords.Count > 0)
        {
            var knownWordsList = string.Join(", ", request.KnownWords);
            systemPrompt = $"{systemPromptBase}\n\nIMPORTANT: The user already knows these words, so DO NOT define them: {knownWordsList}. Total response can be up to 600 words.";
        }
        else
        {
            systemPrompt = $"{systemPromptBase}\n\nTotal response can be up to 600 words.";
        }

        var userPrompt = textProcessing.BuildAnalysisPrompt(contextBlock, previousAnalyses, request.Text);
        var fullInput = $"{systemPrompt}\n\n{userPrompt}";

        var payload = new
        {
            model = model,
            input = fullInput,
            reasoning = new { effort = cfg.GetValue<string>("AI:ReasoningEffort:Vocabulary") },
            max_output_tokens = cfg.GetValue<int>("AI:MaxCompletionTokens:Vocabulary"),
            temperature = cfg.GetValue<double>("AI:Temperature:Vocabulary")
        };

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI summarize failed status={(int)response.StatusCode} body={body}");
            return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        var summary = aiResponseParser.ExtractText(doc.RootElement);

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("input_tokens").GetInt32();
            var completionTokens = usage.GetProperty("output_tokens").GetInt32();
            tokenUsage.AddUsage(promptTokens, completionTokens);
        }

        if (cacheDirForSummary != null && request.ChapterId.HasValue)
        {
            var offsetLabel = request.WordOffset?.ToString() ?? DateTime.UtcNow.Ticks.ToString();
            var fileName = $"summary-{request.ChapterId.Value}-{offsetLabel}.txt";
            var savePath = Path.Combine(cacheDirForSummary, fileName);
            try
            {
                await File.WriteAllTextAsync(savePath, summary ?? string.Empty);
            }
            catch { /* ignore */ }
        }

        return Results.Ok(new SummarizeResponse(summary ?? "No summary returned."));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ OpenAI summarize failed: {ex.Message}");
        return ApiResponse.InternalError("Failed to summarize text.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5f) Learn more about a vocab term ──────────────────────────
app.MapPost("/api/ai/vocab/learn-more", async (
    [FromBody] LearnMoreRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Term))
        return Results.BadRequest(new { error = "Term is required." });

    var tokenLimitResult = CheckTokenLimit(cfg, tokenUsage);
    if (tokenLimitResult is not null) return tokenLimitResult;

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelDeep();

        var contextParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.BookTitle))
            contextParts.Add($"Book: {request.BookTitle}");
        if (!string.IsNullOrWhiteSpace(request.DropboxPath))
            contextParts.Add($"Source path: {request.DropboxPath}");

        var prompt = $@"Provide a rich, scholarly 300-400 word deep dive on the term/phrase ""{request.Term}"" that goes beyond dictionary definitions.

Respond as concise HTML with paragraphs, <ul>, <strong>, and include up to 2-3 reliable image URLs and 1-2 reference links (e.g., Wikipedia) that help explain the term.

**Your analysis should explore:**
- Core meaning and etymology
- Historical development and evolution of the concept
- How this term/concept is understood in different academic disciplines (philosophy, literature, sociology, etc.)
- Key thinkers, works, or movements associated with it
- How it appears in popular culture vs. academic discourse
- Common misconceptions or debates surrounding the term
- Relevance to contemporary discussions or current events (if applicable)
- Interesting facts or notable usage examples

IMAGE RULES (strict):
- Prefer upload.wikimedia.org or commons.wikimedia.org images; use fully-qualified HTTPS URLs with underscores instead of spaces.
- Do NOT include images unless you are confident the URL exists and is directly fetchable (ending in .jpg/.png/.jpeg).
- If unsure about an image URL, skip images entirely.

Structure:
- Rich overview paragraph (2-3 sentences)
- Bullet list covering the points above
- A ""Resources"" section with authoritative hyperlinks (plain <a href=""..."">text</a>)
- After the text, include a line ""Images:"" followed by <img src=""..."" alt=""..."" loading=""lazy"" /> for each image (absolute URLs only). Use images that are likely to be stable (e.g., Wikimedia, Wikipedia, major news/edu sites). No base64.

Context: {string.Join(" | ", contextParts)}
Definition (if given): {request.Definition ?? "(none)"}
Relevant passage/context: {request.Context ?? "(none)"}";

        var systemInstructions = "You are a scholarly explainer with expertise in philosophy, critical theory, literature, history, and cultural studies. Provide nuanced, intellectually rich analysis that bridges academic and accessible discourse.";
        var fullInput = $"{systemInstructions}\n\n{prompt}";

        var payload = new
        {
            model,
            input = fullInput,
            reasoning = new { effort = cfg.GetValue<string>("AI:ReasoningEffort:WikiImages") },
            max_output_tokens = cfg.GetValue<int>("AI:MaxCompletionTokens:WikiImages"),
            temperature = cfg.GetValue<double>("AI:Temperature:WikiImages")
        };

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI learn-more failed status={(int)response.StatusCode} body={body}");
            return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var detail = aiResponseParser.ExtractText(doc.RootElement);

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("input_tokens").GetInt32();
            var completionTokens = usage.GetProperty("output_tokens").GetInt32();
            tokenUsage.AddUsage(promptTokens, completionTokens);
        }

        return Results.Ok(new LearnMoreResponse(detail ?? "No details returned."));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ OpenAI learn-more failed: {ex.Message}");
        return ApiResponse.InternalError("Failed to fetch details.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5g) Flashcards CRUD ─────────────────────────────────────────
app.MapGet("/api/ai/flashcards", ([FromQuery] string path, IFlashcardService flashcardService) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    var flashcards = flashcardService.LoadFlashcards(path);
    return Results.Ok(flashcards);
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapPost("/api/ai/flashcards", async (
    [FromBody] FlashcardRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    IFlashcardService flashcardService) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Term))
        return Results.BadRequest(new { error = "Term is required." });

    var shouldSave = request.SaveToLibrary ?? true;
    if (shouldSave && string.IsNullOrWhiteSpace(request.DropboxPath))
        return Results.BadRequest(new { error = "dropboxPath is required when saving flashcards." });

    var tokenLimitResult = CheckTokenLimit(cfg, tokenUsage);
    if (tokenLimitResult is not null) return tokenLimitResult;

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");

        // Truncate very long passages to avoid overwhelming the model
        var maxInputLength = cfg.GetValue<int>("AI:MaxInputLength");
        var inputText = request.Term.Length > maxInputLength
            ? request.Term.Substring(0, maxInputLength) + "..."
            : request.Term;

        var systemPrompt = @"You are a vocabulary flashcard generator. Your job is to extract INDIVIDUAL WORDS or SHORT PHRASES from text and create a separate flashcard for EACH ONE.

CRITICAL: Extract MULTIPLE individual terms from the passage. DO NOT create a single flashcard with the entire passage. Each flashcard should be for ONE specific word or short phrase.

Return ONLY valid JSON, no markdown or explanation.

JSON Structure (ARRAY of flashcards):
[
  { ""term"": ""audacity"", ""definition"": ""bold or rude behavior"", ""etymology"": ""Latin audax (bold)"", ""usageExamples"": [""She had the audacity to criticize."", ""His audacity was shocking.""], ""notes"": """" },
  { ""term"": ""rhizome"", ""definition"": ""(philosophy) a non-hierarchical network structure, as opposed to a tree-like hierarchy"", ""etymology"": ""Greek rhizoma (mass of roots)"", ""usageExamples"": [""Deleuze uses rhizome as a metaphor."", ""A rhizomatic structure has no center.""], ""notes"": ""Specific philosophical meaning by Deleuze & Guattari"" },
  ...
]

What to extract (BE VERY SELECTIVE):
- College-level or graduate-level vocabulary (words beyond typical high school reading)
- Foreign words/phrases used in the text
- Specialized academic, philosophical, or technical terms
- Subject-specific jargon that requires domain knowledge
- Neologisms or terms with specialized meaning in this work (e.g., philosophy terms that are also common English words but have specific meaning here)
- Archaic or literary words rarely used in modern English
- Historical/cultural references requiring background knowledge

DO NOT extract:
- Common words that high school students would know (e.g., ""said"", ""walked"", ""important"", ""although"", ""necessary"")
- Basic academic words taught in high school (e.g., ""analyze"", ""demonstrate"", ""significant"")
- Simple vocabulary regardless of context

BE STRICT: Only select words that would genuinely challenge someone with a high school education or require specific domain knowledge.

Rules:
- Extract 3-10 individual terms from the passage (fewer is better than including common words)
- Each term should be a SINGLE WORD or SHORT PHRASE (2-4 words max)
- Definitions: 1-2 sentences, clear and concise (include subject-specific meaning if applicable)
- Usage examples: 2 brief sentences showing the word in context
- Etymology: Short phrase (""Unknown"" if unclear)
- Notes: Include context if the word has a specific meaning in this discipline/work";

        var knownWordsContext = request.KnownWords != null && request.KnownWords.Count > 0
            ? $"\n\nEXCLUDE these words (user already knows them): {string.Join(", ", request.KnownWords)}"
            : "";

        // Add custom context instructions if provided (for intelligent selection handling)
        var customInstructions = !string.IsNullOrWhiteSpace(request.Context)
            ? $"\n\nSPECIAL INSTRUCTIONS:\n{request.Context}\n"
            : "";

        var userPrompt = $@"Extract vocabulary terms from this passage:

""{inputText}""

Context: {request.BookTitle ?? "Unknown book"}{knownWordsContext}{customInstructions}

Return JSON array of flashcards for individual terms found in the passage.";

        var model = "gpt-4o"; // Use GPT-4o for cost-effective vocab extraction
        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:LearnMore"),
            temperature: cfg.GetValue<double>("AI:Temperature:LearnMore")
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI flashcard failed status={(int)response.StatusCode} body={body}");
            return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var content = aiResponseParser.ExtractText(doc.RootElement) ?? "{}";

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            tokenUsage.AddUsage(promptTokens, completionTokens);
        }

        List<FlashcardItem> cardsParsed;
        try
        {
            // Try to clean the content first - remove markdown code blocks if present
            var cleanedContent = content.Trim();
            if (cleanedContent.StartsWith("```"))
            {
                var lines = cleanedContent.Split('\n');
                cleanedContent = string.Join('\n', lines.Skip(1).SkipLast(1));
            }

            // Try to extract JSON array from the content
            var jsonMatch = Regex.Match(cleanedContent, @"\[[\s\S]*\]");
            if (jsonMatch.Success)
            {
                cleanedContent = jsonMatch.Value;
            }

            cardsParsed = JsonSerializer.Deserialize<List<FlashcardItem>>(cleanedContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new Exception("Invalid flashcard JSON array");

            Console.WriteLine($"✅ Successfully parsed {cardsParsed.Count} flashcards from AI response");
        }
        catch (Exception parseEx)
        {
            Console.WriteLine($"⚠️ Failed to parse flashcards as array: {parseEx.Message}");
            Console.WriteLine($"   AI response: {content.Substring(0, Math.Min(200, content.Length))}...");

            try
            {
                // Try parsing as a single object
                var single = JsonSerializer.Deserialize<FlashcardItem>(content, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (single != null)
                {
                    cardsParsed = new List<FlashcardItem> { single };
                    Console.WriteLine($"✅ Parsed single flashcard");
                }
                else
                    throw new Exception("Invalid flashcard JSON");
            }
            catch (Exception singleEx)
            {
                Console.WriteLine($"❌ Failed to parse flashcards: {singleEx.Message}");
                // Don't create a fallback card - return empty list
                // This prevents creating giant vocab cards with entire text
                cardsParsed = new List<FlashcardItem>();
                Console.WriteLine($"⚠️ Returning empty flashcard list due to parsing failure");
            }
        }

        if (shouldSave && !string.IsNullOrWhiteSpace(request.DropboxPath))
        {
            var list = flashcardService.LoadFlashcards(request.DropboxPath);
            foreach (var card in cardsParsed)
            {
                var existing = list.FindIndex(x => string.Equals(x.Term, card.Term, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0)
                    list[existing] = card;
                else
                    list.Add(card);
            }

            flashcardService.SaveFlashcards(request.DropboxPath, list);
        }
        return Results.Ok(new FlashcardResult(cardsParsed));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Flashcard create failed: {ex.Message}");
        return ApiResponse.InternalError("Failed to create flashcard.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapDelete("/api/ai/flashcards", ([FromQuery] string path, IFlashcardService flashcardService) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    try
    {
        var (_, filePath) = flashcardService.GetFlashcardPath(path);
        if (System.IO.File.Exists(filePath))
            System.IO.File.Delete(filePath);
        return Results.Ok(new { cleared = true });
    }
    catch
    {
        return ApiResponse.InternalError("Failed to clear flashcards.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5h) Wikipedia images helper (via REST summary) ─────────────────
app.MapGet("/api/media/wiki-images", async ([FromQuery] string term) =>
{
    if (string.IsNullOrWhiteSpace(term))
        return Results.BadRequest(new { error = "Query parameter 'term' is required." });

    try
    {
        var title = term.Replace(' ', '_');
        var summaryUrl = $"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(title)}";

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AnnasArchive/1.0 (+https://fs01pfbooks.synology.me)");

        var images = new List<string>();
        var summaryJson = await http.GetStringAsync(summaryUrl);
        using (var doc = JsonDocument.Parse(summaryJson))
        {
            void TryAdd(string? url)
            {
                if (string.IsNullOrWhiteSpace(url)) return;
                var normalized = url.Replace(" ", "_");
                if (!normalized.StartsWith("https://upload.wikimedia.org", StringComparison.OrdinalIgnoreCase)) return;
                if (!(normalized.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                      normalized.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                      normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                    return;
                if (!images.Contains(normalized)) images.Add(normalized);
            }

            if (doc.RootElement.TryGetProperty("originalimage", out var original) &&
                original.TryGetProperty("source", out var source))
            {
                TryAdd(source.GetString());
            }

            if (doc.RootElement.TryGetProperty("thumbnail", out var thumb) &&
                thumb.TryGetProperty("source", out var tsource))
            {
                TryAdd(tsource.GetString());
            }
        }

        // Fallback: use pageimages API for more options if needed
        if (images.Count < 3)
        {
            var queryUrl = $"https://en.wikipedia.org/w/api.php?action=query&format=json&prop=pageimages&piprop=original|thumbnail&pithumbsize=640&titles={Uri.EscapeDataString(title)}";
            var queryJson = await http.GetStringAsync(queryUrl);
            using var doc2 = JsonDocument.Parse(queryJson);
            if (doc2.RootElement.TryGetProperty("query", out var q) &&
                q.TryGetProperty("pages", out var pages) &&
                pages.EnumerateObject().Any())
            {
                foreach (var page in pages.EnumerateObject())
                {
                    var val = page.Value;
                    if (val.TryGetProperty("original", out var orig) && orig.TryGetProperty("source", out var osrc))
                    {
                        var url = osrc.GetString();
                        if (!string.IsNullOrWhiteSpace(url) && images.Count < 3)
                        {
                            var normalized = url!.Replace(" ", "_");
                            if (normalized.StartsWith("https://upload.wikimedia.org", StringComparison.OrdinalIgnoreCase) &&
                                (normalized.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                 normalized.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                 normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                            {
                                if (!images.Contains(normalized))
                                    images.Add(normalized);
                            }
                        }
                    }

                    if (val.TryGetProperty("thumbnail", out var thumb) && thumb.TryGetProperty("source", out var tsrc))
                    {
                        var url = tsrc.GetString();
                        if (!string.IsNullOrWhiteSpace(url) && images.Count < 3)
                        {
                            var normalized = url!.Replace(" ", "_");
                            if (normalized.StartsWith("https://upload.wikimedia.org", StringComparison.OrdinalIgnoreCase) &&
                                (normalized.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                 normalized.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                 normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                            {
                                if (!images.Contains(normalized))
                                    images.Add(normalized);
                            }
                        }
                    }
                }
            }
        }

        return Results.Ok(new WikiImagesResponse(images));
    }
    catch
    {
        Console.WriteLine($"⚠️ Wiki images lookup failed for term '{term}'");
        return Results.Ok(new WikiImagesResponse(new List<string>()));
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

static DateTime? ComputeResetDateUtc(IConfiguration cfg)
{
    var resetDay = cfg.GetValue<int?>("OpenAI:AllowanceResetDay") ?? 1;
    if (resetDay < 1 || resetDay > 28) resetDay = 1;
    var now = DateTime.UtcNow;
    var thisMonth = new DateTime(now.Year, now.Month, resetDay, 0, 0, 0, DateTimeKind.Utc);
    var nextReset = now <= thisMonth ? thisMonth : thisMonth.AddMonths(1);
    return nextReset;
}

app.MapGet("/api/anna/dropbox/epub/search", async (
    [FromQuery] string path,
    [FromQuery] string query,
    DropboxClient dropbox) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only .epub files are supported." });

    if (string.IsNullOrWhiteSpace(query) || query.Length < 10)
        return Results.BadRequest(new { error = "Search query must be at least 10 characters." });

    try
    {
        var matches = await DropboxEpubCache.SearchAsync(dropbox, path, query);
        return Results.Ok(matches);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to search EPUB cache: {ex.Message}");
        return ApiResponse.InternalError("Unable to search this book right now.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5i) Full-chapter summary with SSE progress ────────────────────────────────
app.MapPost("/api/ai/summarize/chapter/stream", async (
    HttpContext context,
    [FromBody] FullChapterSummaryRequest request,
    DropboxClient dropbox,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection,
    ITextProcessingService textProcessing) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.DropboxPath))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = "dropboxPath is required." });
        return;
    }
    if (request.ChapterId < 0)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = "chapterId must be zero or positive." });
        return;
    }

    if (request.ForceRegenerate)
    {
        AiContentCache.DeleteChapterSummary(request.DropboxPath, request.ChapterId);
    }

    // Check if cached summary exists
    var cached = AiContentCache.LoadChapterSummary<Dictionary<string, object>>(request.DropboxPath, request.ChapterId);
    if (cached != null)
    {
        Console.WriteLine($"📦 Returning cached chapter summary for {request.DropboxPath} chapter {request.ChapterId}");
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.Append("Cache-Control", "no-cache");
        context.Response.Headers.Append("Connection", "keep-alive");

        static long ToLong(object? value)
        {
            if (value == null) return 0L;
            if (value is long l) return l;
            if (value is int i) return i;
            if (value is double d) return (long)d;
            if (value is string s && long.TryParse(s, out var parsed)) return parsed;
            if (value is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Number && je.TryGetInt64(out var num)) return num;
                if (je.ValueKind == JsonValueKind.String && long.TryParse(je.GetString(), out var numFromString)) return numFromString;
            }
            return 0L;
        }

        static DateTime ToDateTime(object? value)
        {
            if (value == null) return DateTime.UtcNow;
            if (value is DateTime dt) return dt;
            if (value is string s && DateTime.TryParse(s, out var parsed)) return parsed;
            if (value is JsonElement je && je.ValueKind == JsonValueKind.String && DateTime.TryParse(je.GetString(), out var parsedJe)) return parsedJe;
            return DateTime.UtcNow;
        }

        var completeEvent = new
        {
            summary = cached.GetValueOrDefault("summary", ""),
            promptTokens = cached.TryGetValue("promptTokens", out var pt) ? ToLong(pt) : 0L,
            completionTokens = cached.TryGetValue("completionTokens", out var ct) ? ToLong(ct) : 0L,
            totalTokens = cached.TryGetValue("totalTokens", out var tt) ? ToLong(tt) : 0L,
            cachedAt = cached.TryGetValue("cachedAt", out var cachedAt) ? ToDateTime(cachedAt) : DateTime.UtcNow
        };

        await ServerSentEventsHelper.SendEventAsync(context.Response, completeEvent, "complete");
        return;
    }

    var chapterSummaryLockKey = $"chapter-summary:{request.DropboxPath}:{request.ChapterId}";
    if (!TryStartAiJob(chapterSummaryLockKey))
    {
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { error = "Chapter summary already in progress." });
        return;
    }

    try
    {
        // Check token limit
        var tokenLimitResult = CheckTokenLimit(cfg, tokenUsage);
        if (tokenLimitResult is not null)
        {
            await tokenLimitResult.ExecuteAsync(context);
            return;
        }

        var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new { error = "OpenAI API key not configured." });
            return;
        }

        // Set up SSE headers
        context.Response.Headers.Append("Content-Type", "text/event-stream");
        context.Response.Headers.Append("Cache-Control", "no-cache");
        context.Response.Headers.Append("Connection", "keep-alive");

        // Load chapter content using helper
        var content = await LoadChapterContentAsync(dropbox, request.DropboxPath, request.ChapterId);
        if (content is null)
        {
            await ServerSentEventsHelper.SendEventAsync(context.Response, new { message = "Chapter not found or empty." }, "error");
            return;
        }

        // Prepare context for AI
        var index = await LoadChapterIndexAsync(dropbox, request.DropboxPath);
        var chapter = index?.Chapters.FirstOrDefault(c => c.Id == request.ChapterId);

        var contextParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.BookTitle))
            contextParts.Add($"Book: {request.BookTitle}");

        var chapterTitle = !string.IsNullOrWhiteSpace(chapter?.Title)
            ? $"Chapter {request.ChapterId + 1}: {chapter.Title}"
            : $"Chapter {request.ChapterId + 1}";
        contextParts.Add(chapterTitle);
        var contextLine = string.Join(" | ", contextParts);

        // Split into chunks
        var chunkSize = cfg.GetValue<int>("AI:ChunkSize");
        var chunks = textProcessing.SplitIntoChunks(content, chunkSize);

        using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelDeep();

        // TIER 1: Summarize chunks using helper
        var (chunkSummaries, tier1PromptTokens, tier1CompletionTokens) =
            await SummarizeChunksAsync(http, model, chunks, contextLine, context.Response, cfg, aiResponseParser, tokenUsage);

        // TIER 2: Synthesize sections using helper
        var (sectionSummaries, tier2PromptTokens, tier2CompletionTokens) =
            await SynthesizeSectionsAsync(http, model, chunkSummaries, contextLine, context.Response, cfg, aiResponseParser, tokenUsage);

        // TIER 3: Create final summary using helper
        var (finalSummary, tier3PromptTokens, tier3CompletionTokens) =
            await CreateFinalSummaryAsync(http, model, sectionSummaries, contextParts, context.Response, cfg, aiResponseParser);

        // Calculate total tokens
        var promptTokensTotal = tier1PromptTokens + tier2PromptTokens + tier3PromptTokens;
        var completionTokensTotal = tier1CompletionTokens + tier2CompletionTokens + tier3CompletionTokens;

        tokenUsage.AddUsage(promptTokensTotal, completionTokensTotal);
        var totals = tokenUsage.GetTotals();
        var monthlyAllowance = cfg.GetValue<long?>("OpenAI:MonthlyTokenAllowance");
        double? percent = null;
        long? remaining = null;
        if (monthlyAllowance.HasValue && monthlyAllowance.Value > 0)
        {
            percent = Math.Round((double)totals.TotalTokens / monthlyAllowance.Value * 100, 2);
            remaining = monthlyAllowance.Value - totals.TotalTokens;
        }

        // Save summary to cache
        var summaryData = new
        {
            summary = finalSummary,
            promptTokens = promptTokensTotal,
            completionTokens = completionTokensTotal,
            totalTokens = promptTokensTotal + completionTokensTotal,
            allowanceUsedPercent = percent,
            tokensRemaining = remaining,
            cachedAt = DateTime.UtcNow
        };

        AiContentCache.SaveChapterSummary(request.DropboxPath, request.ChapterId, summaryData);

        // Send completion event with full summary
        await ServerSentEventsHelper.SendEventAsync(context.Response, summaryData, "complete");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Full-chapter summary failed: {ex.Message}");
        await ServerSentEventsHelper.SendEventAsync(context.Response, new { message = "Failed to summarize chapter.", error = ex.Message }, "error");
    }
    finally
    {
        EndAiJob(chapterSummaryLockKey);
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// Retrieve cached full-chapter summary (if any)
app.MapGet("/api/ai/summarize/chapter", (
    [FromQuery] string dropboxPath,
    [FromQuery] int chapterId) =>
{
    if (string.IsNullOrWhiteSpace(dropboxPath) || chapterId < 0)
        return Results.BadRequest(new { error = "dropboxPath and valid chapterId are required." });

    var cached = AiContentCache.LoadChapterSummary<Dictionary<string, object>>(dropboxPath, chapterId);
    if (cached == null)
        return ApiResponse.NotFound("No summary cached for this chapter.");

    return Results.Ok(cached);
})
.RequireAuthorization()
.RequireRateLimiting("api");

// Delete cached chapter summary
app.MapDelete("/api/ai/summarize/chapter", (
    [FromQuery] string dropboxPath,
    [FromQuery] int chapterId) =>
{
    if (string.IsNullOrWhiteSpace(dropboxPath) || chapterId < 0)
        return Results.BadRequest(new { error = "dropboxPath and valid chapterId are required." });

    AiContentCache.DeleteChapterSummary(dropboxPath, chapterId);
    return Results.Ok(new { message = "Cached summary deleted." });
})
.RequireAuthorization()
.RequireRateLimiting("api");

// Retrieve or generate an ultra "I'm a Dummy" chapter summary
app.MapPost("/api/ai/summarize/chapter/dummy", async (
    [FromBody] UltraChapterSummaryRequest request,
    DropboxClient dropbox,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.DropboxPath) || request.ChapterId < 0)
        return Results.BadRequest(new { error = "dropboxPath and valid chapterId are required." });

    if (request.ForceRegenerate)
    {
        AiContentCache.DeleteUltraChapterSummary(request.DropboxPath, request.ChapterId);
    }

    var cached = AiContentCache.LoadUltraChapterSummary<Dictionary<string, object>>(request.DropboxPath, request.ChapterId);
    if (cached != null)
        return Results.Ok(cached);

    var baseSummaryData = AiContentCache.LoadChapterSummary<Dictionary<string, object>>(request.DropboxPath, request.ChapterId);
    var baseSummaryText = baseSummaryData != null && baseSummaryData.TryGetValue("summary", out var summaryObj)
        ? summaryObj?.ToString()
        : null;

    if (string.IsNullOrWhiteSpace(baseSummaryText))
        return ApiResponse.NotFound("Full chapter summary is required before generating the dummy explanation.");

    var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Problem("OpenAI API key not configured.");

    var index = await LoadChapterIndexAsync(dropbox, request.DropboxPath);
    var chapterTitle = index?.Chapters.FirstOrDefault(c => c.Id == request.ChapterId)?.Title;

    var contextParts = new List<string>();
    if (!string.IsNullOrWhiteSpace(request.BookTitle))
        contextParts.Add($"Book: {request.BookTitle}");
    if (!string.IsNullOrWhiteSpace(chapterTitle))
        contextParts.Add($"Chapter: {chapterTitle}");
    var contextLine = contextParts.Count > 0 ? string.Join(" | ", contextParts) : "Chapter context";

    var systemPrompt = @"You are a friendly teacher who makes hard ideas feel obvious.
Write in a warm, conversational tone for a smart reader with zero background knowledge.
Use 3–5 short paragraphs. No headings, no bullet points, no numbered lists.";

    var userPrompt = $@"Explain this chapter in the clearest, most human way possible.
Focus on:
- why this matters
- what the author is really getting at
- why someone should care
- how it connects (or doesn't) to modern life

Be direct, vivid, and helpful without dumbing it down.

{contextLine}

Chapter summary:
{baseSummaryText}";

    using var http = httpFactory.CreateClient("OpenAI");
    var model = cfg["OpenAI:ModelUltra"]
        ?? Environment.GetEnvironmentVariable("OPENAI_MODEL_ULTRA")
        ?? modelSelection.GetModelDeep();

    var reasoningEffort = cfg.GetValue<string>("AI:ReasoningEffort:UltraSummary") ?? "high";
    var maxCompletion = cfg.GetValue<int?>("AI:MaxCompletionTokens:UltraChapterSummary")
        ?? cfg.GetValue<int?>("AI:MaxCompletionTokens:FullChapterSummary")
        ?? 1400;

    var payload = modelHelper.BuildChatCompletionPayload(
        model,
        new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt }
        },
        maxCompletionTokens: maxCompletion,
        temperature: null,
        reasoningEffort: reasoningEffort
    );

    var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
    if (!response.IsSuccessStatusCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"❌ OpenAI ultra summary failed: {response.StatusCode}");
        Console.WriteLine($"   Response body: {body}");
        return Results.Problem($"Ultra summary failed: {(int)response.StatusCode}");
    }

    using var stream = await response.Content.ReadAsStreamAsync();
    using var doc = await JsonDocument.ParseAsync(stream);
    var summary = aiResponseParser.ExtractText(doc.RootElement);
    if (string.IsNullOrWhiteSpace(summary))
        return Results.Problem("Ultra summary response was empty.");

    var promptTokens = 0;
    var completionTokens = 0;
    if (doc.RootElement.TryGetProperty("usage", out var usage))
    {
        promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
        completionTokens = usage.GetProperty("completion_tokens").GetInt32();
        tokenUsage.AddUsage(promptTokens, completionTokens);
    }

    var totals = tokenUsage.GetTotals();
    var monthlyAllowance = cfg.GetValue<long?>("OpenAI:MonthlyTokenAllowance");
    double? percent = null;
    long? remaining = null;
    if (monthlyAllowance.HasValue && monthlyAllowance.Value > 0)
    {
        percent = Math.Round((double)totals.TotalTokens / monthlyAllowance.Value * 100, 2);
        remaining = monthlyAllowance.Value - totals.TotalTokens;
    }

    var summaryData = new
    {
        summary = summary,
        promptTokens,
        completionTokens,
        totalTokens = promptTokens + completionTokens,
        allowanceUsedPercent = percent,
        tokensRemaining = remaining,
        cachedAt = DateTime.UtcNow
    };

    AiContentCache.SaveUltraChapterSummary(request.DropboxPath, request.ChapterId, summaryData);
    return Results.Ok(summaryData);
})
.RequireAuthorization()
.RequireRateLimiting("api");

// Retrieve cached ultra "I'm a Dummy" summary (if any)
app.MapGet("/api/ai/summarize/chapter/dummy", (
    [FromQuery] string dropboxPath,
    [FromQuery] int chapterId) =>
{
    if (string.IsNullOrWhiteSpace(dropboxPath) || chapterId < 0)
        return Results.BadRequest(new { error = "dropboxPath and valid chapterId are required." });

    var cached = AiContentCache.LoadUltraChapterSummary<Dictionary<string, object>>(dropboxPath, chapterId);
    if (cached == null)
        return ApiResponse.NotFound("No dummy summary cached for this chapter.");

    return Results.Ok(cached);
})
.RequireAuthorization()
.RequireRateLimiting("api");

// Get all cached summaries for a book
app.MapGet("/api/ai/summarize/book", (
    [FromQuery] string dropboxPath) =>
{
    if (string.IsNullOrWhiteSpace(dropboxPath))
        return Results.BadRequest(new { error = "dropboxPath is required." });

    var summaries = AiContentCache.LoadAllChapterSummaries(dropboxPath);
    return Results.Ok(summaries);
})
.RequireAuthorization()
.RequireRateLimiting("api");

// Token usage status
app.MapGet("/api/ai/usage", (IConfiguration cfg, ITokenUsageService tokenUsage) =>
{
    var totals = tokenUsage.GetTotals();
    var allowance = cfg.GetValue<long?>("OpenAI:MonthlyTokenAllowance");
    double? percent = null;
    long? remaining = null;
    if (allowance.HasValue && allowance.Value > 0)
    {
        percent = Math.Round((double)totals.TotalTokens / allowance.Value * 100, 2);
        remaining = allowance.Value - totals.TotalTokens;
    }

    var reset = ComputeResetDateUtc(cfg);
    var costUsd = tokenUsage.CalculateCostUsd(totals.PromptTokens, totals.CompletionTokens);

    var resp = new TokenUsageResponse(
        totals.PromptTokens,
        totals.CompletionTokens,
        totals.TotalTokens,
        allowance,
        percent,
        remaining,
        reset,
        costUsd);
    return Results.Ok(resp);
})
.RequireAuthorization()
.RequireRateLimiting("api");

// Reset token usage counter
app.MapPost("/api/ai/usage/reset", (ITokenUsageService tokenUsage) =>
{
    tokenUsage.Reset();
    Console.WriteLine("✅ Token usage counter has been reset");
    return Results.Ok(new { success = true, message = "Token usage counter has been reset" });
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5j) Chunk boundaries detection with SSE progress ────────────────────────
app.MapGet("/api/ai/chunk-boundaries", async (
    HttpContext context,
    [FromQuery] string dropboxPath,
    [FromQuery] int chapterId,
    DropboxClient dropbox,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser) =>
{
    if (string.IsNullOrWhiteSpace(dropboxPath) || chapterId < 0)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = "dropboxPath and valid chapterId are required." });
        return;
    }

    // Check cache first
    var cached = AiContentCache.LoadChunkBoundaries(dropboxPath, chapterId);
    if (cached != null)
    {
        Console.WriteLine($"✅ Returning cached chunk boundaries for chapter {chapterId}");
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(cached);
        return;
    }

    var chunkBoundaryLockKey = $"chunk-boundaries:{dropboxPath}:{chapterId}";
    if (!TryStartAiJob(chunkBoundaryLockKey))
    {
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { error = "Chunk boundary detection already in progress." });
        return;
    }

    try
    {
        // Not cached - detect boundaries with SSE progress
        Console.WriteLine($"🔍 Detecting chunk boundaries for chapter {chapterId}...");

    // Set up SSE
    context.Response.Headers["Content-Type"] = "text/event-stream";
    context.Response.Headers["Cache-Control"] = "no-cache";
    context.Response.Headers["Connection"] = "keep-alive";

    // Check token limit
    if (IsTokenLimitExceeded(cfg, tokenUsage))
    {
        await ServerSentEventsHelper.SendEventAsync(context.Response, new
        {
            stage = "error",
            stepNumber = 0,
            totalSteps = 1,
            message = "Monthly token allowance exceeded"
        });
        return;
    }

    // Load chapter content (index if needed)
    var existingKeys = AiContentCache.GetExistingSummaryKeys();
    var isLibrary = TryResolveLibraryFileForReaderKey(dropboxPath, existingKeys, out _, out var libraryPath);
    var cacheRoot = isLibrary ? LibraryEpubCache.GetCacheRoot() : DropboxEpubCache.GetCacheRoot();
    var epubHash = isLibrary
        ? LibraryEpubCache.ComputeHashPublic(dropboxPath)
        : DropboxEpubCache.ComputeHashPublic(dropboxPath);
    var chapterPath = Path.Combine(cacheRoot, epubHash, $"chapter-{chapterId:D4}.txt");

    if (!File.Exists(chapterPath))
    {
        // Chapter not indexed - index it now
        await ServerSentEventsHelper.SendEventAsync(context.Response, new
        {
            stage = "indexing",
            stepNumber = 0,
            totalSteps = 1,
            message = "Indexing book (first time only)..."
        });
        Console.WriteLine($"📑 Chapter {chapterId} not indexed - indexing entire book now...");

        try
        {
            var cacheDir = Path.Combine(cacheRoot, epubHash);
            if (isLibrary)
            {
                await LibraryEpubCache.EnsureCacheBuildAsync(libraryPath, dropboxPath, cacheDir);
            }
            else
            {
                await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, dropboxPath, cacheDir);
            }
            await ServerSentEventsHelper.SendEventAsync(context.Response, new
            {
                stage = "indexing",
                stepNumber = 1,
                totalSteps = 1,
                message = "Book indexed successfully"
            });
            Console.WriteLine($"✅ Book indexed successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to index book: {ex.Message}");
            await ServerSentEventsHelper.SendEventAsync(context.Response, new
            {
                stage = "error",
                stepNumber = 0,
                totalSteps = 1,
                message = $"Failed to index book: {ex.Message}"
            });
            return;
        }

        // Verify chapter file now exists
        if (!File.Exists(chapterPath))
        {
            await ServerSentEventsHelper.SendEventAsync(context.Response, new
            {
                stage = "error",
                stepNumber = 0,
                totalSteps = 1,
                message = "Chapter file not found after indexing"
            });
            return;
        }
    }

    var chapterText = await File.ReadAllTextAsync(chapterPath);
    var words = chapterText.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    var totalWords = words.Length;

    Console.WriteLine($"📖 Chapter has {totalWords} words");

    // Estimate total chunks
    var estimatedChunks = Math.Max(1, (int)Math.Ceiling(totalWords / 500.0));
    await ServerSentEventsHelper.SendEventAsync(context.Response, new
    {
        stage = "detecting",
        stepNumber = 0,
        totalSteps = estimatedChunks,
        message = $"Analyzing {totalWords:N0} words..."
    });

    // Use GPT-4o to detect chunk boundaries
    var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        await ServerSentEventsHelper.SendEventAsync(context.Response, new
        {
            stage = "error",
            stepNumber = 0,
            totalSteps = 1,
            message = "OpenAI API key not configured"
        });
        return;
    }

    var chunks = new List<ChunkBoundary>();
    var currentStart = 0;
    var targetChunkSize = 500;
    var maxChunkSize = 600;

    using var http = httpFactory.CreateClient("OpenAI");
        var model = "gpt-4o"; // Use GPT-4o for cost-effective chunking

        Console.WriteLine($"🤖 Using model for chunk detection: {model}");
        Console.WriteLine($"   Model info: {modelHelper.GetModelDescription(model)}");

        while (currentStart < totalWords)
        {
            var chunkIndex = chunks.Count + 1;
            await ServerSentEventsHelper.SendEventAsync(context.Response, new
            {
                stage = "detecting",
                stepNumber = chunkIndex,
                totalSteps = estimatedChunks,
                message = $"Detecting section {chunkIndex} of ~{estimatedChunks}..."
            });

            // Extract text window (target 500-600 words)
            var endWord = Math.Min(currentStart + maxChunkSize, totalWords);
            var windowWords = words.Skip(currentStart).Take(endWord - currentStart).ToArray();
            var windowText = string.Join(" ", windowWords);

            if (endWord >= totalWords)
            {
                // Last chunk - just add it
                chunks.Add(new ChunkBoundary(currentStart, totalWords, totalWords - currentStart));
                break;
            }

            // Ask GPT-4o to find the best break point
            var prompt = $@"You are analyzing a section of a book chapter to find the best place to split it into readable chunks.

The text below is approximately {windowWords.Length} words. I need to split this into a chunk of around 500 words (±100 words flexibility).

Your task:
1. Read through the text and identify natural breaking points (paragraph boundaries, topic shifts, scene breaks)
2. Find the best break point between word 400 and word 600 that:
   - Ends at a paragraph boundary (double newline)
   - Completes a thought or topic
   - Does NOT cut off mid-sentence or mid-paragraph
3. Return ONLY a JSON object with the word index where the break should occur

Text to analyze:
{windowText}

Return format (JSON only, no explanation):
{{
  ""breakWordIndex"": <number between 400 and {windowWords.Length}>
}}";

            // Safely read config values with fallbacks
            int maxTokens = 100; // Default for chunk boundary detection
            double temp = 0.3;   // Default temperature
            try
            {
                maxTokens = cfg.GetValue<int>("AI:MaxCompletionTokens:ChunkBoundary", 100);
                temp = cfg.GetValue<double>("AI:Temperature:ChunkBoundary", 0.3);
            }
            catch (Exception configEx)
            {
                Console.WriteLine($"⚠️ Config read error (using defaults): {configEx.Message}");
            }

            var payload = modelHelper.BuildChatCompletionPayload(
                model,
                new object[]
                {
                    new { role = "user", content = prompt }
                },
                maxCompletionTokens: maxTokens,
                temperature: temp
            );

            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ OpenAI chunk detection failed: {body}");
                await ServerSentEventsHelper.SendEventAsync(context.Response, new
                {
                    stage = "error",
                    stepNumber = chunkIndex,
                    totalSteps = estimatedChunks,
                    message = $"Detection failed: {(int)response.StatusCode}"
                });
                return;
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var aiText = aiResponseParser.ExtractText(doc.RootElement);

            // Track token usage
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
                var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
                tokenUsage.AddUsage(promptTokens, completionTokens);
            }

            // Parse the break point
            int breakPoint = targetChunkSize; // Default fallback
            if (!string.IsNullOrWhiteSpace(aiText))
            {
                Console.WriteLine($"🤖 AI response: {aiText}");
                try
                {
                    // Try to extract JSON from response (handle markdown code blocks)
                    var cleanedText = aiText.Trim();

                    // Remove markdown code blocks if present
                    if (cleanedText.StartsWith("```"))
                    {
                        var lines = cleanedText.Split('\n');
                        cleanedText = string.Join('\n', lines.Skip(1).SkipLast(1));
                    }

                    // Try direct JSON parse first
                    JsonDocument? jsonDoc = null;
                    try
                    {
                        jsonDoc = JsonDocument.Parse(cleanedText);
                    }
                    catch
                    {
                        // Fall back to regex extraction
                        var jsonMatch = Regex.Match(cleanedText, @"\{[^\}]*""breakWordIndex""[^\}]*\}");
                        if (jsonMatch.Success)
                        {
                            jsonDoc = JsonDocument.Parse(jsonMatch.Value);
                        }
                    }

                    if (jsonDoc != null && jsonDoc.RootElement.TryGetProperty("breakWordIndex", out var idx))
                    {
                        breakPoint = idx.GetInt32();
                        // Clamp to valid range
                        breakPoint = Math.Max(400, Math.Min(breakPoint, windowWords.Length));
                        Console.WriteLine($"✂️ AI suggested break at word {breakPoint}");
                    }
                    else
                    {
                        Console.WriteLine($"⚠️ No breakWordIndex found in response, using default: {breakPoint}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Failed to parse break point: {ex.Message}, using default: {breakPoint}");
                }
            }
            else
            {
                Console.WriteLine($"⚠️ Empty AI response, using default break point: {breakPoint}");
            }

            // If we're still at default (500), try to find a paragraph boundary as fallback
            if (breakPoint == targetChunkSize)
            {
                Console.WriteLine($"⚠️ Using fallback: finding nearest paragraph boundary around word {breakPoint}");

                // Look for paragraph breaks (double newlines) near the target position
                var searchStart = Math.Max(400, breakPoint - 50);
                var searchEnd = Math.Min(windowWords.Length, breakPoint + 50);

                // Reconstruct text to find paragraph boundaries
                var searchText = string.Join(" ", windowWords.Skip(searchStart).Take(searchEnd - searchStart));
                var paragraphs = searchText.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.None);

                if (paragraphs.Length > 1)
                {
                    // Find the paragraph break closest to target position
                    var currentPos = searchStart;
                    var bestBreak = breakPoint;
                    var bestDistance = int.MaxValue;

                    foreach (var para in paragraphs.Take(paragraphs.Length - 1)) // Don't include last paragraph
                    {
                        var paraWordCount = para.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
                        currentPos += paraWordCount;

                        var distance = Math.Abs(currentPos - breakPoint);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            bestBreak = currentPos;
                        }
                    }

                    if (bestBreak >= 400 && bestBreak <= windowWords.Length)
                    {
                        breakPoint = bestBreak;
                        Console.WriteLine($"✂️ Found paragraph boundary at word {breakPoint} (distance from target: {bestDistance})");
                    }
                }
            }

            var chunkEnd = currentStart + breakPoint;
            chunks.Add(new ChunkBoundary(currentStart, chunkEnd, chunkEnd - currentStart));
            currentStart = chunkEnd;

            Console.WriteLine($"✂️ Chunk detected: words {chunks[^1].Start}-{chunks[^1].End} ({chunks[^1].WordCount} words)");
        }

        // Save to cache
        AiContentCache.SaveChunkBoundaries(dropboxPath, chapterId, chunks);

        // Send completion event
        var result = new
        {
            chapterId,
            chunks,
            cachedAt = DateTime.UtcNow
        };
        await ServerSentEventsHelper.SendEventAsync(context.Response, result);

        Console.WriteLine($"✅ Detected {chunks.Count} sections for chapter {chapterId}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Chunk boundary detection failed: {ex.Message}");
        await ServerSentEventsHelper.SendEventAsync(context.Response, new
        {
            stage = "error",
            stepNumber = 0,
            totalSteps = 1,
            message = $"Detection failed: {ex.Message}"
        });
    }
    finally
    {
        EndAiJob(chunkBoundaryLockKey);
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5j) Get cached section summary (no generation) ─────────────────────────
app.MapGet("/api/ai/section-summary", ([FromQuery] string dropboxPath, [FromQuery] int chapterId, [FromQuery] int sectionIndex) =>
{
    if (string.IsNullOrWhiteSpace(dropboxPath))
        return Results.BadRequest(new { error = "dropboxPath is required." });
    if (chapterId < 0 || sectionIndex < 0)
        return Results.BadRequest(new { error = "chapterId and sectionIndex must be zero or positive." });

    var cached = AiContentCache.LoadSectionSummary(dropboxPath, chapterId, sectionIndex);
    if (cached != null)
    {
        // Load associated vocab if it exists
        var vocab = AiContentCache.LoadSectionVocab(dropboxPath, chapterId, sectionIndex);

        // Create new response with vocab included
        var response = cached with { Vocab = vocab };

        Console.WriteLine($"✅ Returning cached section summary for chapter {chapterId}, section {sectionIndex} (vocab: {vocab?.Count ?? 0} cards)");
        return Results.Ok(response);
    }

    return Results.NotFound(new { error = "No cached summary found for this section." });
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5k) Section summary generation using GPT-5.2 ────────────────────────────
app.MapPost("/api/ai/section-summary", async (
    [FromBody] SectionSummaryRequest request,
    DropboxClient dropbox,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.DropboxPath))
        return Results.BadRequest(new { error = "dropboxPath is required." });
    if (request.ChapterId < 0 || request.SectionIndex < 0)
        return Results.BadRequest(new { error = "chapterId and sectionIndex must be zero or positive." });

    // Check if summary already cached
    var cached = AiContentCache.LoadSectionSummary(request.DropboxPath, request.ChapterId, request.SectionIndex);
    if (cached != null)
    {
        Console.WriteLine($"✅ Returning cached section summary for chapter {request.ChapterId}, section {request.SectionIndex}");
        return Results.Ok(cached);
    }

    var sectionSummaryLockKey = $"section-summary:{request.DropboxPath}:{request.ChapterId}:{request.SectionIndex}";
    if (!TryStartAiJob(sectionSummaryLockKey))
    {
        return Results.Conflict(new { error = "Section summary already in progress." });
    }

    try
    {
        // Check token limit
        var tokenLimitResult = CheckTokenLimit(cfg, tokenUsage);
        if (tokenLimitResult is not null) return tokenLimitResult;

    // Load chunk boundaries
    var boundaries = AiContentCache.LoadChunkBoundaries(request.DropboxPath, request.ChapterId);
    if (boundaries == null || request.SectionIndex >= boundaries.Chunks.Count)
        return Results.BadRequest(new { error = "Invalid sectionIndex or chunk boundaries not detected." });

    // Load chapter content
    var existingKeys = AiContentCache.GetExistingSummaryKeys();
    var isLibrary = TryResolveLibraryFileForReaderKey(request.DropboxPath, existingKeys, out _, out var libraryPath);
    var cacheRoot = isLibrary ? LibraryEpubCache.GetCacheRoot() : DropboxEpubCache.GetCacheRoot();
    var epubHash = isLibrary
        ? LibraryEpubCache.ComputeHashPublic(request.DropboxPath)
        : DropboxEpubCache.ComputeHashPublic(request.DropboxPath);
    var chapterPath = Path.Combine(cacheRoot, epubHash, $"chapter-{request.ChapterId:D4}.txt");

    if (!File.Exists(chapterPath))
    {
        if (isLibrary)
        {
            await LibraryEpubCache.EnsureCacheBuildAsync(libraryPath, request.DropboxPath, Path.Combine(cacheRoot, epubHash));
        }
        else
        {
            await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, request.DropboxPath, Path.Combine(cacheRoot, epubHash));
        }
    }

    if (!File.Exists(chapterPath))
        return Results.NotFound(new { error = "Chapter not indexed." });

    var chapterText = await File.ReadAllTextAsync(chapterPath);
    var words = chapterText.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);

    var chunk = boundaries.Chunks[request.SectionIndex];
    var sectionWords = words.Skip(chunk.Start).Take(chunk.WordCount).ToArray();
    var sectionText = string.Join(" ", sectionWords);

    Console.WriteLine($"📝 Generating summary for chapter {request.ChapterId}, section {request.SectionIndex} ({chunk.WordCount} words)");

    // Use GPT-5.2 (deep model) for high-quality summaries
    var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        Console.WriteLine("❌ OpenAI API key not configured");
        return Results.Problem("OpenAI API key not configured.");
    }

    using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelDeep();

        Console.WriteLine($"🤖 Using model: {model}");
        Console.WriteLine($"   Model info: {modelHelper.GetModelDescription(model)}");

        var bookContext = !string.IsNullOrWhiteSpace(request.BookTitle)
            ? $" from the book \"{request.BookTitle}\""
            : "";

        // Build prompt for educational, explanatory summary
        var prompt = $@"You are an expert educator explaining this text section{bookContext} to someone who wants to deeply understand it.

Provide a comprehensive summary that:

1. **What Happens**: Summarize the key events, dialogue, and developments in this section

2. **Explain Concepts**: When you encounter complex ideas, philosophical terms, or specialized vocabulary:
   - Define and explain the concept in accessible language
   - Provide historical or cultural context
   - Explain WHY this concept matters and what problem it addresses
   - Connect abstract ideas to concrete examples

3. **Clarify References**: For any historical, literary, philosophical, or cultural references:
   - Identify who/what is being referenced
   - Explain the significance and context
   - Show how it relates to the current text

4. **Thematic Analysis**: Explain the deeper meaning and themes being explored

Your goal is to make this text comprehensible and meaningful. If the section discusses abstract theory, explain it in plain language. If it references obscure ideas, provide the background needed to understand them. Assume the reader is intelligent but may not be familiar with specialized academic or philosophical concepts.

Keep your summary thorough but focused (2-5 paragraphs depending on complexity).

Text to summarize:
{sectionText}";

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new object[]
            {
                new { role = "user", content = prompt }
            },
            maxCompletionTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:SectionSummary"),
            temperature: cfg.GetValue<double>("AI:Temperature:SectionSummary")
        );

        Console.WriteLine($"📤 Sending request to OpenAI Chat Completions API...");
        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI section summary failed: {response.StatusCode}");
            Console.WriteLine($"   Response body: {body}");
            return Results.Problem($"Section summary failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        var summary = aiResponseParser.ExtractText(doc.RootElement);
        Console.WriteLine($"✅ Summary generated: {summary?.Length ?? 0} characters");

        // Track token usage
        int promptTokens = 0, completionTokens = 0;
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            tokenUsage.AddUsage(promptTokens, completionTokens);
            Console.WriteLine($"📊 Token usage: {promptTokens} prompt + {completionTokens} completion = {promptTokens + completionTokens} total");
        }

        // Save to cache
        var result = new SectionSummaryResponse(
            summary ?? "No summary generated.",
            request.SectionIndex,
            promptTokens,
            completionTokens,
            promptTokens + completionTokens,
            DateTime.UtcNow
        );

        AiContentCache.SaveSectionSummary(request.DropboxPath, request.ChapterId, request.SectionIndex, result);
        Console.WriteLine($"💾 Section summary cached successfully");

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Section summary generation failed: {ex.Message}");
        Console.WriteLine($"   Stack trace: {ex.StackTrace}");
        return Results.Problem("Failed to generate section summary.");
    }
    finally
    {
        EndAiJob(sectionSummaryLockKey);
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5l) Save section vocabulary to cache ────────────────────────────────────
app.MapPost("/api/ai/section-vocab", ([FromBody] SaveSectionVocabRequest request) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.DropboxPath))
        return Results.BadRequest(new { error = "dropboxPath is required." });
    if (request.ChapterId < 0 || request.SectionIndex < 0)
        return Results.BadRequest(new { error = "chapterId and sectionIndex must be zero or positive." });
    if (request.Vocab == null)
        return Results.BadRequest(new { error = "vocab is required." });

    Console.WriteLine($"💾 Saving {request.Vocab.Count} vocab cards for chapter {request.ChapterId}, section {request.SectionIndex}");

    AiContentCache.SaveSectionVocab(request.DropboxPath, request.ChapterId, request.SectionIndex, request.Vocab);

    return Results.Ok(new { success = true, vocabCount = request.Vocab.Count });
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5m) Suggest authors for a book title ────────────────────────────────────
app.MapPost("/api/ai/suggest-authors", async (
    [FromBody] SuggestAuthorsRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.BookTitle))
        return Results.BadRequest(new { error = "BookTitle is required." });

    try
    {
        var openLibraryAuthors = await FetchAuthorsFromOpenLibraryAsync(request.BookTitle, httpFactory);
        if (openLibraryAuthors.Count > 0)
        {
            Console.WriteLine($"✅ Author suggestions (OpenLibrary) for '{request.BookTitle}': {openLibraryAuthors.Count} authors found");
            return Results.Ok(new SuggestAuthorsResponse(openLibraryAuthors));
        }

        var tokenLimitResult = CheckTokenLimit(cfg, tokenUsage);
        if (tokenLimitResult is not null) return tokenLimitResult;

        using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelFast();  // Uses gpt-4o by default

        var systemPrompt = @"You are a book metadata expert. Given a book title, suggest the 3-5 most likely authors sorted by probability. Return ONLY valid JSON with no markdown, explanation, or additional text.";

        var userPrompt = $@"Book title: ""{request.BookTitle}""

Return ONLY a JSON array of likely authors sorted by probability (most likely first). Each entry should have ""author"" (full name) and ""confidence"" (high/medium/low).

Example format:
[
  {{""author"": ""J.R.R. Tolkien"", ""confidence"": ""high""}},
  {{""author"": ""Christopher Tolkien"", ""confidence"": ""medium""}}
]

If the title is ambiguous or you don't recognize it, return an empty array: []

Do NOT include any markdown formatting, explanations, or text outside the JSON array.";

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: 500,
            temperature: 0.3
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI suggest-authors failed status={(int)response.StatusCode} body={body}");
            return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var rawText = aiResponseParser.ExtractText(doc.RootElement);

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            tokenUsage.AddUsage(promptTokens, completionTokens);
        }

        // Parse the JSON array of authors
        var authors = new List<AuthorSuggestion>();
        if (!string.IsNullOrWhiteSpace(rawText))
        {
            try
            {
                // Remove markdown code blocks if present
                var cleanedText = rawText.Trim();
                if (cleanedText.StartsWith("```"))
                {
                    cleanedText = cleanedText
                        .Replace("```json", "")
                        .Replace("```", "")
                        .Trim();
                }

                // If the model adds extra text, extract the JSON array.
                var arrayMatch = Regex.Match(cleanedText, @"\[[\s\S]*\]");
                var jsonPayload = arrayMatch.Success ? arrayMatch.Value : cleanedText;

                var authorsDoc = JsonDocument.Parse(jsonPayload);
                if (authorsDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in authorsDoc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("author", out var authorProp) &&
                            item.TryGetProperty("confidence", out var confidenceProp))
                        {
                            authors.Add(new AuthorSuggestion(
                                authorProp.GetString() ?? "",
                                confidenceProp.GetString() ?? "low"
                            ));
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"⚠️ Failed to parse author suggestions JSON: {ex.Message}");
                Console.WriteLine($"Raw text: {rawText}");
                // Return empty array on parse failure
            }
        }

        Console.WriteLine($"✅ Author suggestions for '{request.BookTitle}': {authors.Count} authors found");
        return Results.Ok(new SuggestAuthorsResponse(authors));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ OpenAI suggest-authors failed: {ex.Message}");
        return ApiResponse.InternalError("Failed to suggest authors.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5n) Find related books (series + other series by author) ────────────────
app.MapPost("/api/ai/related-books", async (
    [FromBody] RelatedBooksRequest request,
    AnnaArchiveService annaArchiveService,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.BookTitle) || string.IsNullOrWhiteSpace(request.Author))
        return Results.BadRequest(new { error = "BookTitle and Author are required." });

    var tokenLimitResult = CheckTokenLimit(cfg, tokenUsage);
    if (tokenLimitResult is not null) return tokenLimitResult;

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelFast();

        var systemPrompt = @"You are a literary expert with comprehensive knowledge of book series and author bibliographies. Given a book title and author, identify related books. Return ONLY valid JSON with no markdown or explanations.";

        var userPrompt = $@"Book: ""{request.BookTitle}"" by {request.Author}

Provide:
1. A summary of the current series (if this book is part of a series)
2. Other books in the SAME SERIES (if this book is part of a series)
3. OTHER SERIES by this author (different series they've written) with ALL books in each series

Return ONLY this JSON structure:
{{
  ""seriesSummary"": ""A 2-3 sentence overview of the current series, its themes, and significance. Null if not part of a series."",
  ""sameSeries"": [
    {{""title"": ""Book Title"", ""order"": 1, ""description"": ""Brief 1-line description""}}
  ],
  ""seriesName"": ""Series Name (optional)"",
  ""seriesSearchQuery"": ""Search query to find series books (optional)"",
  ""otherSeries"": [
    {{
      ""seriesName"": ""Series Name"",
      ""bookCount"": 3,
      ""books"": [
        {{""title"": ""Book 1 Title"", ""order"": 1, ""description"": ""Brief description""}}
      ],
      ""description"": ""Brief 1-line description of series"",
      ""summary"": ""2-3 sentence overview of this series""
    }}
  ]
}}

Rules:
- If the book is NOT part of a series, return null for seriesSummary
- If the series has MANY books, still return ALL known published titles (no ellipses)
- If you cannot list all titles, set seriesName and seriesSearchQuery for lookup
- For otherSeries, include ALL books in each series in the ""books"" array
- Only include PUBLISHED books (no unreleased/rumored books)
- Sort all books by publication/reading order
- For otherSeries, include 3-5 most notable series
- Each series summary should be 2-3 sentences covering themes, plot arc, and significance
- Keep individual book descriptions concise (max 15 words)
- Return ONLY the JSON object, no markdown formatting";

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: 3500,
            temperature: 0.3
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI related-books failed status={(int)response.StatusCode} body={body}");
            return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var rawText = aiResponseParser.ExtractText(doc.RootElement);

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            tokenUsage.AddUsage(promptTokens, completionTokens);
        }

        // Parse the JSON response
        var sameSeries = new List<SeriesBook>();
        var otherSeries = new List<AuthorSeries>();
        string? seriesName = null;
        string? seriesSearchQuery = null;
        string? seriesSummary = null;

        if (!string.IsNullOrWhiteSpace(rawText))
        {
            try
            {
                // Remove markdown code blocks if present
                var cleanedText = rawText.Trim();
                if (cleanedText.StartsWith("```"))
                {
                    cleanedText = cleanedText
                        .Replace("```json", "")
                        .Replace("```", "")
                        .Trim();
                }

                var relatedDoc = JsonDocument.Parse(cleanedText);

                // Parse seriesSummary
                if (relatedDoc.RootElement.TryGetProperty("seriesSummary", out var summaryProp) &&
                    summaryProp.ValueKind == JsonValueKind.String)
                {
                    seriesSummary = summaryProp.GetString();
                }

                // Parse sameSeries
                if (relatedDoc.RootElement.TryGetProperty("sameSeries", out var sameSeriesArray) &&
                    sameSeriesArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in sameSeriesArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("title", out var titleProp))
                        {
                            sameSeries.Add(new SeriesBook(
                                titleProp.GetString() ?? "",
                                item.TryGetProperty("order", out var orderProp) ? orderProp.GetInt32() : 0,
                                item.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "",
                                null  // CoverUrl will be populated later
                            ));
                        }
                    }
                }

                if (relatedDoc.RootElement.TryGetProperty("seriesName", out var seriesNameProp) &&
                    seriesNameProp.ValueKind == JsonValueKind.String)
                {
                    seriesName = seriesNameProp.GetString();
                }

                if (relatedDoc.RootElement.TryGetProperty("seriesSearchQuery", out var seriesSearchProp) &&
                    seriesSearchProp.ValueKind == JsonValueKind.String)
                {
                    seriesSearchQuery = seriesSearchProp.GetString();
                }

                // Parse otherSeries
                if (relatedDoc.RootElement.TryGetProperty("otherSeries", out var otherSeriesArray) &&
                    otherSeriesArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in otherSeriesArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("seriesName", out var nameProp))
                        {
                            // Parse books array for this series
                            var seriesBooks = new List<SeriesBook>();
                            if (item.TryGetProperty("books", out var booksArray) &&
                                booksArray.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var book in booksArray.EnumerateArray())
                                {
                                    if (book.TryGetProperty("title", out var bookTitleProp))
                                    {
                                        seriesBooks.Add(new SeriesBook(
                                            bookTitleProp.GetString() ?? "",
                                            book.TryGetProperty("order", out var bookOrderProp) ? bookOrderProp.GetInt32() : 0,
                                            book.TryGetProperty("description", out var bookDescProp) ? bookDescProp.GetString() ?? "" : "",
                                            null  // CoverUrl will be populated later
                                        ));
                                    }
                                }
                            }

                            otherSeries.Add(new AuthorSeries(
                                nameProp.GetString() ?? "",
                                item.TryGetProperty("bookCount", out var countProp) ? countProp.GetInt32() : seriesBooks.Count,
                                seriesBooks,
                                item.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "",
                                item.TryGetProperty("summary", out var seriesSummaryProp) ? seriesSummaryProp.GetString() ?? "" : ""
                            ));
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"⚠️ Failed to parse related books JSON: {ex.Message}");
                Console.WriteLine($"Raw text: {rawText}");
            }
        }

        if (sameSeries.Count < 15)
        {
            string Normalize(string value) =>
                Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

            var query = seriesSearchQuery ?? seriesName ?? $"{request.BookTitle} {request.Author}";
            try
            {
                var searchResults = await annaArchiveService.SearchAsync(query, 80, exact: false);
                var normalizedAuthor = Normalize(request.Author);
                var normalizedSeries = Normalize(seriesName ?? request.BookTitle);

                var matches = searchResults
                    .Where(b => b.Authors.Any(a => Normalize(a).Contains(normalizedAuthor)))
                    .Select(b => b.Title)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct()
                    .Where(t => Normalize(t!).Contains(normalizedSeries))
                    .Select((t, index) => new SeriesBook(t!, index + 1, "", null))
                    .ToList();

                if (matches.Count > sameSeries.Count)
                {
                    sameSeries = matches;
                    Console.WriteLine($"✅ Series expanded via search: {matches.Count} titles");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Series expansion failed: {ex.Message}");
            }
        }

        Console.WriteLine($"✅ Related books for '{request.BookTitle}': {sameSeries.Count} series books, {otherSeries.Count} other series");

        return Results.Ok(new RelatedBooksResponse(sameSeries, otherSeries, seriesSummary));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ OpenAI related-books failed: {ex.Message}");
        return ApiResponse.InternalError("Failed to get related books.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5n-2) AI book search (freeform query) ───────────────────────────────
app.MapPost("/api/ai/book-search", async (
    [FromBody] AiBookSearchRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection,
    CancellationToken cancellationToken) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Query))
        return Results.BadRequest(new { error = "query is required." });

    var tokenLimitResult = CheckTokenLimit(cfg, tokenUsage);
    if (tokenLimitResult is not null) return tokenLimitResult;

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelDeep();
        var hasUrl = request.Query.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || request.Query.Contains("https://", StringComparison.OrdinalIgnoreCase);
        var extractedTitles = hasUrl
            ? await ExtractBookTitlesFromQueryAsync(request.Query, httpFactory, cancellationToken)
            : new List<string>();
        var hasExtractedTitles = extractedTitles.Count > 0;
        var maxResults = hasExtractedTitles
            ? Math.Min(20, extractedTitles.Count)
            : 20;
        var perBookWordLimit = hasExtractedTitles && extractedTitles.Count >= 60 ? 24 : 45;

        var systemPrompt = @"You are a book discovery assistant. Determine whether the user query is asking for books.
If it is, return a list of relevant books with an engaging, spoiler-free summary of the search.
Return ONLY valid JSON with no markdown or extra text.";

        var extractedBlock = hasExtractedTitles
            ? $"ExtractedTitles (from the URL):\n- {string.Join("\n- ", extractedTitles.Take(100))}\n"
            : "ExtractedTitles: None\n";

        var userPrompt = $@"Query: ""{request.Query}""
{extractedBlock}

Return ONLY this JSON structure:
{{
  ""isBookQuery"": boolean,
  ""message"": string|null,
  ""summary"": string|null,
  ""books"": [
    {{
      ""title"": ""Book title"",
      ""author"": ""Author name"",
      ""summary"": ""Spoiler-free note on what makes this book special (2-3 sentences)"",
      ""importance"": ""Context/impact (historical, critical acclaim, cultural influence; 1 sentence)""
    }}
  ]
}}

Rules:
- If the query is NOT about books, set isBookQuery=false and return a brief message.
- If ExtractedTitles are provided, return those titles in that order and fill in author if known; do not invent titles not present.
- If ExtractedTitles are not provided, return up to {maxResults} books when the query includes a URL or asks for a list; otherwise return 10-25.
- Make the summary 2-3 sentences, spoiler-free, and engaging (max 80 words).
- The summary should briefly explain what the list represents and why it's notable (e.g., award significance, era, genre influence).
- Keep each book summary and importance concise (max {perBookWordLimit} words each).";

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: hasUrl ? 6000 : 2000,
            temperature: 0.3
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI book-search failed status={(int)response.StatusCode} body={body}");
            return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var rawText = aiResponseParser.ExtractText(doc.RootElement);

        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            tokenUsage.AddUsage(promptTokens, completionTokens);
        }

        if (string.IsNullOrWhiteSpace(rawText))
            return Results.Problem("AI search returned empty response.");

        var cleaned = rawText.Trim();
        if (cleaned.StartsWith("```"))
        {
            cleaned = cleaned
                .Replace("```json", "")
                .Replace("```", "")
                .Trim();
        }

        JsonDocument resultDoc;
        try
        {
            resultDoc = JsonDocument.Parse(cleaned);
        }
        catch (Exception ex)
        {
            var rawPreview = rawText.Length > 2000 ? rawText[..2000] + "…" : rawText;
            var cleanPreview = cleaned.Length > 2000 ? cleaned[..2000] + "…" : cleaned;
            Console.WriteLine($"❌ AI book-search JSON parse failed: {ex.Message}");
            Console.WriteLine($"❌ AI book-search raw preview: {rawPreview}");
            Console.WriteLine($"❌ AI book-search cleaned preview: {cleanPreview}");
            return Results.BadRequest(new { error = "AI response could not be parsed. Try again or simplify the query." });
        }

        var root = resultDoc.RootElement;

        var isBookQuery = root.TryGetProperty("isBookQuery", out var bookProp) && bookProp.ValueKind == JsonValueKind.True;
        if (!isBookQuery)
        {
            var message = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "Query is not about books.";
            return Results.BadRequest(new { error = message ?? "Query is not about books." });
        }

        var summary = root.TryGetProperty("summary", out var summaryProp) ? summaryProp.GetString() : null;
        var books = new List<AiBookSearchItem>();

        if (root.TryGetProperty("books", out var booksProp) && booksProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in booksProp.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var author = item.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";
                var bookSummary = item.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                var importance = item.TryGetProperty("importance", out var i) ? i.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(title)) continue;

                var coverUrl = await FetchOpenLibraryCoverAsync(title, author, httpFactory)
                               ?? await FetchGoogleBooksCoverAsync(title, author, httpFactory);

                books.Add(new AiBookSearchItem(title, author, bookSummary, importance, coverUrl));
            }
        }

        if (books.Count == 0 && !hasExtractedTitles)
        {
            var retryPrompt = $@"Query: ""{request.Query}""

Return ONLY this JSON structure:
{{
  ""isBookQuery"": true,
  ""message"": null,
  ""summary"": string|null,
  ""books"": [
    {{
      ""title"": ""Book title"",
      ""author"": ""Author name"",
      ""summary"": ""Spoiler-free note on what makes this book special (2-3 sentences)"",
      ""importance"": ""Context/impact (historical, critical acclaim, cultural influence; 1 sentence)""
    }}
  ]
}}

Rules:
- You MUST return 10-20 books. Do not return an empty list.
- Make the summary 2-3 sentences, spoiler-free, and engaging (max 80 words).
- Keep each book summary and importance concise (max {perBookWordLimit} words each).";

            var retryPayload = modelHelper.BuildChatCompletionPayload(
                "gpt-4o",
                new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = retryPrompt }
                },
                maxCompletionTokens: 2500,
                temperature: 0.4
            );

            var retryResponse = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", retryPayload, cancellationToken);
            if (retryResponse.IsSuccessStatusCode)
            {
                using var retryStream = await retryResponse.Content.ReadAsStreamAsync(cancellationToken);
                using var retryDoc = await JsonDocument.ParseAsync(retryStream, cancellationToken: cancellationToken);
                var retryText = aiResponseParser.ExtractText(retryDoc.RootElement);
                if (!string.IsNullOrWhiteSpace(retryText))
                {
                    var retryClean = retryText.Trim();
                    if (retryClean.StartsWith("```"))
                    {
                        retryClean = retryClean
                            .Replace("```json", "")
                            .Replace("```", "")
                            .Trim();
                    }

                    var retryResultDoc = JsonDocument.Parse(retryClean);
                    var retryRoot = retryResultDoc.RootElement;
                    summary = retryRoot.TryGetProperty("summary", out var retrySummary) ? retrySummary.GetString() : summary;

                    if (retryRoot.TryGetProperty("books", out var retryBooks) && retryBooks.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in retryBooks.EnumerateArray())
                        {
                            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                            var author = item.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";
                            var bookSummary = item.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                            var importance = item.TryGetProperty("importance", out var i) ? i.GetString() ?? "" : "";

                            if (string.IsNullOrWhiteSpace(title)) continue;

                            var coverUrl = await FetchOpenLibraryCoverAsync(title, author, httpFactory)
                                           ?? await FetchGoogleBooksCoverAsync(title, author, httpFactory);

                            books.Add(new AiBookSearchItem(title, author, bookSummary, importance, coverUrl));
                        }
                    }
                }
            }
        }

        return Results.Ok(new AiBookSearchResponse(summary, books));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ OpenAI book-search failed: {ex.Message}");
        return ApiResponse.InternalError("Failed to run AI book search.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5o) Match series books intelligently using GPT ─────────────────────────
app.MapPost("/api/ai/match-series-books", async (
    [FromBody] MatchSeriesBooksRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser,
    IModelSelectionService modelSelection) =>
{
    if (request is null || request.Books is null || request.Books.Count == 0)
        return Results.BadRequest(new { error = "Books list is required." });

    var tokenLimitResult = CheckTokenLimit(cfg, tokenUsage);
    if (tokenLimitResult is not null) return tokenLimitResult;

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");
        var model = modelSelection.GetModelFast();

        // Build a comprehensive prompt with all search results
        var booksJson = System.Text.Json.JsonSerializer.Serialize(request.Books, new JsonSerializerOptions { WriteIndented = true });

        var systemPrompt = @"You are an expert book matcher. You analyze search results from a library database and select the best match for each book in a series.

Your task: For each book, examine all search result candidates and select the BEST match based on:
1. Title match (handle variations like subtitles, series numbers in parentheses)
2. Author match (exact or close match)
3. Format match (if specified)
4. Detect and AVOID: Omnibus editions, anthologies, collections, combined volumes
5. Prefer standalone individual books over compilations

Return ONLY valid JSON with no markdown or explanation.";

        var userPrompt = $@"Series: ""{request.SeriesName ?? "Unknown Series"}""
Author: ""{request.Author}""
Preferred Format: ""{request.PreferredFormat ?? "ANY"}""

For each book below, I'm providing the title we're looking for and the search results. Select the BEST candidate or flag if no good match exists.

Books and Search Results:
{booksJson}

Return ONLY this JSON structure:
{{
  ""matches"": [
    {{
      ""bookTitle"": ""Book title we searched for"",
      ""order"": 1,
      ""status"": ""matched|ambiguous|not_found"",
      ""selectedMd5"": ""md5_of_best_match"",
      ""selectedTitle"": ""Full title from search results"",
      ""confidence"": ""exact|likely|uncertain"",
      ""reason"": ""Brief explanation (e.g., 'Exact title and author match', 'Anthology detected', etc.)""
    }}
  ]
}}

Rules:
- status: ""matched"" if you found a good match, ""ambiguous"" if multiple viable options, ""not_found"" if no good match
- confidence: ""exact"" for perfect matches, ""likely"" for close matches, ""uncertain"" if you're not sure
- ALWAYS avoid omnibus/anthology editions unless that's the ONLY option
- If a book has ""(Books 1-3)"" or ""Complete Series"" in the title, flag it as ambiguous or not_found
- Match format if specified (e.g., only select EPUB if format is EPUB)";

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: 2000,
            temperature: 0.2
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI match-series-books failed status={(int)response.StatusCode} body={body}");
            return Results.Problem($"OpenAI request failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var rawText = aiResponseParser.ExtractText(doc.RootElement);

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            tokenUsage.AddUsage(promptTokens, completionTokens);
        }

        // Parse the JSON response
        var matches = new List<SeriesBookMatch>();

        if (!string.IsNullOrWhiteSpace(rawText))
        {
            try
            {
                // Remove markdown code blocks if present
                var cleanedText = rawText.Trim();
                if (cleanedText.StartsWith("```"))
                {
                    cleanedText = cleanedText
                        .Replace("```json", "")
                        .Replace("```", "")
                        .Trim();
                }

                var matchDoc = JsonDocument.Parse(cleanedText);

                if (matchDoc.RootElement.TryGetProperty("matches", out var matchesArray) &&
                    matchesArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in matchesArray.EnumerateArray())
                    {
                        matches.Add(new SeriesBookMatch(
                            item.TryGetProperty("bookTitle", out var bt) ? bt.GetString() ?? "" : "",
                            item.TryGetProperty("order", out var ord) ? ord.GetInt32() : 0,
                            item.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                            item.TryGetProperty("selectedMd5", out var md5) ? md5.GetString() : null,
                            item.TryGetProperty("selectedTitle", out var title) ? title.GetString() : null,
                            item.TryGetProperty("confidence", out var conf) ? conf.GetString() ?? "" : "",
                            item.TryGetProperty("reason", out var rsn) ? rsn.GetString() ?? "" : ""
                        ));
                    }
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"⚠️ Failed to parse series match JSON: {ex.Message}");
                Console.WriteLine($"Raw text: {rawText}");
            }
        }

        Console.WriteLine($"✅ Matched {matches.Count(m => m.Status == "matched")} of {request.Books.Count} books");
        return Results.Ok(new MatchSeriesBooksResponse(matches));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ OpenAI match-series-books failed: {ex.Message}");
        return ApiResponse.InternalError("Failed to match series books.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── Character Graph Generation ─────────────────────────────────────────────

app.MapPost("/api/ai/characters/graph", async (
    [FromBody] CharacterGraphRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser) =>
{
    if (string.IsNullOrWhiteSpace(request.DropboxPath))
        return Results.BadRequest(new { error = "DropboxPath is required." });

    Console.WriteLine($"📊 Generating character graph for {request.BookTitle ?? request.DropboxPath}...");

    // Gather all existing summaries (both chapter and section) for this book
    var chapterSummaries = AiContentCache.GetAllChapterSummariesAsStrings(request.DropboxPath);
    var sectionSummaries = AiContentCache.GetAllSectionSummaries(request.DropboxPath);

    if (chapterSummaries.Count == 0 && sectionSummaries.Count == 0)
    {
        Console.WriteLine("⚠️ No summaries found. Generate some chapter or section summaries first.");
        return Results.BadRequest(new { error = "No summaries found. Please generate chapter or section summaries as you read the book first." });
    }

    Console.WriteLine($"📚 Found {chapterSummaries.Count} chapter summaries and {sectionSummaries.Count} section summaries to analyze");

    // Combine all summaries
    var allSummaries = new List<string>();
    allSummaries.AddRange(chapterSummaries);
    allSummaries.AddRange(sectionSummaries);
    var totalSummaryCount = allSummaries.Count;

    var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Problem("OpenAI API key not configured.");

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");

        // Build consolidated summary text
        var summaryText = string.Join("\n\n---\n\n", allSummaries.Select((s, i) =>
            $"Summary {i + 1}:\n{s}"));

        var systemPrompt = @"You are a character relationship analyzer for novels. Analyze the provided story summaries and create a network graph of character relationships.

IMPORTANT: Only include information that appears in the provided summaries. Do not add or infer information beyond what's explicitly mentioned.

Return ONLY valid JSON, no markdown, no code blocks.

JSON Structure:
{
  ""nodes"": [
    {
      ""id"": ""zhao"",
      ""label"": ""Adm. Zhao"",
      ""description"": ""Brief role (2-5 words)"",
      ""detailedDescription"": ""Detailed description of who they are, what they've done so far, their motivations and characteristics based ONLY on the summaries provided (2-3 sentences)""
    }
  ],
  ""edges"": [
    {
      ""from"": ""zhao"",
      ""to"": ""miller"",
      ""label"": ""relationship type (friend/enemy/spouse/etc.)"",
      ""detailedDescription"": ""Detailed description of their relationship and key interactions based ONLY on the summaries provided (1-2 sentences)""
    }
  ]
}

CRITICAL: The ""from"" and ""to"" fields in edges MUST use the simplified lowercase IDs, NOT the character labels.
Example: If a node has id=""zhao"" and label=""Adm. Zhao"", the edge must use ""zhao"", not ""Adm. Zhao"".

Rules:
- Include main and important secondary characters (5-15 characters max)
- Only include characters that appear in the provided summaries
- Character names MUST be properly capitalized (first letter of each word uppercase)
- If a character has a military/professional title (Admiral, Captain, Lieutenant, Sergeant, Doctor, etc.), include the abbreviated title before their name:
  * Admiral → Adm.
  * Captain → Capt.
  * Lieutenant → Lt.
  * Sergeant → Sgt.
  * Colonel → Col.
  * Doctor → Dr.
  * Professor → Prof.
  * Example: ""Adm. Zhao"", ""Capt. Miller"", ""Dr. Smith""
- Relationship labels should be concise
- Detailed descriptions should cite specific events from the summaries
- The ""id"" field should be a simplified lowercase version without titles (e.g., ""zhao"", ""miller"", ""smith"")
- Do NOT reveal information that hasn't appeared in the summaries";

        var userPrompt = $@"Analyze the characters and their relationships from these story summaries:

Book: {request.BookTitle ?? "Unknown"}

Story Summaries:
{summaryText}

Create a character relationship network graph based ONLY on information in these summaries.";

        var model = "gpt-4o"; // Use GPT-4o for cost-effective character graph generation
        Console.WriteLine($"🤖 Using model for character graph: {model}");

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:CharacterGraph"),
            temperature: cfg.GetValue<double>("AI:Temperature:CharacterGraph")
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ Character graph failed: {response.StatusCode}");
            return Results.Problem($"Character graph generation failed: {(int)response.StatusCode}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var content = aiResponseParser.ExtractText(doc.RootElement);

        if (string.IsNullOrWhiteSpace(content))
        {
            Console.WriteLine("❌ No content returned from GPT");
            return Results.Problem("No character graph data returned.");
        }

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            tokenUsage.AddUsage(promptTokens, completionTokens);
        }

        // Parse the character graph JSON
        try
        {
            using var jsonDoc = JsonDocument.Parse(content);
            var root = jsonDoc.RootElement;

            var nodesJson = root.GetProperty("nodes").GetRawText();
            var edgesJson = root.GetProperty("edges").GetRawText();

            var nodes = JsonSerializer.Deserialize<List<CharacterNode>>(nodesJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<CharacterNode>();

            var edges = JsonSerializer.Deserialize<List<CharacterEdge>>(edgesJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<CharacterEdge>();

            // Create response with metadata
            var graph = new CharacterGraphResponse(nodes, edges, totalSummaryCount, DateTime.UtcNow);

            // Save to cache
            AiContentCache.SaveCharacterGraph(request.DropboxPath, graph);
            Console.WriteLine($"✅ Character graph generated with {graph.Nodes.Count} characters and {graph.Edges.Count} relationships from {totalSummaryCount} summaries ({chapterSummaries.Count} chapter + {sectionSummaries.Count} section)");

            return Results.Ok(graph);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to parse character graph: {ex.Message}");
            Console.WriteLine($"   Content: {content.Substring(0, Math.Min(200, content.Length))}");
            return Results.Problem("Failed to parse character graph data.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Character graph generation failed: {ex.Message}");
        return Results.Problem("Failed to generate character graph.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapGet("/api/ai/characters/graph", ([FromQuery] string dropboxPath) =>
{
    if (string.IsNullOrWhiteSpace(dropboxPath))
        return Results.BadRequest(new { error = "Query parameter 'dropboxPath' is required." });

    var graph = AiContentCache.LoadCharacterGraph(dropboxPath);
    if (graph == null)
        return Results.NotFound(new { error = "No character graph found. Generate one first." });

    // Check if the graph is stale (has fewer summaries than currently exist)
    var currentChapterSummaries = AiContentCache.GetAllChapterSummariesAsStrings(dropboxPath);
    var currentSectionSummaries = AiContentCache.GetAllSectionSummaries(dropboxPath);
    var currentTotalCount = currentChapterSummaries.Count + currentSectionSummaries.Count;
    var needsUpdate = currentTotalCount > graph.SummaryCount;

    return Results.Ok(new
    {
        graph.Nodes,
        graph.Edges,
        graph.SummaryCount,
        graph.CachedAt,
        CurrentSummaryCount = currentTotalCount,
        NeedsUpdate = needsUpdate
    });
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapPost("/api/ai/characters/update", async (
    [FromBody] CharacterGraphUpdateRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITokenUsageService tokenUsage,
    IOpenAiModelHelper modelHelper,
    IAiResponseParser aiResponseParser) =>
{
    if (string.IsNullOrWhiteSpace(request.DropboxPath) || string.IsNullOrWhiteSpace(request.NewContent))
        return Results.BadRequest(new { error = "DropboxPath and NewContent are required." });

    var existingGraph = AiContentCache.LoadCharacterGraph(request.DropboxPath);
    if (existingGraph == null)
        return Results.BadRequest(new { error = "No existing character graph. Generate one first." });

    Console.WriteLine($"🔄 Updating character graph with new content...");

    var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Problem("OpenAI API key not configured.");

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");

        var existingJson = JsonSerializer.Serialize(existingGraph);

        var systemPrompt = @"You are a character relationship analyzer. Update an existing character network graph based on new story content.

Return ONLY valid JSON, no markdown.

Rules:
- Add new characters if they appear and are important
- Add new relationships discovered
- Update relationship labels if they change
- Keep the same JSON structure as the existing graph
- Do NOT remove existing characters or relationships unless directly contradicted";

        var userPrompt = $@"Existing character graph:
{existingJson}

New story content:
{request.NewContent}

Update the character graph with any new information. Return the complete updated graph.";

        var model = "gpt-4o"; // Use GPT-4o for cost-effective character graph updates
        Console.WriteLine($"🤖 Using model for character graph update: {model}");

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: cfg.GetValue<int>("AI:MaxCompletionTokens:ChapterInsight"),
            temperature: cfg.GetValue<double>("AI:Temperature:ChapterInsight")
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"❌ Character graph update failed: {response.StatusCode}");
            return Results.Problem("Failed to update character graph.");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var content = aiResponseParser.ExtractText(doc.RootElement);

        if (string.IsNullOrWhiteSpace(content))
            return Results.Problem("No updated graph data returned.");

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            tokenUsage.AddUsage(promptTokens, completionTokens);
        }

        // Parse updated graph
        var updatedGraph = JsonSerializer.Deserialize<CharacterGraphResponse>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new Exception("Failed to parse updated graph");

        // Save to cache
        AiContentCache.SaveCharacterGraph(request.DropboxPath, updatedGraph);
        Console.WriteLine($"✅ Character graph updated: {updatedGraph.Nodes.Count} characters, {updatedGraph.Edges.Count} relationships");

        return Results.Ok(updatedGraph);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Character graph update failed: {ex.Message}");
        return Results.Problem("Failed to update character graph.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5) send-to-kindle ───────────────────────────────────────────────────
app.MapPost("/api/anna/book/{md5}/send-to-kindle", async (
    HttpContext context,
    [FromRoute] string md5,
    [FromQuery] string? title,
    [FromQuery] string target,
    [FromQuery] string? coverUrl,
    AnnaArchiveService anna,
    IEmailService emailService,
    IEbookCoverService coverService,
    DropboxClient dropbox,
    IConfiguration cfg,
    IValidationService validation,
    IDownloadTrackingService downloadTracking) =>
{
    if (!validation.IsValidMd5(md5))
        return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

    if (!validation.IsValidTitle(title))
        return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

    if (string.IsNullOrWhiteSpace(target) || (target != "dad" && target != "mom"))
        return Results.BadRequest(new { error = "Invalid target. Must be 'dad' or 'mom'." });

    // Use shared helper to download book from Anna's Archive
    var (resp, fileName, acctInfo, errorMessage) = await DownloadBookFromAnnaArchiveAsync(md5, title, anna, memberKey);

    if (errorMessage != null)
    {
        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
        return Results.Ok(new { success = false, message = errorMessage, accountFastInfo = trackingInfo });
    }

    if (resp == null || fileName == null)
    {
        var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
        var trackingInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);
        return Results.Ok(new { success = false, message = "Failed to download book.", accountFastInfo = trackingInfo });
    }

    var tempFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{fileName}");

    using (resp)
    {
        try
        {
            // Get the ebook stream
            Stream ebookStream = await resp.Content.ReadAsStreamAsync();

            // Attempt cover replacement if coverUrl is provided and format is supported
            // FIXED: Now preserves ZIP metadata consistency to prevent Kindle E999 errors
            if (!string.IsNullOrWhiteSpace(coverUrl))
            {
                var ext = Path.GetExtension(fileName).TrimStart('.');
                if (coverService.IsFormatSupported(ext))
                {
                    Console.WriteLine($"[send-to-kindle] Attempting cover replacement for {fileName}");
                    ebookStream = await coverService.ReplaceCoverAsync(ebookStream, coverUrl, ext);
                }
                else
                {
                    Console.WriteLine($"[send-to-kindle] Format {ext} not supported for cover replacement, skipping");
                }
            }

            // Write to temp file
            using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await ebookStream.CopyToAsync(fileStream);
            }

            // Send email to the appropriate Kindle
            var kindleEmail = target.ToLower() == "dad"
                ? cfg["Email:DadsKindleEmail"] ?? throw new InvalidOperationException("Email:DadsKindleEmail not configured")
                : cfg["Email:MomsKindleEmail"] ?? throw new InvalidOperationException("Email:MomsKindleEmail not configured");

            await emailService.SendEmailWithAttachmentAsync(
                kindleEmail,
                "Book from Anna's Archive",
                $"Sent from Anna's Archive: {title ?? fileName}",
                tempFilePath,
                fileName);

            // After successful email send, also backup to Dropbox
            bool dropboxSuccess = false;
            string? dropboxPathResult = null;

            try
            {
                var dropboxFolder = target.ToLower() == "dad" ? "/dad_downloads" : "/mom_downloads";
                var dropboxPath = $"{dropboxFolder}/{fileName}";

                using (var fileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Console.WriteLine($"[send-to-kindle] Uploading '{fileName}' to Dropbox: {dropboxPath}");

                    var uploaded = await dropbox.Files.UploadAsync(
                        dropboxPath,
                        WriteMode.Overwrite.Instance,
                        body: fileStream
                    );

                    dropboxPathResult = uploaded.PathDisplay;
                    dropboxSuccess = true;
                    Console.WriteLine($"✅ Dropbox backup successful! Path: {dropboxPathResult}");
                }
            }
            catch (ApiException<UploadError> ex)
            {
                var details = ex.ErrorResponse?.ToString() ?? ex.ToString();
                Console.WriteLine($"⚠️ Dropbox backup failed (non-critical): {ex.Message} | Details: {details}");
            }
            catch (HttpException ex)
            {
                Console.WriteLine($"⚠️ Dropbox backup failed (non-critical, HTTP {ex.StatusCode}): {ex.Message}");
            }
            catch (DropboxException ex)
            {
                Console.WriteLine($"⚠️ Dropbox backup failed (non-critical): {ex}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Dropbox backup failed (non-critical): {ex.Message}");
            }

            // Get user name from auth context
            var userName = context.User?.FindFirst(ClaimTypes.Email)?.Value
                ?? context.User?.FindFirst(ClaimTypes.Name)?.Value
                ?? "unknown";

            // Record successful download in our tracking system
            downloadTracking.RecordDownload(md5, userName);
            Console.WriteLine($"[send-to-kindle] Recorded download for user {userName}, MD5: {md5}");

            // Get updated download tracking status
            var (downloadsLeft, downloadsPerDay) = downloadTracking.GetDownloadStatus();
            var counterInfo = new AccountFastDownloadInfoDto(downloadsLeft, downloadsPerDay);

            return Results.Ok(new
            {
                success         = true,
                message         = dropboxSuccess
                    ? $"Book sent to {target}'s Kindle and backed up to Dropbox"
                    : $"Book sent to {target}'s Kindle (Dropbox backup failed, but email succeeded)",
                dropboxPath     = dropboxPathResult,
                accountFastInfo = counterInfo
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Send to Kindle failed: {ex.Message}");
            return Results.Ok(new
            {
                success         = false,
                message         = "Failed to send book to Kindle. Please try again.",
                accountFastInfo = acctInfo
            });
        }
        finally
        {
            // Clean up temp file
            if (File.Exists(tempFilePath))
            {
                try { File.Delete(tempFilePath); }
                catch { /* ignore */ }
            }
        }
    } // end using (resp)
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 6) Login endpoint (using invite codes) ──────────────────────────────
app.MapPost("/api/auth/login", (CodeLoginRequest request, IConfiguration cfg) =>
{
    if (string.IsNullOrEmpty(request.Code))
        return Results.BadRequest(new { error = "Access code required" });

    // Get access codes from config
    var codesSection = cfg.GetSection("Auth:AccessCodes");
    var codes = codesSection.Get<List<AccessCode>>();

    if (codes == null || codes.Count == 0)
        return Results.Unauthorized();

    // Find valid code (supports both hashed and plaintext for migration)
    var validCode = codes.FirstOrDefault(c =>
    {
        // If the stored code starts with "$2" it's a BCrypt hash
        if (c.Code.StartsWith("$2"))
        {
            try
            {
                return BCrypt.Net.BCrypt.Verify(request.Code, c.Code);
            }
            catch
            {
                return false; // Invalid hash format
            }
        }

        // Fall back to plaintext comparison (DEPRECATED - migrate to BCrypt)
        return c.Code.Equals(request.Code, StringComparison.OrdinalIgnoreCase);
    });

    if (validCode == null)
        return Results.Unauthorized();

    // Generate JWT token
    var jwtSecret = cfg["Auth:JwtSecret"]!;
    var jwtKey = Encoding.UTF8.GetBytes(jwtSecret);
    var tokenExpirationDays = cfg.GetValue<int>("Auth:TokenExpirationDays", 30);

    var tokenHandler = new JwtSecurityTokenHandler();
    var claims = new List<Claim>
    {
        new Claim(ClaimTypes.Name, validCode.Name),
        new Claim(ClaimTypes.NameIdentifier, validCode.Code),
        new Claim(ClaimTypes.Role, validCode.IsAdmin ? "Admin" : "User")
    };

    var tokenDescriptor = new SecurityTokenDescriptor
    {
        Subject = new ClaimsIdentity(claims),
        Expires = DateTime.UtcNow.AddDays(tokenExpirationDays),
        Issuer = "AnnasArchiveAPI",
        Audience = "AnnasArchiveApp",
        SigningCredentials = new SigningCredentials(
            new SymmetricSecurityKey(jwtKey),
            SecurityAlgorithms.HmacSha256Signature)
    };

    var token = tokenHandler.CreateToken(tokenDescriptor);
    var tokenString = tokenHandler.WriteToken(token);

    return Results.Ok(new
    {
        token = tokenString,
        name = validCode.Name,
        isAdmin = validCode.IsAdmin,
        expiresAt = tokenDescriptor.Expires
    });
})
.RequireRateLimiting("login");

// ─── 6b) Quiz endpoints (admin only) ─────────────────────────────────────
var quizGroup = app.MapGroup("/api/quiz")
    .RequireAuthorization("AdminOnly")
    .RequireRateLimiting("api");

quizGroup.MapGet("/subjects", async (IQuizStorageService storage, CancellationToken token) =>
{
    var index = await storage.GetIndexAsync(token);
    return Results.Ok(index);
});

quizGroup.MapGet("/subjects/{subjectId}", async (string subjectId, IQuizStorageService storage, CancellationToken token) =>
{
    var subject = await storage.GetSubjectAsync(subjectId, token);
    return subject == null ? Results.NotFound() : Results.Ok(subject);
});

quizGroup.MapPut("/subjects/{subjectId}", async (
    string subjectId,
    QuizSubject subject,
    IQuizStorageService storage,
    IQuizValidationService validator,
    CancellationToken token) =>
{
    if (!string.Equals(subjectId, subject.Id, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Subject id in route must match payload id." });

    var validation = validator.ValidateSubject(subject);
    if (!validation.IsValid)
        return Results.BadRequest(new { errors = validation.Errors });

    try
    {
        var saved = await storage.SaveSubjectAsync(subjectId, subject, token);
        return Results.Ok(saved);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

quizGroup.MapDelete("/subjects/{subjectId}", async (string subjectId, IQuizStorageService storage, CancellationToken token) =>
{
    var deleted = await storage.DeleteSubjectAsync(subjectId, token);
    return deleted ? Results.Ok(new { removed = true }) : Results.NotFound();
});

// ─── 7) Development helper: Generate BCrypt hashes ───────────────────────
#if DEBUG
app.MapGet("/api/dev/hash", (string? code) =>
{
    if (string.IsNullOrEmpty(code))
        return Results.BadRequest(new { error = "Provide ?code=yourcode in the query string" });

    // Generate BCrypt hash with work factor 12 (good balance of security and performance)
    var hash = BCrypt.Net.BCrypt.HashPassword(code, workFactor: 12);

    return Results.Ok(new
    {
        original = code,
        hashed = hash,
        instructions = "Copy the 'hashed' value to appsettings.json Auth:AccessCodes:Code field"
    });
});
#endif

// ─── 8) Gaming PC Control ────────────────────────────────────────────────

// Check gaming PC status (online/offline)
app.MapGet("/api/gaming/status", async (IConfiguration cfg) =>
{
    var pcIp = "192.168.0.80"; // Gaming PC IP

    try
    {
        Console.WriteLine($"→ Checking gaming PC status at {pcIp}");

        // Use ping to check if PC is reachable
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/ping",
                Arguments = $"-c 1 -W 1 {pcIp}", // 1 ping with 1 second timeout
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync();

        var isOnline = process.ExitCode == 0;
        Console.WriteLine($"✅ Gaming PC status: {(isOnline ? "ONLINE" : "OFFLINE")}");

        return Results.Ok(new
        {
            isOnline = isOnline,
            ipAddress = pcIp,
            lastChecked = DateTime.UtcNow
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Gaming PC status check exception: {ex.Message}");
        return Results.Ok(new
        {
            isOnline = false,
            ipAddress = pcIp,
            lastChecked = DateTime.UtcNow,
            error = "Failed to check PC status"
        });
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapPost("/api/gaming/toggle", async (
    [FromQuery] int action,
    IConfiguration cfg) =>
{
    if (action != 1 && action != 2)
        return Results.BadRequest(new { error = "Invalid action. Use 1 to wake PC, 2 to sleep PC." });

    var synologyHost = cfg["Gaming:SynologyHost"];
    var synologyUser = cfg["Gaming:SynologyUser"];
    var synologyKeyPath = cfg["Gaming:SynologyKeyPath"];

    if (string.IsNullOrEmpty(synologyHost) || string.IsNullOrEmpty(synologyUser))
        return Results.Problem("Gaming PC control is not configured.");

    try
    {
        var actionName = action == 1 ? "wake" : "sleep";
        Console.WriteLine($"→ Gaming PC {actionName} request received");

        // SSH into Synology and run the wake-steam.sh script
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/ssh",
                Arguments = string.IsNullOrEmpty(synologyKeyPath)
                    ? $"{synologyUser}@{synologyHost} \"/usr/local/bin/wake-steam.sh {action}\""
                    : $"-i {synologyKeyPath} {synologyUser}@{synologyHost} \"/usr/local/bin/wake-steam.sh {action}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode == 0)
        {
            Console.WriteLine($"✅ Gaming PC {actionName} successful");
            Console.WriteLine(output);
            return Results.Ok(new
            {
                success = true,
                action = actionName,
                message = action == 1
                    ? "Gaming PC is waking up and launching Steam..."
                    : "Gaming PC is shutting down...",
                output = output
            });
        }
        else
        {
            Console.WriteLine($"❌ Gaming PC {actionName} failed: {error}");
            return Results.Ok(new
            {
                success = false,
                action = actionName,
                message = "Failed to control gaming PC.",
                error = error
            });
        }
    }
    catch (Exception ex)
{
    Console.WriteLine($"❌ Gaming PC control exception: {ex.Message}");
    return Results.Problem("An error occurred while controlling the gaming PC.");
}
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── Shared Helper Functions ────────────────────────────────────────────

/// <summary>
/// Loads chapter content from Dropbox EPUB cache.
/// </summary>
/// <returns>Chapter content string, or null if not found/empty</returns>
static async Task<CachedChapterIndex?> LoadChapterIndexAsync(
    DropboxClient dropbox,
    string dropboxPath)
{
    var existingKeys = AiContentCache.GetExistingSummaryKeys();
    if (TryResolveLibraryFileForReaderKey(dropboxPath, existingKeys, out _, out var fullPath))
    {
        var (index, _) = await LibraryEpubCache.GetOrBuildChapterIndexAsync(fullPath, dropboxPath);
        return index;
    }

    var (dropboxIndex, _) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, dropboxPath);
    return dropboxIndex;
}

static async Task<string?> LoadChapterContentAsync(
    DropboxClient dropbox,
    string dropboxPath,
    int chapterId)
{
    var existingKeys = AiContentCache.GetExistingSummaryKeys();
    if (TryResolveLibraryFileForReaderKey(dropboxPath, existingKeys, out _, out var fullPath))
    {
        var (index, cacheDir) = await LibraryEpubCache.GetOrBuildChapterIndexAsync(fullPath, dropboxPath);
        var chapter = index.Chapters.FirstOrDefault(c => c.Id == chapterId);

        if (chapter is null || string.IsNullOrWhiteSpace(chapter.FileName))
            return null;

        var chapterPath = Path.Combine(cacheDir, chapter.FileName);
        if (!File.Exists(chapterPath))
            await LibraryEpubCache.EnsureCacheBuildAsync(fullPath, dropboxPath, cacheDir);

        var content = await File.ReadAllTextAsync(chapterPath);
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    var (dropboxIndex, dropboxCacheDir) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, dropboxPath);
    var dropboxChapter = dropboxIndex.Chapters.FirstOrDefault(c => c.Id == chapterId);

    if (dropboxChapter is null || string.IsNullOrWhiteSpace(dropboxChapter.FileName))
        return null;

    var dropboxChapterPath = Path.Combine(dropboxCacheDir, dropboxChapter.FileName);
    if (!File.Exists(dropboxChapterPath))
        await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, dropboxPath, dropboxCacheDir);

    var dropboxContent = await File.ReadAllTextAsync(dropboxChapterPath);
    return string.IsNullOrWhiteSpace(dropboxContent) ? null : dropboxContent;
}

/// <summary>
/// TIER 1: Summarizes text chunks with progress updates via SSE.
/// </summary>
/// <returns>Tuple of (chunk summaries list, total prompt tokens, total completion tokens)</returns>
static async Task<(List<string> chunkSummaries, int promptTokens, int completionTokens)> SummarizeChunksAsync(
    HttpClient http,
    string model,
    List<string> chunks,
    string contextLine,
    HttpResponse response,
    IConfiguration cfg,
    IAiResponseParser aiResponseParser,
    ITokenUsageService tokenUsage)
{
    var chunkSummaries = new List<string>();
    var promptTokensTotal = 0;
    var completionTokensTotal = 0;

    var chunkInstructions = @"You are an educational guide helping someone deeply understand complex texts. Analyze this passage with rich detail:

1. **What's Happening**: Summarize the main points, arguments, or narrative events
2. **Key Concepts**: Identify and explain central ideas or terminology
3. **Context**: What historical, philosophical, or intellectual background is relevant?
4. **Significance**: Why does this matter? What is the author building toward?

Write 300-400 words that assume the reader is intelligent but may lack specialized background knowledge. Explain references and provide context.";

    for (var i = 0; i < chunks.Count; i++)
    {
        var chunk = chunks[i];
        var chunkInput = $"{chunkInstructions}\n\nContext: {contextLine}\n\n{chunk}";

        await ServerSentEventsHelper.SendEventAsync(response, new
        {
            stage = "chunks",
            stepNumber = i + 1,
            totalSteps = chunks.Count,
            message = $"Analyzing chunk {i + 1}/{chunks.Count}..."
        }, "progress");

        var payload = new
        {
            model,
            input = chunkInput,
            reasoning = new { effort = cfg.GetValue<string>("AI:ReasoningEffort:ChunkSummary") },
            max_output_tokens = cfg.GetValue<int>("AI:MaxCompletionTokens:ChunkSummary")
        };

        var chunkResponse = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload);
        if (!chunkResponse.IsSuccessStatusCode)
        {
            var body = await chunkResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI chunk summary failed status={(int)chunkResponse.StatusCode} body={body}");
            await ServerSentEventsHelper.SendEventAsync(response, new
            {
                stage = "chunks",
                stepNumber = i + 1,
                totalSteps = chunks.Count,
                message = $"Failed at chunk {i + 1}/{chunks.Count}",
                error = $"HTTP {(int)chunkResponse.StatusCode}: {body}"
            }, "error");
            throw new HttpRequestException($"Chunk summarization failed at chunk {i + 1}");
        }

        using var stream = await chunkResponse.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        Console.WriteLine($"🔍 Chunk {i + 1} response JSON: {doc.RootElement.GetRawText()}");

        var chunkSummary = aiResponseParser.ExtractText(doc.RootElement) ?? string.Empty;
        Console.WriteLine($"🔍 Chunk {i + 1} extracted summary length: {chunkSummary.Length}");

        chunkSummaries.Add(chunkSummary);

        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            promptTokensTotal += usage.GetProperty("input_tokens").GetInt32();
            completionTokensTotal += usage.GetProperty("output_tokens").GetInt32();
        }

        await ServerSentEventsHelper.SendEventAsync(response, new
        {
            stage = "chunks",
            stepNumber = i + 1,
            totalSteps = chunks.Count,
            message = $"Completed chunk {i + 1}/{chunks.Count}",
            success = true
        }, "progress");
    }

    return (chunkSummaries, promptTokensTotal, completionTokensTotal);
}

/// <summary>
/// TIER 2: Synthesizes chunk summaries into section summaries with progress updates via SSE.
/// </summary>
/// <returns>Tuple of (section summaries list, total prompt tokens, total completion tokens)</returns>
static async Task<(List<string> sectionSummaries, int promptTokens, int completionTokens)> SynthesizeSectionsAsync(
    HttpClient http,
    string model,
    List<string> chunkSummaries,
    string contextLine,
    HttpResponse response,
    IConfiguration cfg,
    IAiResponseParser aiResponseParser,
    ITokenUsageService tokenUsage,
    int chunksPerSection = 4)
{
    var sectionSummaries = new List<string>();
    var promptTokensTotal = 0;
    var completionTokensTotal = 0;

    var totalSections = (int)Math.Ceiling((double)chunkSummaries.Count / chunksPerSection);
    var sectionNum = 0;

    var sectionInstructions = @"You are synthesizing multiple passage analyses into a coherent section summary. Create a unified narrative that:

1. **Traces the Development**: How do the ideas/arguments/events progress through these passages?
2. **Identifies Core Themes**: What are the central concerns of this section?
3. **Contextualizes**: What intellectual traditions, historical debates, or prior thinkers is the author engaging with?
4. **Clarifies**: Explain difficult concepts in accessible terms

Write 400-500 words. Maintain educational depth while creating a flowing narrative.";

    for (var i = 0; i < chunkSummaries.Count; i += chunksPerSection)
    {
        sectionNum++;
        var sectionChunks = chunkSummaries.Skip(i).Take(chunksPerSection).ToList();
        if (sectionChunks.Count == 0) continue;

        await ServerSentEventsHelper.SendEventAsync(response, new
        {
            stage = "sections",
            stepNumber = sectionNum,
            totalSteps = totalSections,
            message = $"Synthesizing section {sectionNum}/{totalSections}..."
        }, "progress");

        var sectionInput = $"{sectionInstructions}\n\nContext: {contextLine}\n\n{string.Join("\n\n---\n\n", sectionChunks)}";

        var sectionPayload = new
        {
            model,
            input = sectionInput,
            reasoning = new { effort = cfg.GetValue<string>("AI:ReasoningEffort:SectionSynthesis") },
            max_output_tokens = cfg.GetValue<int>("AI:MaxCompletionTokens:SectionSynthesis")
        };

        var sectionResponse = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", sectionPayload);
        if (!sectionResponse.IsSuccessStatusCode)
        {
            var body = await sectionResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI section summary failed status={(int)sectionResponse.StatusCode} body={body}");
            await ServerSentEventsHelper.SendEventAsync(response, new
            {
                stage = "sections",
                stepNumber = sectionNum,
                totalSteps = totalSections,
                message = $"Failed at section {sectionNum}/{totalSections}",
                error = $"HTTP {(int)sectionResponse.StatusCode}: {body}"
            }, "error");
            throw new HttpRequestException($"Section synthesis failed at section {sectionNum}");
        }

        using var sectionStream = await sectionResponse.Content.ReadAsStreamAsync();
        using var sectionDoc = await JsonDocument.ParseAsync(sectionStream);
        var sectionSummary = aiResponseParser.ExtractText(sectionDoc.RootElement) ?? string.Empty;

        sectionSummaries.Add(sectionSummary);

        if (sectionDoc.RootElement.TryGetProperty("usage", out var sectionUsage))
        {
            promptTokensTotal += sectionUsage.GetProperty("input_tokens").GetInt32();
            completionTokensTotal += sectionUsage.GetProperty("output_tokens").GetInt32();
        }

        await ServerSentEventsHelper.SendEventAsync(response, new
        {
            stage = "sections",
            stepNumber = sectionNum,
            totalSteps = totalSections,
            message = $"Completed section {sectionNum}/{totalSections}",
            success = true
        }, "progress");
    }

    return (sectionSummaries, promptTokensTotal, completionTokensTotal);
}

/// <summary>
/// TIER 3: Creates final comprehensive summary from section summaries with progress update via SSE.
/// </summary>
/// <returns>Tuple of (final summary text, prompt tokens used, completion tokens used)</returns>
static async Task<(string finalSummary, int promptTokens, int completionTokens)> CreateFinalSummaryAsync(
    HttpClient http,
    string model,
    List<string> sectionSummaries,
    List<string> contextParts,
    HttpResponse response,
    IConfiguration cfg,
    IAiResponseParser aiResponseParser)
{
    await ServerSentEventsHelper.SendEventAsync(response, new
    {
        stage = "final",
        stepNumber = 1,
        totalSteps = 1,
        message = "Creating final comprehensive summary..."
    }, "progress");

    var finalInstructions = $@"Create a comprehensive 700-900 word educational summary of this chapter that helps someone truly understand and appreciate the material.

Your summary should cover:

1. **Overview**:
   - What is this chapter fundamentally about?
   - What are the main arguments, ideas, or events?

2. **Historical & Intellectual Context**:
   - When and where was this written?
   - What historical events, political climate, or cultural conditions shaped this work?
   - What intellectual traditions or prior thinkers is the author responding to?
   - What debates or questions was the author engaging with?

3. **Core Arguments & Ideas**:
   - What are the key claims or propositions?
   - How does the author support these claims?
   - What concepts or terminology are central to understanding this?

4. **Significance & Interpretation**:
   - Why does this matter?
   - What impact has this had (or might it have)?
   - What makes this important or interesting?

5. **Connections**:
   - How does this relate to other thinkers, movements, or texts?
   - What contemporary issues or questions does this illuminate?

Write as if teaching an intelligent student. Define specialized terms, explain references, and provide context that helps someone new to this material truly understand what's going on and why it matters. Be thorough and educational.";

    var userContent = $"Book context: {string.Join(" | ", contextParts)}\n\nSection summaries:\n{string.Join("\n\n---\n\n", sectionSummaries)}";
    var fullInput = $"{finalInstructions}\n\n{userContent}";

    var finalPrompt = new
    {
        model,
        input = fullInput,
        reasoning = new { effort = cfg.GetValue<string>("AI:ReasoningEffort:FinalSummary") },
        max_output_tokens = cfg.GetValue<int>("AI:MaxCompletionTokens:FinalSummary")
    };

    var finalResponse = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", finalPrompt);
    if (!finalResponse.IsSuccessStatusCode)
    {
        var body = await finalResponse.Content.ReadAsStringAsync();
        Console.WriteLine($"❌ OpenAI final summary failed status={(int)finalResponse.StatusCode} body={body}");
        await ServerSentEventsHelper.SendEventAsync(response, new
        {
            stage = "final",
            stepNumber = 1,
            totalSteps = 1,
            message = "Failed to create final summary",
            error = $"HTTP {(int)finalResponse.StatusCode}: {body}"
        }, "error");
        throw new HttpRequestException("Final summary creation failed");
    }

    using var finalStream = await finalResponse.Content.ReadAsStreamAsync();
    using var finalDoc = await JsonDocument.ParseAsync(finalStream);

    Console.WriteLine($"🔍 Final response JSON: {finalDoc.RootElement.GetRawText()}");

    string finalSummary = aiResponseParser.ExtractText(finalDoc.RootElement) ?? "No summary returned.";
    Console.WriteLine($"🔍 Extracted summary length: {finalSummary.Length}");

    var promptTokens = 0;
    var completionTokens = 0;

    if (finalDoc.RootElement.TryGetProperty("usage", out var finalUsage))
    {
        promptTokens = finalUsage.GetProperty("input_tokens").GetInt32();
        completionTokens = finalUsage.GetProperty("output_tokens").GetInt32();
    }

    return (finalSummary, promptTokens, completionTokens);
}

/// <summary>
/// Downloads a book from Anna's Archive and prepares it for delivery.
/// Handles download URL extraction, account info parsing, title sanitization, and file extension detection.
/// </summary>
/// <returns>
/// A tuple containing:
/// - response: HttpResponseMessage with the file stream
/// - fileName: Sanitized file name with appropriate extension
/// - accountInfo: Account download info if available (null - tracking happens at endpoint level)
/// - errorMessage: Error message if something went wrong (null on success)
/// </returns>
static async Task<(HttpResponseMessage? response, string? fileName, AccountFastDownloadInfoDto? accountInfo, string? errorMessage)>
    DownloadBookFromAnnaArchiveAsync(
        string md5,
        string? title,
        AnnaArchiveService anna,
        string memberKey)
{
    // Get download document from Anna's Archive
    JsonElement doc;
    try
    {
        doc = await anna.GetMemberDownloadDocumentAsync(md5, memberKey);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Rate limit"))
    {
        return (null, null, null, "⏱️ Rate limit exceeded. Please wait 30-60 seconds before trying again.");
    }
    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
    {
        return (null, null, null, "⏱️ Rate limit exceeded. Please wait 30-60 seconds before trying again.");
    }

    // Extract download URL
    string? downloadUrl = null;
    if (doc.TryGetProperty("download_url", out var du))
        downloadUrl = du.ValueKind == JsonValueKind.String
                    ? du.GetString()
                    : du.EnumerateArray().FirstOrDefault().GetString();

    if (string.IsNullOrEmpty(downloadUrl))
        return (null, null, null, "No download URL found.");

    // Extract account info
    AccountFastDownloadInfoDto? acctInfo = null;
    if (doc.TryGetProperty("account_fast_download_info", out var ai) &&
        ai.ValueKind == JsonValueKind.Object)
        acctInfo = new AccountFastDownloadInfoDto(
            ai.GetProperty("downloads_left").GetInt32(),
            ai.GetProperty("downloads_per_day").GetInt32());

    // Download the file
    var resp = await anna.GetDownloadResponseWithFallbackAsync(
        downloadUrl,
        HttpCompletionOption.ResponseHeadersRead);
    if (resp == null || !resp.IsSuccessStatusCode)
        return (null, null, acctInfo, "Download failed.");

    // Sanitize title
    var rawTitle  = !string.IsNullOrWhiteSpace(title) ? title : md5;
    var safeTitle = Regex.Replace(rawTitle, $"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]", "_");

    // Determine file extension
    var ext = Path.GetExtension(new Uri(downloadUrl).AbsolutePath);
    if (string.IsNullOrEmpty(ext))
        ext = resp.Content.Headers.ContentType?.MediaType switch
        {
            "application/pdf"                 => ".pdf",
            "application/epub+zip"            => ".epub",
            "application/x-mobipocket-ebook"  => ".mobi",
            _                                 => ".bin"
        };

    var fileName = $"{safeTitle}{ext}";

    return (resp, fileName, acctInfo, null);
}

static string ResolveLibraryRoot()
{
    var envRoot = Environment.GetEnvironmentVariable("LIBRARY_ROOT");
    if (!string.IsNullOrWhiteSpace(envRoot))
        return envRoot;

    const string synologyDefault = "/volume1/books/Library";
    if (Directory.Exists(synologyDefault))
        return synologyDefault;

    return Path.Combine(AppContext.BaseDirectory, "library");
}

static string ResolveReaderKey(string fileName, ISet<string> existingKeys)
{
    if (existingKeys == null || existingKeys.Count == 0)
        return fileName;

    var sanitized = AiContentCache.SanitizeKey(fileName);
    if (existingKeys.Contains(sanitized))
        return sanitized;

    var match = existingKeys.FirstOrDefault(key =>
        key.EndsWith(sanitized, StringComparison.OrdinalIgnoreCase));
    return match ?? fileName;
}

static bool TryResolveLibraryFileForReaderKey(
    string readerKey,
    ISet<string> existingKeys,
    out string fileName,
    out string fullPath)
{
    fileName = string.Empty;
    fullPath = string.Empty;
    var libraryRoot = ResolveLibraryRoot();
    if (!Directory.Exists(libraryRoot))
        return false;

    var safeFileName = Path.GetFileName(readerKey);
    if (!string.IsNullOrWhiteSpace(safeFileName))
    {
        var directPath = Path.Combine(libraryRoot, safeFileName);
        if (File.Exists(directPath))
        {
            fileName = safeFileName;
            fullPath = directPath;
            return true;
        }
    }

    var jsonOptions = CreateLibraryJsonOptions();
    foreach (var metaFile in Directory.GetFiles(libraryRoot, "*.meta.json"))
    {
        try
        {
            var json = File.ReadAllText(metaFile);
            var meta = JsonSerializer.Deserialize<LibraryBookMeta>(json, jsonOptions);
            if (meta == null || string.IsNullOrWhiteSpace(meta.FileName))
                continue;

            var key = ResolveReaderKey(meta.FileName, existingKeys);
            if (!string.Equals(key, readerKey, StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = Path.Combine(libraryRoot, meta.FileName);
            if (File.Exists(candidate))
            {
                fileName = meta.FileName;
                fullPath = candidate;
                return true;
            }
        }
        catch
        {
            // ignore malformed meta files
        }
    }

    return false;
}

static JsonSerializerOptions CreateLibraryJsonOptions()
{
    return new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

static async Task WriteLibraryMetadataAsync(
    string libraryRoot,
    string fileName,
    string md5,
    string? title,
    string? authors,
    string? format,
    string? fileSize,
    string? coverUrl,
    string? source,
    string? userTag)
{
    var metaPath = Path.Combine(libraryRoot, $"{fileName}.meta.json");
    var authorList = string.IsNullOrWhiteSpace(authors)
        ? Array.Empty<string>()
        : authors.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var tags = string.IsNullOrWhiteSpace(userTag)
        ? Array.Empty<string>()
        : new[] { userTag };

    var meta = new LibraryBookMeta(
        title ?? Path.GetFileNameWithoutExtension(fileName),
        authorList,
        format,
        fileSize,
        fileName,
        coverUrl,
        source,
        md5,
        DateTime.UtcNow,
        null,
        tags,
        null,
        Array.Empty<string>(),
        null,
        null,
        null,
        null,
        null
    );

    var jsonOptions = CreateLibraryJsonOptions();
    await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, jsonOptions));
}

static string? ResolveUserLibraryTag(HttpContext context)
{
    var role = context.User?.FindFirst(ClaimTypes.Role)?.Value;
    var accessCode = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
        return "Paul's Books";

    if (string.Equals(accessCode, "cffdab-233322", StringComparison.OrdinalIgnoreCase))
        return "Mom's Books";

    if (string.Equals(accessCode, "eikdab-233322", StringComparison.OrdinalIgnoreCase))
        return "Dad's Books";

    return null;
}

static string FormatFileSize(long bytes)
{
    if (bytes <= 0)
        return "0B";

    string[] units = { "B", "KB", "MB", "GB" };
    var size = (double)bytes;
    var unitIndex = 0;
    while (size >= 1024 && unitIndex < units.Length - 1)
    {
        size /= 1024;
        unitIndex++;
    }

    return $"{size:0.0}{units[unitIndex]}";
}

static async Task<List<string>> ExtractBookTitlesFromQueryAsync(
    string query,
    IHttpClientFactory httpFactory,
    CancellationToken cancellationToken)
{
    var urls = ExtractUrls(query);
    if (urls.Count == 0)
        return new List<string>();

    var results = new List<string>();
    foreach (var url in urls)
    {
        var titles = await FetchBookTitlesFromUrlAsync(url, httpFactory, cancellationToken);
        results.AddRange(titles);
        if (results.Count >= 120)
            break;
    }

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var deduped = new List<string>();
    foreach (var title in results)
    {
        if (seen.Add(title))
            deduped.Add(title);
    }

    return deduped;
}

static List<string> ExtractUrls(string query)
{
    if (string.IsNullOrWhiteSpace(query))
        return new List<string>();

    var matches = Regex.Matches(query, @"https?://\S+");
    return matches.Select(m => m.Value.Trim().TrimEnd(')', ']', '}', '.', ',', ';', '"', '\''))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static async Task<List<string>> FetchBookTitlesFromUrlAsync(
    string url,
    IHttpClientFactory httpFactory,
    CancellationToken cancellationToken)
{
    try
    {
        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(12);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");

        using var resp = await http.GetAsync(url, cancellationToken);
        if (!resp.IsSuccessStatusCode)
            return new List<string>();

        var html = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
            return new List<string>();

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var container = doc.DocumentNode.SelectSingleNode("//article")
                        ?? doc.DocumentNode.SelectSingleNode("//main")
                        ?? doc.DocumentNode;
        var contentRoot = SelectBestContentRoot(container, doc, html, url) ?? container;

        var titles = new List<string>();
        var listCandidates = contentRoot.SelectNodes(".//ol|.//ul");
        var listNode = listCandidates?
            .Where(node => !IsNavigationNode(node) && !HasNavigationAncestor(node))
            .Select(node => new { Node = node, Score = CountLikelyBookItems(node) })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Node.SelectNodes(".//li")?.Count ?? 0)
            .FirstOrDefault()?.Node;

        var listLikelyCount = listNode is null ? 0 : CountLikelyBookItems(listNode);
        var listItems = listLikelyCount > 0 ? listNode?.SelectNodes(".//li") : null;
        if (listItems != null && listLikelyCount > 0)
        {
            foreach (var item in listItems)
            {
                if (HasNavigationAncestor(item)) continue;
                var emphasized = item.SelectNodes(".//em|.//i|.//cite|.//strong");
                if (emphasized != null && emphasized.Count > 0)
                {
                    foreach (var node in emphasized)
                    {
                        if (HasNavigationAncestor(node)) continue;
                        var candidate = CleanBookTitle(node.InnerText);
                        if (IsReasonableTitle(candidate) && !LooksLikeNavigation(candidate))
                            titles.Add(candidate);
                    }
                }
                else
                {
                    var candidate = CleanBookTitle(item.InnerText);
                    if (IsReasonableTitle(candidate) && !LooksLikeNavigation(candidate))
                        titles.Add(candidate);
                }
            }
        }

        if (titles.Count == 0)
        {
            var headings = contentRoot.SelectNodes(".//h2|.//h3") ?? doc.DocumentNode.SelectNodes("//h2|//h3");
            if (headings != null)
            {
                foreach (var node in headings)
                {
                    if (HasNavigationAncestor(node)) continue;
                    var candidate = CleanBookTitle(node.InnerText);
                    if (IsReasonableTitle(candidate) && !LooksLikeNavigation(candidate))
                        titles.Add(candidate);
                }
            }
        }

        if (titles.Count == 0)
        {
            var emphasis = contentRoot.SelectNodes(".//p//strong|.//p//em|.//p//i|.//p//cite");
            if (emphasis != null)
            {
                foreach (var node in emphasis)
                {
                    if (HasNavigationAncestor(node)) continue;
                    var candidate = CleanBookTitle(node.InnerText);
                    if (IsReasonableTitle(candidate) && !LooksLikeNavigation(candidate))
                        titles.Add(candidate);
                }
            }
        }

        return titles;
    }
    catch
    {
        return new List<string>();
    }
}

static string CleanBookTitle(string raw)
{
    var text = WebUtility.HtmlDecode(raw ?? string.Empty);
    text = Regex.Replace(text, @"\s+", " ").Trim();
    text = Regex.Replace(text, @"^\d+[\.\)\-:\s]+", "");
    text = Regex.Replace(text, @"^[-•*]+\s*", "");
    var bySplit = Regex.Split(text, @"\s+by\s+", RegexOptions.IgnoreCase);
    text = bySplit.Length > 1 ? bySplit[0] : text;
    text = Regex.Split(text, @"\s+[—–-]\s+").FirstOrDefault() ?? text;
    text = text.Trim().Trim('"', '\'', '“', '”', '’', '‘');
    return text;
}

static bool IsReasonableTitle(string? title)
{
    if (string.IsNullOrWhiteSpace(title))
        return false;
    if (title.Length < 2 || title.Length > 120)
        return false;
    var letters = title.Count(char.IsLetter);
    return letters >= 2;
}

static bool LooksLikeNavigation(string title)
{
    var lower = title.ToLowerInvariant();
    if (lower.Contains("related post") || lower.Contains("related articles"))
        return true;
    if (lower.Contains("listen to this article"))
        return true;
    if (lower.Contains("read more") || lower.Contains("comments"))
        return true;
    if (lower.Contains("subscribe") || lower.Contains("newsletter"))
        return true;
    if (lower.Contains("category") || lower.Contains("categories"))
        return true;
    if (lower.Contains("advertisement") || lower.Contains("sponsored"))
        return true;
    return false;
}

static HtmlNode? SelectBestContentRoot(HtmlNode container, HtmlDocument doc, string html, string url)
{
    var candidates = container.SelectNodes(".//*[contains(@class,'entry-content') or contains(@class,'post-content') or contains(@class,'article-content') or contains(@class,'post-content-inner') or contains(@class,'post-content-column')]");
    if (candidates != null && candidates.Count > 0)
    {
        return candidates
            .OrderByDescending(node => (node.InnerText ?? string.Empty).Length)
            .FirstOrDefault();
    }

    return ExtractReadableRoot(doc, html, url);
}

static bool HasNavigationAncestor(HtmlNode node)
{
    var current = node.ParentNode;
    while (current != null)
    {
        if (IsNavigationNode(current))
            return true;
        current = current.ParentNode;
    }

    return false;
}

static bool IsNavigationNode(HtmlNode node)
{
    var name = node.Name.ToLowerInvariant();
    if (name is "nav" or "header" or "footer" or "aside" or "form")
        return true;

    var classId = $"{node.GetAttributeValue("class", "")} {node.GetAttributeValue("id", "")}".ToLowerInvariant();
    string[] tokens =
    {
        "nav", "menu", "footer", "header", "sidebar", "widget", "breadcrumb", "related", "share",
        "social", "subscribe", "newsletter", "category", "tag", "promo", "advert", "ads", "comment",
        "search", "pagination", "toolbar"
    };

    return tokens.Any(token => classId.Contains(token));
}

static int CountLikelyBookItems(HtmlNode listNode)
{
    var listItems = listNode.SelectNodes(".//li");
    if (listItems == null || listItems.Count == 0)
        return 0;

    var count = 0;
    foreach (var item in listItems)
    {
        if (HasNavigationAncestor(item))
            continue;

        var emphasized = item.SelectNodes(".//em|.//i|.//cite|.//strong");
        if (emphasized != null && emphasized.Count > 0)
        {
            foreach (var node in emphasized)
            {
                if (HasNavigationAncestor(node)) continue;
                var candidate = CleanBookTitle(node.InnerText);
                if (IsReasonableTitle(candidate) && !LooksLikeNavigation(candidate))
                {
                    count++;
                    break;
                }
            }
        }
        else
        {
            var candidate = CleanBookTitle(item.InnerText);
            if (IsReasonableTitle(candidate) && !LooksLikeNavigation(candidate))
                count++;
        }
    }

    return count;
}

static HtmlNode? ExtractReadableRoot(HtmlDocument doc, string html, string url)
{
    try
    {
        var nReadability = new NReadabilityWebTranscoder();
        var nReadabilityResult = nReadability.Transcode(new WebTranscodingInput(html));
        if (nReadabilityResult.ContentExtracted)
        {
            var readableDoc = new HtmlDocument();
            var readableHtml = nReadabilityResult.ExtractedContent ?? string.Empty;
            readableDoc.LoadHtml(readableHtml);
            var readableRoot = readableDoc.DocumentNode.SelectSingleNode("//article")
                               ?? readableDoc.DocumentNode.SelectSingleNode("//main")
                               ?? readableDoc.DocumentNode;
            if (readableRoot != null)
                return readableRoot;
        }

        var candidates = doc.DocumentNode.SelectNodes("//article|//main|//section|//div");
        if (candidates == null || candidates.Count == 0)
            return null;

        HtmlNode? best = null;
        double bestScore = 0;

        foreach (var node in candidates)
        {
            if (IsBoilerplateNode(node))
                continue;

            var text = HtmlEntity.DeEntitize(node.InnerText ?? string.Empty);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length < 200)
                continue;

            var linkTextLength = node.SelectNodes(".//a")?
                .Select(a => HtmlEntity.DeEntitize(a.InnerText ?? string.Empty).Trim())
                .Where(s => s.Length > 0)
                .Sum(s => s.Length) ?? 0;

            var linkDensity = text.Length == 0 ? 1.0 : (double)linkTextLength / text.Length;
            var score = text.Length * (1 - linkDensity);

            if (score > bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        return best;
    }
    catch
    {
        return null;
    }
}

static bool IsBoilerplateNode(HtmlNode node)
{
    var classId = $"{node.GetAttributeValue("class", "")} {node.GetAttributeValue("id", "")}".ToLowerInvariant();
    if (classId.Contains("nav") || classId.Contains("menu") || classId.Contains("footer") || classId.Contains("header"))
        return true;
    if (classId.Contains("sidebar") || classId.Contains("widget") || classId.Contains("related"))
        return true;
    if (classId.Contains("promo") || classId.Contains("advert") || classId.Contains("ad-") || classId.Contains("ads"))
        return true;
    if (classId.Contains("newsletter") || classId.Contains("subscribe") || classId.Contains("share"))
        return true;
    if (classId.Contains("comment") || classId.Contains("breadcrumb"))
        return true;
    return false;
}

static string? NormalizeLibraryCoverUrl(string? coverValue, string baseUrl)
{
    if (string.IsNullOrWhiteSpace(coverValue))
        return null;

    if (coverValue.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        return coverValue;

    var normalized = coverValue.Replace("\\", "/").TrimStart('/');
    var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(Uri.EscapeDataString);
    var encodedPath = string.Join("/", segments);

    return $"{baseUrl}/api/library/cover/{encodedPath}";
}

static string? FindLocalCoverUrl(string libraryRoot, string fileName, string baseUrl)
{
    var coverDir = Path.Combine(libraryRoot, "_covers");
    if (!Directory.Exists(coverDir))
        return null;

    var safeName = Path.GetFileName(fileName);
    var matches = Directory.GetFiles(coverDir, $"{safeName}.cover.*");
    var match = matches.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(match))
        return null;

    var relative = Path.Combine("_covers", Path.GetFileName(match)).Replace("\\", "/");
    return NormalizeLibraryCoverUrl(relative, baseUrl);
}

app.Run();

// ─── Helper Classes ──────────────────────────────────────────────────────

public static class ServerSentEventsHelper
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task SendEventAsync(HttpResponse response, object data, string? eventName = null)
    {
        if (eventName is not null)
        {
            await response.WriteAsync($"event: {eventName}\n");
        }

        var json = JsonSerializer.Serialize(data, SseJsonOptions);
        await response.WriteAsync($"data: {json}\n\n");
        await response.Body.FlushAsync();
    }
}

// ─── Standard API Response Helper ───────────────────────────────────────
public static class ApiResponse
{
    /// <summary>
    /// Returns a standardized 400 Bad Request with error message
    /// </summary>
    public static IResult BadRequest(string errorMessage) =>
        Results.BadRequest(new { error = errorMessage });

    /// <summary>
    /// Returns a standardized 404 Not Found with error message
    /// </summary>
    public static IResult NotFound(string errorMessage) =>
        Results.NotFound(new { error = errorMessage });

    /// <summary>
    /// Returns a standardized 500 Internal Server Error with error message
    /// </summary>
    public static IResult InternalError(string errorMessage) =>
        Results.Problem(detail: errorMessage, statusCode: 500);

    /// <summary>
    /// Returns a standardized 401 Unauthorized
    /// </summary>
    public static IResult Unauthorized() =>
        Results.Unauthorized();
}

// ─── DTOs ────────────────────────────────────────────────────────────────
record CodeLoginRequest(string Code);
record AccessCode(string Code, string Name, bool IsAdmin);
record DropboxEpubFileDto(string Id, string Name, string Path, long Size, DateTime ServerModified);
record DropboxEpubChaptersResponse(string Title, List<DropboxChapterDto> Chapters);
record DropboxChapterDto(int Id, string Title, int Level, int WordCount, string? DisplayLabel, bool? IsMainChapter);
record DropboxChapterContentDto(int Id, string Title, string Content, int CharacterCount, int WordCount);
record FlatChapter(int Id, string Title, int Level, string PlainText, int WordCount);
record LabeledChapter(FlatChapter Chapter, string DisplayLabel, bool IsMainChapter);
record ChapterLabelResult(int Id, string DisplayLabel, bool IsMainChapter);
record CachedChapterMeta(int Id, string Title, int Level, int CharacterCount, int WordCount, string FileName, string? DisplayLabel, bool? IsMainChapter);
record CachedChapterIndex(string Path, string Title, DateTime CachedAt, List<CachedChapterMeta> Chapters, string? LabelSource = null);
record DropboxCacheStatusDto(bool Cached, bool InProgress, int ChaptersTotal, int ChaptersCached, double Percent, DateTime? CachedAt, string? Error);
record DropboxSearchMatchDto(int ChapterId, string Title, int MatchCount, int Position, string Snippet);
static class ChapterLabeler
{
    private static readonly string[] FrontMatterKeywords =
    {
        "table of contents",
        "contents",
        "toc",
        "preface",
        "foreword",
        "introduction",
        "acknowledgments",
        "acknowledgements",
        "about the author",
        "author's note",
        "authors' note",
        "notes",
        "endnotes",
        "footnotes",
        "bibliography",
        "references",
        "index",
        "glossary",
        "appendix",
        "appendices",
        "maps",
        "map",
        "list of illustrations",
        "list of figures",
        "list of tables",
        "illustrations",
        "figures",
        "tables",
        "dedication",
        "copyright",
        "imprint",
        "credits",
        "colophon",
        "prologue",
        "epilogue",
        "afterword"
    };

    private static readonly string[] StructuralKeywords =
    {
        "part",
        "book",
        "section",
        "volume",
        "act",
        "interlude"
    };

    public static List<LabeledChapter> LabelChapters(IReadOnlyList<FlatChapter> chapters)
    {
        var labeled = new List<LabeledChapter>(chapters.Count);
        var mainIndex = 0;
        var nonIndex = 0;

        foreach (var chapter in chapters)
        {
            var title = chapter.Title?.Trim() ?? string.Empty;
            var normalized = NormalizeTitle(title);
            var isFrontMatter = IsFrontMatterTitle(normalized) || IsLikelyTableOfContents(chapter.PlainText);
            var isStructural = IsStructuralHeading(normalized);
            var isExplicitChapter = HasChapterIndicator(title);
            var isMain = !isFrontMatter && (isExplicitChapter || (!isStructural && IsLikelyMainContent(chapter)));

            string displayLabel;
            if (isMain)
            {
                mainIndex++;
                displayLabel = BuildMainLabel(mainIndex, title);
            }
            else
            {
                nonIndex++;
                displayLabel = BuildNonMainLabel(nonIndex, title);
            }

            labeled.Add(new LabeledChapter(chapter, displayLabel, isMain));
        }

        return labeled;
    }

    private static bool IsFrontMatterTitle(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        return FrontMatterKeywords.Any(keyword => normalizedTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsStructuralHeading(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        return StructuralKeywords.Any(keyword =>
            Regex.IsMatch(normalizedTitle, $@"\b{Regex.Escape(keyword)}\b", RegexOptions.IgnoreCase));
    }

    private static bool HasChapterIndicator(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        if (Regex.IsMatch(title, @"\bchapter\b", RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(title, @"^\s*\d+[\.\:\-\s]", RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(title, @"^\s*[ivxlcdm]+[\.\:\-\s]", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    private static bool IsLikelyMainContent(FlatChapter chapter)
    {
        if (chapter.WordCount >= 350)
            return true;

        return chapter.Level == 0 && chapter.WordCount >= 200;
    }

    private static bool IsLikelyTableOfContents(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        if (Regex.IsMatch(content, @"\b(table of contents|contents)\b", RegexOptions.IgnoreCase))
            return true;

        var lines = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 2)
            .ToList();

        if (lines.Count < 5)
            return false;

        var dotLeaderLines = lines.Count(l => l.Contains("...") || Regex.IsMatch(l, @"\.{2,}\s*\d+$"));
        var chapterLines = lines.Count(l => Regex.IsMatch(l, @"\bchapter\b", RegexOptions.IgnoreCase));
        var numericLines = lines.Count(l => Regex.IsMatch(l, @"\b\d+\b"));
        var shortLines = lines.Count(l => l.Length <= 60);

        var score = 0;
        if (dotLeaderLines >= Math.Max(3, lines.Count / 4)) score++;
        if (chapterLines >= 3) score++;
        if (numericLines >= lines.Count / 2) score++;
        if (shortLines >= (int)(lines.Count * 0.7)) score++;

        return score >= 2;
    }

    private static string BuildMainLabel(int chapterNumber, string title)
    {
        var cleaned = CleanChapterTitle(title);
        if (string.IsNullOrWhiteSpace(cleaned))
            return $"Chapter {chapterNumber}";

        return $"Chapter {chapterNumber}: {cleaned}";
    }

    private static string BuildNonMainLabel(int index, string title)
    {
        var cleaned = CleanNonChapterTitle(title);
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "Front matter";

        return $"{ToRoman(index).ToLowerInvariant()}. {cleaned}";
    }

    private static string CleanChapterTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var cleaned = title.Trim();
        cleaned = Regex.Replace(cleaned, @"^(chapter|chap\.?)\s+\d+[\.\:\-\s]*", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"^(chapter|chap\.?)\s+[ivxlcdm]+[\.\:\-\s]*", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"^\d+[\.\:\-\s]+", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"^[ivxlcdm]+[\.\:\-\s]+", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"^(book|part|section)\s+[ivxlcdm\d]+[\.\:\-\s]*", "", RegexOptions.IgnoreCase).Trim();
        return cleaned;
    }

    private static string CleanNonChapterTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        return Regex.Replace(title.Trim(), @"\s+", " ");
    }

    private static string NormalizeTitle(string title) =>
        Regex.Replace(title.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string ToRoman(int number)
    {
        if (number <= 0)
            return "i";

        var map = new[]
        {
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        };

        var result = new StringBuilder();
        var remaining = number;

        foreach (var (value, symbol) in map)
        {
            while (remaining >= value)
            {
                result.Append(symbol);
                remaining -= value;
            }
        }

        return result.ToString();
    }
}

static class ChapterLabelingHelper
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LabelLocks = new();
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static async Task<CachedChapterIndex> EnsureGptChapterLabelsAsync(
        CachedChapterIndex index,
        string cacheDir,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        CancellationToken cancellationToken)
    {
        var model = cfg["OpenAI:ChapterLabelModel"] ?? "gpt-4o";
        if (string.Equals(index.LabelSource, model, StringComparison.OrdinalIgnoreCase) &&
            index.Chapters.All(ch => !string.IsNullOrWhiteSpace(ch.DisplayLabel) && ch.IsMainChapter != null))
        {
            return index;
        }

        var gate = LabelLocks.GetOrAdd(cacheDir, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var metaPath = Path.Combine(cacheDir, "metadata.json");
            if (File.Exists(metaPath))
            {
                var existingJson = await File.ReadAllTextAsync(metaPath, cancellationToken);
                var cached = JsonSerializer.Deserialize<CachedChapterIndex>(existingJson, CacheJsonOptions);
                if (cached != null &&
                    string.Equals(cached.LabelSource, model, StringComparison.OrdinalIgnoreCase) &&
                    cached.Chapters.All(ch => !string.IsNullOrWhiteSpace(ch.DisplayLabel) && ch.IsMainChapter != null))
                {
                    return cached;
                }
            }

            var labeled = await RequestGptLabelsAsync(index.Chapters, model, httpFactory, cfg, modelHelper, aiResponseParser, cancellationToken);
            if (labeled == null || labeled.Count == 0)
            {
                // Fallback to heuristic labeling when GPT fails
                var fallback = ChapterLabeler.LabelChapters(index.Chapters
                    .Select(ch => new FlatChapter(ch.Id, ch.Title, ch.Level, string.Empty, ch.WordCount))
                    .ToList());

                labeled = fallback.ToDictionary(ch => ch.Chapter.Id, ch => new ChapterLabelResult(
                    ch.Chapter.Id,
                    ch.DisplayLabel,
                    ch.IsMainChapter));
            }

            var updatedChapters = index.Chapters.Select(ch =>
            {
                if (labeled.TryGetValue(ch.Id, out var label) && !string.IsNullOrWhiteSpace(label.DisplayLabel))
                {
                    return ch with { DisplayLabel = label.DisplayLabel, IsMainChapter = label.IsMainChapter };
                }
                return ch;
            }).ToList();

            var updatedIndex = index with { Chapters = updatedChapters, LabelSource = model };
            var metaJson = JsonSerializer.Serialize(updatedIndex, CacheJsonOptions);
            await File.WriteAllTextAsync(metaPath, metaJson, cancellationToken);
            return updatedIndex;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<Dictionary<int, ChapterLabelResult>?> RequestGptLabelsAsync(
        IReadOnlyList<CachedChapterMeta> chapters,
        string model,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        CancellationToken cancellationToken)
    {
        var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("❌ OpenAI API key not configured for chapter labeling.");
            return null;
        }

        using var http = httpFactory.CreateClient("OpenAI");

        var chapterPayload = chapters.Select(ch => new
        {
            id = ch.Id,
            title = ch.Title,
            wordCount = ch.WordCount
        }).ToList();

        var systemPrompt = @"You label ebook chapter lists. Return ONLY valid JSON, no markdown.
Use the provided chapter titles and word counts to produce a clean display label and whether it's a main chapter.";

        var userPrompt = $@"Input chapters (in reading order):
{JsonSerializer.Serialize(chapterPayload)}

Rules:
- Preserve ids exactly; do not reorder.
- Main chapters should be numbered sequentially: ""Chapter 1: Title"", ""Chapter 2: Title"".
- If no title is provided, use ""Chapter N"" for main chapters.
- Non-chapters (contents, preface, index, maps, acknowledgments, etc.) should use lowercase roman numerals: ""i. Preface"", ""ii. Table of Contents"".
- If a title already contains a chapter number, remove the number and keep the clean title.
- Use wordCount as a hint: very short sections are likely non-chapters.

Return ONLY this JSON array:
[
  {{
    ""id"": 1,
    ""displayLabel"": ""Chapter 1: Title"",
    ""isMainChapter"": true
  }}
]";

        var payload = modelHelper.BuildChatCompletionPayload(
            model,
            new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            maxCompletionTokens: 2000,
            temperature: 0.2
        );

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine($"❌ OpenAI chapter-labeling failed status={(int)response.StatusCode} body={body}");
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var rawText = aiResponseParser.ExtractText(doc.RootElement);

        if (string.IsNullOrWhiteSpace(rawText))
            return null;

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var parsed = JsonSerializer.Deserialize<List<ChapterLabelResult>>(rawText, options);
            if (parsed == null || parsed.Count == 0)
                return null;

            return parsed
                .GroupBy(p => p.Id)
                .ToDictionary(g => g.Key, g => g.First());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to parse chapter labels JSON: {ex.Message}");
            return null;
        }
    }
}
record SummarizeRequest(string Text, string? BookTitle, string? Author, int? Year, string? Premise, string? DropboxPath, int? ChapterId, int? WordOffset, List<string>? KnownWords);
record LearnMoreRequest(string Term, string? Definition, string? DropboxPath, string? BookTitle, string? Context);
record LearnMoreResponse(string Detail);
record FlashcardRequest(string Term, string? Definition, string? DropboxPath, string? BookTitle, string? Context, List<string>? KnownWords, bool? SaveToLibrary);
// FlashcardItem is now defined in AnnasArchive.Core.Services.IFlashcardService
public record FlashcardResult(List<FlashcardItem> Cards);
record WikiImagesResponse(List<string> Images);
record SummarizeResponse(string Summary);
record SuggestAuthorsRequest(string BookTitle);
record SuggestAuthorsResponse(List<AuthorSuggestion> Authors);
record AuthorSuggestion(string Author, string Confidence);
record RelatedBooksRequest(string BookTitle, string Author);
record RelatedBooksResponse(List<SeriesBook> SameSeries, List<AuthorSeries> OtherSeries, string? SeriesSummary);
record SeriesBook(string Title, int Order, string Description, string? CoverUrl);
record AuthorSeries(string SeriesName, int BookCount, List<SeriesBook> Books, string Description, string Summary);
record AiBookSearchRequest(string Query);
record AiBookSearchItem(string Title, string Author, string Summary, string Importance, string? CoverUrl);
record AiBookSearchResponse(string? Summary, List<AiBookSearchItem> Books);
record LibraryBookMeta(
    string? Title,
    string[]? Authors,
    string? Format,
    string? FileSize,
    string FileName,
    string? CoverUrl,
    string? Source,
    string? Md5,
    DateTime? SavedAt,
    string? PrimaryGenre,
    string[]? Tags,
    string? Series,
    string[]? Genres,
    string? PublishedDate,
    string? Pages,
    double? GoodreadsRating,
    int? PersonalRating,
    bool? ReaderEnabled)
{
    public string? Title { get; set; } = Title;
    public string[]? Authors { get; set; } = Authors;
    public string? PrimaryGenre { get; set; } = PrimaryGenre;
    public string[]? Tags { get; set; } = Tags;
    public string? Series { get; set; } = Series;
    public double? GoodreadsRating { get; set; } = GoodreadsRating;
    public int? PersonalRating { get; set; } = PersonalRating;
    public bool? ReaderEnabled { get; set; } = ReaderEnabled;
}
record LibraryBookMetadataUpdate(
    string PrimaryGenre,
    string[]? Tags,
    string? Series,
    string? Title,
    string[]? Authors);
record LibraryBookRatingsUpdate(
    double? GoodreadsRating,
    int? PersonalRating);
record LibraryBookReaderUpdate(
    bool? Enabled);
record LibraryBookCoverUpdate(
    string CoverUrl);
record ReaderBookDto(
    string FileName,
    string ReaderKey,
    string Title,
    string[] Authors,
    string Format,
    string? CoverUrl,
    bool HasSummaries);
record LibraryReaderIndexRequest(
    string FileName);
record LibraryBookDto(
    string Title,
    string[] Authors,
    string Format,
    string FileSize,
    string FileName,
    string? CoverUrl,
    string? Source,
    string? Md5,
    DateTime? SavedAt,
    string? PrimaryGenre,
    string[] Tags,
    string? Series,
    string[] Genres,
    string? PublishedDate,
    string? Pages,
    double? GoodreadsRating,
    int? PersonalRating,
    bool? ReaderEnabled);
record MatchSeriesBooksRequest(string? SeriesName, string Author, string? PreferredFormat, List<BookWithCandidates> Books);
record BookWithCandidates(string Title, int Order, List<CandidateBook> Candidates);
record CandidateBook(string Md5, string Title, List<string> Authors, string Format, string FileSize);
record SeriesBookMatch(string BookTitle, int Order, string Status, string? SelectedMd5, string? SelectedTitle, string Confidence, string Reason);
record MatchSeriesBooksResponse(List<SeriesBookMatch> Matches);
record FullChapterSummaryRequest(string DropboxPath, int ChapterId, string? BookTitle, string? Author, int? Year, string? Premise, bool ForceRegenerate = false);
record UltraChapterSummaryRequest(string DropboxPath, int ChapterId, string? BookTitle, string? Author, int? Year, string? Premise, bool ForceRegenerate = false);
record FullChapterSummaryResponse(string Summary, int PromptTokens, int CompletionTokens, int TotalTokens, double? AllowanceUsedPercent, long? TokensRemaining, DateTime CachedAt, List<ProcessingStep> Steps);
record ProcessingStep(string Stage, int StepNumber, int TotalSteps, string Message, bool Success, string? Error);
record ChapterSummaryCacheResponse(string Summary, int PromptTokens, int CompletionTokens, int TotalTokens, DateTime CachedAt);
record TokenUsageResponse(long PromptTokens, long CompletionTokens, long TotalTokens, long? Allowance, double? AllowanceUsedPercent, long? TokensRemaining, DateTime? ResetsAtUtc, double? TotalCostUsd);

// ─── Chunk/Section boundary models ──────────────────────────────────────────
public record ChunkBoundary(int Start, int End, int WordCount);
public record ChunkBoundariesResponse(int ChapterId, List<ChunkBoundary> Chunks, DateTime CachedAt);
public record SectionSummaryRequest(string DropboxPath, int ChapterId, int SectionIndex, string? BookTitle, string? Author);
public record SectionSummaryResponse(string Summary, int SectionIndex, int PromptTokens, int CompletionTokens, int TotalTokens, DateTime CachedAt, List<FlashcardItem>? Vocab = null);
public record SaveSectionVocabRequest(string DropboxPath, int ChapterId, int SectionIndex, List<FlashcardItem> Vocab);

// ─── Character Graph models ──────────────────────────────────────────────
public record CharacterGraphRequest(string DropboxPath, string? BookTitle, string? Context);
public record CharacterGraphUpdateRequest(string DropboxPath, string NewContent);
public record CharacterNode(string Id, string Label, string Description, string? DetailedDescription);
public record CharacterEdge(string From, string To, string Label, string? DetailedDescription);
public record CharacterGraphResponse(List<CharacterNode> Nodes, List<CharacterEdge> Edges, int SummaryCount, DateTime CachedAt);

// ─── Adapter for EPUB Cache Path Provider ───────────────────────────────
class EpubCachePathProviderAdapter : IEpubCachePathProvider
{
    public string GetCacheRoot() => DropboxEpubCache.GetCacheRoot();
    public string ComputeHash(string value) => DropboxEpubCache.ComputeHashPublic(value);
}

// ─── Helper class for Dropbox EPUB caching ──────────────────────────────
static class DropboxEpubCache
{
    private static readonly string EpubCacheRoot = ResolveCacheRoot();
    private static readonly ConcurrentDictionary<string, Task> CacheBuildTasks = new();
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static string ResolveCacheRoot()
    {
        var env = Environment.GetEnvironmentVariable("EPUB_CACHE_ROOT");
        if (!string.IsNullOrWhiteSpace(env))
        {
            Directory.CreateDirectory(env);
            return env;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fallback = Path.Combine(home, ".annas-archive", "epub-cache");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    public static async Task<List<DropboxEpubFileDto>> ListDropboxEpubsAsync(
        DropboxClient dropbox,
        string folderPath)
    {
        var epubs = new List<DropboxEpubFileDto>();

        async Task ListLoop(ListFolderResult result)
        {
            foreach (var entry in result.Entries.OfType<FileMetadata>())
            {
                if (!entry.Name.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
                    continue;

                epubs.Add(new DropboxEpubFileDto(
                    entry.Id,
                    entry.Name,
                    entry.PathDisplay ?? entry.PathLower ?? entry.Name,
                    (long)entry.Size,
                    entry.ServerModified));
            }

            if (result.HasMore)
            {
                var next = await dropbox.Files.ListFolderContinueAsync(result.Cursor);
                await ListLoop(next);
            }
        }

        var initial = await dropbox.Files.ListFolderAsync(
            new ListFolderArg(folderPath ?? string.Empty, recursive: true));
        await ListLoop(initial);

        return epubs
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<(CachedChapterIndex Index, string CacheDir)> GetOrBuildChapterIndexAsync(
        DropboxClient dropbox,
        string dropboxPath)
    {
        Directory.CreateDirectory(EpubCacheRoot);
        var cacheDir = Path.Combine(EpubCacheRoot, ComputeHash(dropboxPath));
        Directory.CreateDirectory(cacheDir);

        var metaPath = Path.Combine(cacheDir, "metadata.json");

        if (File.Exists(metaPath))
        {
            var cached = await TryReadIndex(metaPath);
            if (cached != null)
            {
                _ = CacheBuildTasks.GetOrAdd(cacheDir, _ => BuildCacheInternalAsync(dropbox, dropboxPath, cacheDir));
                return (cached, cacheDir);
            }
        }

        await CacheBuildTasks.GetOrAdd(cacheDir, _ => BuildCacheInternalAsync(dropbox, dropboxPath, cacheDir));
        var fresh = await TryReadIndex(metaPath)
            ?? throw new InvalidOperationException("Failed to read chapter index after build.");

        CacheBuildTasks.TryRemove(cacheDir, out _);
        return (fresh, cacheDir);
    }

    public static Task EnsureCacheBuildAsync(DropboxClient dropbox, string dropboxPath, string cacheDir) =>
        CacheBuildTasks.GetOrAdd(cacheDir, _ => BuildCacheInternalAsync(dropbox, dropboxPath, cacheDir));

    public static async Task<DropboxCacheStatusDto> GetCacheStatusAsync(
        DropboxClient dropbox,
        string dropboxPath)
    {
        Directory.CreateDirectory(EpubCacheRoot);
        var cacheDir = Path.Combine(EpubCacheRoot, ComputeHash(dropboxPath));
        var metaPath = Path.Combine(cacheDir, "metadata.json");
        var errorPath = Path.Combine(cacheDir, "error.txt");
        var inProgress = CacheBuildTasks.ContainsKey(cacheDir);
        var error = File.Exists(errorPath) ? await File.ReadAllTextAsync(errorPath) : null;

        if (!File.Exists(metaPath))
        {
            // Kick off background build if not present
            _ = CacheBuildTasks.GetOrAdd(cacheDir, _ => BuildCacheInternalAsync(dropbox, dropboxPath, cacheDir));
            return new DropboxCacheStatusDto(false, true, 0, 0, 0, null, error);
        }

        var meta = await TryReadIndex(metaPath);
        if (meta == null)
            return new DropboxCacheStatusDto(false, inProgress, 0, 0, 0, null, error);

        var total = meta.Chapters.Count;
        var cached = meta.Chapters.Count(ch => File.Exists(Path.Combine(cacheDir, ch.FileName)));
        var percent = total == 0 ? 0 : Math.Round((double)cached / total * 100, 2);

        return new DropboxCacheStatusDto(true, inProgress, total, cached, percent, meta.CachedAt, error);
    }

    public static bool DeleteCache(string dropboxPath)
    {
        Directory.CreateDirectory(EpubCacheRoot);
        var cacheDir = Path.Combine(EpubCacheRoot, ComputeHash(dropboxPath));
        try
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
                CacheBuildTasks.TryRemove(cacheDir, out _);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<List<DropboxSearchMatchDto>> SearchAsync(
        DropboxClient dropbox,
        string dropboxPath,
        string query)
    {
        var (index, cacheDir) = await GetOrBuildChapterIndexAsync(dropbox, dropboxPath);
        await EnsureCacheBuildAsync(dropbox, dropboxPath, cacheDir);

        var normalizedQuery = query.Trim();
        var results = new List<DropboxSearchMatchDto>();

        foreach (var chapter in index.Chapters)
        {
            var chapterPath = Path.Combine(cacheDir, chapter.FileName);
            if (!File.Exists(chapterPath)) continue;

            var content = await File.ReadAllTextAsync(chapterPath);
            var matches = Regex.Matches(content, Regex.Escape(normalizedQuery), RegexOptions.IgnoreCase);
            if (matches.Count == 0) continue;

            var first = matches[0];
            var start = Math.Max(0, first.Index - 80);
            var end = Math.Min(content.Length, first.Index + normalizedQuery.Length + 120);
            var snippet = content[start..end];
            snippet = Regex.Replace(snippet, @"\s+", " ").Trim();

            results.Add(new DropboxSearchMatchDto(
                chapter.Id,
                chapter.Title,
                matches.Count,
                first.Index,
                snippet));
        }

        return results
            .OrderByDescending(r => r.MatchCount)
            .ThenBy(r => r.ChapterId)
            .ToList();
    }

    private static async Task BuildCacheInternalAsync(DropboxClient dropbox, string dropboxPath, string cacheDir)
    {
        var errorPath = Path.Combine(cacheDir, "error.txt");
        try
        {
            var download = await dropbox.Files.DownloadAsync(dropboxPath);
            await using var dropboxStream = await download.GetContentAsStreamAsync();
            using var ms = new MemoryStream();
            await dropboxStream.CopyToAsync(ms);
            var book = await ReadBookWithFallbackAsync(ms.ToArray(), $"dropbox:{dropboxPath}");
            var flatChapters = FlattenChapters(book)
                .Where(ch => ch.WordCount >= 50)
                .ToList();

            var chapterMetas = new List<CachedChapterMeta>();
            foreach (var ch in flatChapters)
            {
                var fileName = $"chapter-{ch.Id:D4}.txt";
                var chapterPath = Path.Combine(cacheDir, fileName);
                await File.WriteAllTextAsync(chapterPath, ch.PlainText);

                chapterMetas.Add(new CachedChapterMeta(
                    ch.Id,
                    ch.Title,
                    ch.Level,
                    ch.PlainText.Length,
                    ch.WordCount,
                    fileName,
                    null,
                    null));
            }

            var meta = new CachedChapterIndex(
                dropboxPath,
                string.IsNullOrWhiteSpace(book.Title)
                    ? Path.GetFileNameWithoutExtension(dropboxPath)
                    : book.Title,
                DateTime.UtcNow,
                chapterMetas);

            var metaJson = JsonSerializer.Serialize(meta, CacheJsonOptions);
            await File.WriteAllTextAsync(Path.Combine(cacheDir, "metadata.json"), metaJson);
            if (File.Exists(errorPath))
                File.Delete(errorPath);
        }
        catch (Exception ex)
        {
            var message = $"[dropbox] Failed to build EPUB cache for {dropboxPath}: {ex}";
            Console.WriteLine(message);
            await File.WriteAllTextAsync(errorPath, message);
            throw;
        }
        finally
        {
            CacheBuildTasks.TryRemove(cacheDir, out _);
        }
    }

    private static async Task<EpubBook> ReadBookWithFallbackAsync(byte[] sourceBytes, string label)
    {
        var workingBytes = sourceBytes;
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coverFallbackApplied = false;
        var zipRepairAttempted = false;

        for (var attempt = 0; attempt < 3; attempt += 1)
        {
            using var source = new MemoryStream(workingBytes);
            try
            {
                return await EpubReader.ReadBookAsync(source);
            }
            catch (InvalidDataException)
            {
                if (!zipRepairAttempted)
                {
                    var repaired = TryRepairZip(workingBytes);
                    if (repaired != null)
                    {
                        Console.WriteLine($"[epub] Repaired zip structure for {label}");
                        workingBytes = repaired;
                        zipRepairAttempted = true;
                        continue;
                    }
                }
                throw;
            }
            catch (EpubContentException ex)
            {
                var missingPath = ExtractMissingEpubPath(ex.Message);
                if (string.IsNullOrWhiteSpace(missingPath) || added.Contains(missingPath))
                {
                    if (!coverFallbackApplied)
                    {
                        workingBytes = EnsureCommonCoverEntries(workingBytes);
                        coverFallbackApplied = true;
                        continue;
                    }
                    throw;
                }

                Console.WriteLine($"[epub] Missing content '{missingPath}' in {label}. Injecting placeholder.");
                workingBytes = EnsureZipEntry(workingBytes, missingPath);
                added.Add(missingPath);
            }
        }

        throw new InvalidOperationException($"Failed to parse EPUB after fallback attempts: {label}");
    }

    private static async Task<(string Title, List<FlatChapter> Chapters)> GetFlatChaptersAsync(
        byte[] sourceBytes,
        string label,
        string filePath)
    {
        try
        {
            var book = await ReadBookWithFallbackAsync(sourceBytes, label);
            var chapters = FlattenChapters(book).ToList();
            var title = string.IsNullOrWhiteSpace(book.Title)
                ? Path.GetFileNameWithoutExtension(filePath)
                : book.Title;
            return (title, chapters);
        }
        catch (Exception ex)
        {
            var fallback = TryBuildChaptersFromZipBytes(sourceBytes, label);
            if (fallback != null && fallback.Value.Chapters.Count > 0)
            {
                var title = string.IsNullOrWhiteSpace(fallback.Value.Title)
                    ? Path.GetFileNameWithoutExtension(filePath)
                    : fallback.Value.Title!;
                return (title, fallback.Value.Chapters);
            }

            throw new InvalidOperationException($"Failed to parse EPUB after tolerant fallback: {label}", ex);
        }
    }

    private static (string? Title, List<FlatChapter> Chapters)? TryBuildChaptersFromZipBytes(
        byte[] sourceBytes,
        string label)
    {
        if (!TryReadZipEntries(sourceBytes, label, out var entries, out var opfPath))
        {
            var repaired = TryRepairZip(sourceBytes);
            if (repaired == null || !TryReadZipEntries(repaired, label, out entries, out opfPath))
                return null;
        }

        if (entries.Count == 0)
            return null;

        string? bookTitle = null;
        List<string> orderedHtml = new();

        if (!string.IsNullOrWhiteSpace(opfPath) && entries.TryGetValue(opfPath, out var opfBytes))
        {
            try
            {
                var opfText = ReadTextFromBytes(opfBytes);
                var opfDir = NormalizeZipDir(Path.GetDirectoryName(opfPath) ?? string.Empty);
                var doc = XDocument.Parse(opfText);

                bookTitle = doc.Descendants()
                    .FirstOrDefault(el => el.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase))
                    ?.Value
                    ?.Trim();

                var items = doc.Descendants()
                    .Where(el => el.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase))
                    .Select(el => new
                    {
                        Id = el.Attribute("id")?.Value,
                        Href = el.Attribute("href")?.Value
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
                    .ToDictionary(
                        item => item.Id!,
                        item => NormalizeZipPath(ResolveOpfHref(opfDir, item.Href!)),
                        StringComparer.OrdinalIgnoreCase);

                var spine = doc.Descendants()
                    .Where(el => el.Name.LocalName.Equals("itemref", StringComparison.OrdinalIgnoreCase))
                    .Select(el => el.Attribute("idref")?.Value)
                    .Where(idref => !string.IsNullOrWhiteSpace(idref))
                    .Select(idref => items.TryGetValue(idref!, out var href) ? href : null)
                    .Where(href => !string.IsNullOrWhiteSpace(href))
                    .Select(href => FindEntry(entries, href!))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                orderedHtml = spine;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[epub] Failed to parse OPF for tolerant fallback ({label}): {ex.Message}");
            }
        }

        if (orderedHtml.Count == 0)
        {
            orderedHtml = entries.Keys
                .Where(IsHtmlEntry)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var chapters = new List<FlatChapter>();
        var index = 0;
        foreach (var path in orderedHtml)
        {
            if (!entries.TryGetValue(path, out var data))
                continue;

            var html = ReadTextFromBytes(data);
            var text = HtmlToPlainText(html);
            var words = CountWords(text);
            var title = ExtractTitleFromHtml(html) ?? Path.GetFileNameWithoutExtension(path);
            chapters.Add(new FlatChapter(index++, title, 0, text, words));
        }

        if (chapters.Count == 0)
            return null;

        Console.WriteLine($"[epub] Tolerant fallback used for {label}. Chapters={chapters.Count}");
        return (bookTitle, chapters);
    }

    private static bool TryReadZipEntries(
        byte[] sourceBytes,
        string label,
        out Dictionary<string, byte[]> entries,
        out string? opfPath)
    {
        entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        opfPath = null;

        try
        {
            using var inputStream = new MemoryStream(sourceBytes);
            using var zipInput = new ZipInputStream(inputStream);
            ZipEntry? entry;
            while ((entry = zipInput.GetNextEntry()) != null)
            {
                if (!entry.IsFile) continue;
                var name = NormalizeZipPath(entry.Name);
                if (!IsHtmlEntry(name) && !name.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var buffer = new MemoryStream();
                zipInput.CopyTo(buffer);
                entries[name] = buffer.ToArray();
                if (opfPath == null && name.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
                    opfPath = name;
            }

            return entries.Count > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[epub] Failed to read zip entries for tolerant fallback ({label}): {ex.Message}");
            return false;
        }
    }

    private static string NormalizeZipPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path.Replace('\\', '/').TrimStart('/');
        var fragmentIndex = normalized.IndexOf('#');
        if (fragmentIndex >= 0)
            normalized = normalized[..fragmentIndex];
        return normalized;
    }

    private static string NormalizeZipDir(string path)
    {
        var normalized = NormalizeZipPath(path);
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized.TrimEnd('/');
    }

    private static string ResolveOpfHref(string opfDir, string href)
    {
        var decoded = Uri.UnescapeDataString(href);
        decoded = decoded.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(opfDir))
            return decoded;
        return $"{opfDir}/{decoded}";
    }

    private static string? FindEntry(Dictionary<string, byte[]> entries, string href)
    {
        var normalized = NormalizeZipPath(href);
        if (entries.ContainsKey(normalized))
            return normalized;

        var match = entries.Keys.FirstOrDefault(key =>
            key.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase) ||
            key.EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
        return match;
    }

    private static bool IsHtmlEntry(string path)
    {
        return path.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadTextFromBytes(byte[] data)
    {
        using var stream = new MemoryStream(data);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string? ExtractTitleFromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var titleMatch = Regex.Match(html, @"<title[^>]*>(?<t>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (titleMatch.Success)
        {
            var title = WebUtility.HtmlDecode(titleMatch.Groups["t"].Value).Trim();
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }

        var h1Match = Regex.Match(html, @"<h1[^>]*>(?<t>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (h1Match.Success)
        {
            var title = WebUtility.HtmlDecode(h1Match.Groups["t"].Value).Trim();
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }

        var h2Match = Regex.Match(html, @"<h2[^>]*>(?<t>.*?)</h2>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (h2Match.Success)
        {
            var title = WebUtility.HtmlDecode(h2Match.Groups["t"].Value).Trim();
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }

        return null;
    }

    private static byte[]? TryRepairZip(byte[] sourceBytes)
    {
        try
        {
            using var inputStream = new MemoryStream(sourceBytes);
            using var zipInput = new ZipInputStream(inputStream);
            var entries = new List<(string Name, byte[] Data)>();
            ZipEntry? entry;
            while ((entry = zipInput.GetNextEntry()) != null)
            {
                if (!entry.IsFile) continue;
                using var buffer = new MemoryStream();
                zipInput.CopyTo(buffer);
                entries.Add((entry.Name, buffer.ToArray()));
            }

            if (entries.Count == 0)
                return null;

            var mimeEntry = entries.FirstOrDefault(e => string.Equals(e.Name, "mimetype", StringComparison.OrdinalIgnoreCase));
            var others = entries.Where(e => !string.Equals(e.Name, "mimetype", StringComparison.OrdinalIgnoreCase)).ToList();

            using var outputStream = new MemoryStream();
            using var zipOutput = new ZipOutputStream(outputStream);
            zipOutput.SetLevel(6);

            if (!string.IsNullOrEmpty(mimeEntry.Name))
            {
                var crc32 = new ICSharpCode.SharpZipLib.Checksum.Crc32();
                crc32.Update(mimeEntry.Data);
                var mimeZipEntry = new ZipEntry("mimetype")
                {
                    CompressionMethod = CompressionMethod.Stored,
                    Size = mimeEntry.Data.Length,
                    CompressedSize = mimeEntry.Data.Length,
                    Crc = crc32.Value
                };
                zipOutput.PutNextEntry(mimeZipEntry);
                zipOutput.Write(mimeEntry.Data, 0, mimeEntry.Data.Length);
                zipOutput.CloseEntry();
            }

            foreach (var item in others)
            {
                var newEntry = new ZipEntry(item.Name)
                {
                    CompressionMethod = CompressionMethod.Deflated
                };
                zipOutput.PutNextEntry(newEntry);
                zipOutput.Write(item.Data, 0, item.Data.Length);
                zipOutput.CloseEntry();
            }

            zipOutput.Finish();
            return outputStream.ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[epub] Zip repair failed: {ex.Message}");
            return null;
        }
    }

    private static byte[] EnsureCommonCoverEntries(byte[] sourceBytes)
    {
        var paths = new[]
        {
            "OEBPS/Images/Cover.png",
            "OEBPS/Images/cover.png",
            "OEBPS/Images/cover.jpg",
            "OEBPS/Cover.png",
            "OEBPS/cover.jpg",
            "cover.png",
            "cover.jpg"
        };

        var updated = sourceBytes;
        foreach (var path in paths)
        {
            updated = EnsureZipEntry(updated, path);
        }
        return updated;
    }

    private static string? ExtractMissingEpubPath(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var patterns = new[]
        {
            "file\\s+[\"“”'](?<path>[^\"“”']+)[\"“”']\\s+was not found",
            "file\\s+(?<path>[^\\s]+)\\s+was not found"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups["path"].Value;
        }

        var fallback = Regex.Match(message, @"(?<path>OEBPS/[^""\s]+)", RegexOptions.IgnoreCase);
        return fallback.Success ? fallback.Groups["path"].Value : null;
    }

    private static byte[] EnsureZipEntry(byte[] sourceBytes, string entryPath)
    {
        try
        {
            using var stream = new MemoryStream(sourceBytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);
            if (archive.GetEntry(entryPath) != null)
                return sourceBytes;

            var entry = archive.CreateEntry(entryPath);
            using var entryStream = entry.Open();
            entryStream.Flush();
            return stream.ToArray();
        }
        catch (InvalidDataException ex)
        {
            Console.WriteLine($"[epub] Invalid zip structure while adding '{entryPath}': {ex.Message}");
            return sourceBytes;
        }
    }

    private static async Task<CachedChapterIndex?> TryReadIndex(string metaPath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(metaPath);
            return JsonSerializer.Deserialize<CachedChapterIndex>(json, CacheJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<FlatChapter> FlattenChapters(EpubBook book)
    {
        var results = new List<FlatChapter>();
        var index = 0;

        if (book.Navigation != null && book.Navigation.Any())
        {
            void Walk(IEnumerable<EpubNavigationItem> items, int level)
            {
            foreach (var nav in items)
            {
                if (nav.Type == EpubNavigationItemType.LINK && nav.HtmlContentFile != null)
                {
                    var currentId = index++;
                    var title = string.IsNullOrWhiteSpace(nav.Title)
                        ? $"Chapter {currentId + 1}"
                        : nav.Title.Trim();

                    var text = HtmlToPlainText(nav.HtmlContentFile.Content);
                    var words = CountWords(text);

                    results.Add(new FlatChapter(currentId, title, level, text, words));
                }

                    if (nav.NestedItems?.Any() == true)
                        Walk(nav.NestedItems, level + 1);
                }
            }

            Walk(book.Navigation, 0);
        }

        if (results.Count == 0 && book.ReadingOrder != null && book.ReadingOrder.Any())
        {
            foreach (var file in book.ReadingOrder)
            {
                var currentId = index++;
                var title = string.IsNullOrWhiteSpace(file.FilePath)
                    ? $"Section {currentId + 1}"
                    : Path.GetFileNameWithoutExtension(file.FilePath);

                var text = HtmlToPlainText(file.Content);
                var words = CountWords(text);

                results.Add(new FlatChapter(currentId, title, 0, text, words));
            }
        }

        return results;
    }

    private static string HtmlToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var cleaned = Regex.Replace(
            html,
            "<(script|style)[^>]*?>.*?</\\1>",
            string.Empty,
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        cleaned = Regex.Replace(
            cleaned,
            @"<(br|p|div|h[1-6]|li)[^>]*>",
            "\n\n",
            RegexOptions.IgnoreCase);

        cleaned = Regex.Replace(cleaned, "<[^>]+>", " ");

        var decoded = WebUtility.HtmlDecode(cleaned);
        decoded = decoded.Replace("\r", "");

        decoded = Regex.Replace(decoded, @"[ \t]+", " ");
        decoded = Regex.Replace(decoded, @"(\n\s*){3,}", "\n\n");
        decoded = decoded.Trim();

        return decoded.Trim();
    }

    private static string ComputeHash(string value)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(value);
        var hashBytes = sha.ComputeHash(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return Regex.Matches(text, @"\b\w+\b").Count;
    }

    public static string GetCacheRoot() => EpubCacheRoot;
    public static string ComputeHashPublic(string value) => ComputeHash(value);
}

// ─── Helper class for Library EPUB caching ──────────────────────────────
static class LibraryEpubCache
{
    private static readonly string EpubCacheRoot = ResolveCacheRoot();
    private static readonly ConcurrentDictionary<string, Task> CacheBuildTasks = new();
    private static readonly MemoryCache ChapterContentCache = new(new MemoryCacheOptions());
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static string ResolveCacheRoot()
    {
        var env = Environment.GetEnvironmentVariable("EPUB_CACHE_ROOT");
        if (!string.IsNullOrWhiteSpace(env))
        {
            Directory.CreateDirectory(env);
            return env;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fallback = Path.Combine(home, ".annas-archive", "epub-cache");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    public static async Task<(CachedChapterIndex Index, string CacheDir)> GetOrBuildChapterIndexAsync(
        string filePath,
        string readerKey)
    {
        Directory.CreateDirectory(EpubCacheRoot);
        var cacheDir = Path.Combine(EpubCacheRoot, ComputeHash(readerKey));
        Directory.CreateDirectory(cacheDir);

        var metaPath = Path.Combine(cacheDir, "metadata.json");

        if (File.Exists(metaPath))
        {
            var cached = await TryReadIndex(metaPath);
            if (cached != null)
            {
                _ = CacheBuildTasks.GetOrAdd(cacheDir, _ => BuildCacheInternalAsync(filePath, readerKey, cacheDir));
                return (cached, cacheDir);
            }
        }

        await CacheBuildTasks.GetOrAdd(cacheDir, _ => BuildCacheInternalAsync(filePath, readerKey, cacheDir));
        var fresh = await TryReadIndex(metaPath)
            ?? throw new InvalidOperationException("Failed to read chapter index after build.");

        CacheBuildTasks.TryRemove(cacheDir, out _);
        return (fresh, cacheDir);
    }

    public static async Task<(CachedChapterIndex Index, string CacheDir)> GetOrBuildChapterIndexQuickAsync(
        string filePath,
        string readerKey)
    {
        Directory.CreateDirectory(EpubCacheRoot);
        var cacheDir = Path.Combine(EpubCacheRoot, ComputeHash(readerKey));
        Directory.CreateDirectory(cacheDir);

        var metaPath = Path.Combine(cacheDir, "metadata.json");
        if (File.Exists(metaPath))
        {
            var cached = await TryReadIndex(metaPath);
            if (cached != null)
                return (cached, cacheDir);
        }

        await BuildIndexOnlyAsync(filePath, readerKey, cacheDir);
        var fresh = await TryReadIndex(metaPath)
            ?? throw new InvalidOperationException("Failed to read chapter index after quick build.");
        return (fresh, cacheDir);
    }

    public static Task EnsureCacheBuildAsync(string filePath, string readerKey, string cacheDir) =>
        CacheBuildTasks.GetOrAdd(cacheDir, _ => BuildCacheInternalAsync(filePath, readerKey, cacheDir));

    public static async Task<DropboxCacheStatusDto> GetCacheStatusAsync(
        string filePath,
        string readerKey)
    {
        Directory.CreateDirectory(EpubCacheRoot);
        var cacheDir = Path.Combine(EpubCacheRoot, ComputeHash(readerKey));
        var metaPath = Path.Combine(cacheDir, "metadata.json");
        var errorPath = Path.Combine(cacheDir, "error.txt");
        var inProgress = CacheBuildTasks.ContainsKey(cacheDir);
        var error = File.Exists(errorPath) ? await File.ReadAllTextAsync(errorPath) : null;

        if (!File.Exists(metaPath))
        {
            _ = CacheBuildTasks.GetOrAdd(cacheDir, _ => BuildCacheInternalAsync(filePath, readerKey, cacheDir));
            return new DropboxCacheStatusDto(false, true, 0, 0, 0, null, error);
        }

        var meta = await TryReadIndex(metaPath);
        if (meta == null)
            return new DropboxCacheStatusDto(false, inProgress, 0, 0, 0, null, error);

        var total = meta.Chapters.Count;
        var cached = meta.Chapters.Count(ch => File.Exists(Path.Combine(cacheDir, ch.FileName)));
        var percent = total == 0 ? 0 : Math.Round((double)cached / total * 100, 2);

        return new DropboxCacheStatusDto(true, inProgress, total, cached, percent, meta.CachedAt, error);
    }

    public static bool DeleteCache(string readerKey)
    {
        Directory.CreateDirectory(EpubCacheRoot);
        var cacheDir = Path.Combine(EpubCacheRoot, ComputeHash(readerKey));
        try
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
                CacheBuildTasks.TryRemove(cacheDir, out _);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<List<DropboxSearchMatchDto>> SearchAsync(
        string filePath,
        string readerKey,
        string query)
    {
        var (index, cacheDir) = await GetOrBuildChapterIndexAsync(filePath, readerKey);
        await EnsureCacheBuildAsync(filePath, readerKey, cacheDir);

        var normalizedQuery = query.Trim();
        var results = new List<DropboxSearchMatchDto>();

        foreach (var chapter in index.Chapters)
        {
            var chapterPath = Path.Combine(cacheDir, chapter.FileName);
            if (!File.Exists(chapterPath)) continue;

            var content = await File.ReadAllTextAsync(chapterPath);
            var matches = Regex.Matches(content, Regex.Escape(normalizedQuery), RegexOptions.IgnoreCase);
            if (matches.Count == 0) continue;

            var first = matches[0];
            var start = Math.Max(0, first.Index - 80);
            var end = Math.Min(content.Length, first.Index + normalizedQuery.Length + 120);
            var snippet = content[start..end];
            snippet = Regex.Replace(snippet, @"\s+", " ").Trim();

            results.Add(new DropboxSearchMatchDto(
                chapter.Id,
                chapter.Title,
                matches.Count,
                first.Index,
                snippet));
        }

        return results
            .OrderByDescending(r => r.MatchCount)
            .ThenBy(r => r.ChapterId)
            .ToList();
    }

    private static async Task BuildCacheInternalAsync(string filePath, string readerKey, string cacheDir)
    {
        var errorPath = Path.Combine(cacheDir, "error.txt");
        try
        {
            await using var fileStream = File.OpenRead(filePath);
            using var ms = new MemoryStream();
            await fileStream.CopyToAsync(ms);
            var (title, flatChapters) = await GetFlatChaptersAsync(ms.ToArray(), $"library:{filePath}", filePath);
            flatChapters = flatChapters
                .Where(ch => ch.WordCount >= 50)
                .ToList();

            var chapterMetas = new List<CachedChapterMeta>();
            foreach (var ch in flatChapters)
            {
                var fileName = $"chapter-{ch.Id:D4}.txt";
                var chapterPath = Path.Combine(cacheDir, fileName);
                await File.WriteAllTextAsync(chapterPath, ch.PlainText);

                chapterMetas.Add(new CachedChapterMeta(
                    ch.Id,
                    ch.Title,
                    ch.Level,
                    ch.PlainText.Length,
                    ch.WordCount,
                    fileName,
                    null,
                    null));
            }

            var meta = new CachedChapterIndex(
                readerKey,
                title,
                DateTime.UtcNow,
                chapterMetas);

            var metaJson = JsonSerializer.Serialize(meta, CacheJsonOptions);
            await File.WriteAllTextAsync(Path.Combine(cacheDir, "metadata.json"), metaJson);
            if (File.Exists(errorPath))
                File.Delete(errorPath);
        }
        catch (Exception ex)
        {
            var message = $"[library] Failed to build EPUB cache for {filePath}: {ex}";
            Console.WriteLine(message);
            await File.WriteAllTextAsync(errorPath, message);
            throw;
        }
        finally
        {
            CacheBuildTasks.TryRemove(cacheDir, out _);
        }
    }

    private static async Task<EpubBook> ReadBookWithFallbackAsync(byte[] sourceBytes, string label)
    {
        var workingBytes = sourceBytes;
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coverFallbackApplied = false;
        var zipRepairAttempted = false;

        for (var attempt = 0; attempt < 3; attempt += 1)
        {
            using var source = new MemoryStream(workingBytes);
            try
            {
                return await EpubReader.ReadBookAsync(source);
            }
            catch (InvalidDataException)
            {
                if (!zipRepairAttempted)
                {
                    var repaired = TryRepairZip(workingBytes);
                    if (repaired != null)
                    {
                        Console.WriteLine($"[epub] Repaired zip structure for {label}");
                        workingBytes = repaired;
                        zipRepairAttempted = true;
                        continue;
                    }
                }
                throw;
            }
            catch (EpubContentException ex)
            {
                var missingPath = ExtractMissingEpubPath(ex.Message);
                if (string.IsNullOrWhiteSpace(missingPath) || added.Contains(missingPath))
                {
                    if (!coverFallbackApplied)
                    {
                        workingBytes = EnsureCommonCoverEntries(workingBytes);
                        coverFallbackApplied = true;
                        continue;
                    }
                    throw;
                }

                Console.WriteLine($"[epub] Missing content '{missingPath}' in {label}. Injecting placeholder.");
                workingBytes = EnsureZipEntry(workingBytes, missingPath);
                added.Add(missingPath);
            }
        }

        throw new InvalidOperationException($"Failed to parse EPUB after fallback attempts: {label}");
    }


    private static async Task<(string Title, List<FlatChapter> Chapters)> GetFlatChaptersAsync(
        byte[] sourceBytes,
        string label,
        string filePath)
    {
        try
        {
            var book = await ReadBookWithFallbackAsync(sourceBytes, label);
            var chapters = FlattenChapters(book).ToList();
            var title = string.IsNullOrWhiteSpace(book.Title)
                ? Path.GetFileNameWithoutExtension(filePath)
                : book.Title;
            return (title, chapters);
        }
        catch (Exception ex)
        {
            var fallback = TryBuildChaptersFromZipBytes(sourceBytes, label);
            if (fallback != null && fallback.Value.Chapters.Count > 0)
            {
                var title = string.IsNullOrWhiteSpace(fallback.Value.Title)
                    ? Path.GetFileNameWithoutExtension(filePath)
                    : fallback.Value.Title!;
                return (title, fallback.Value.Chapters);
            }

            throw new InvalidOperationException($"Failed to parse EPUB after tolerant fallback: {label}", ex);
        }
    }

    private static (string? Title, List<FlatChapter> Chapters)? TryBuildChaptersFromZipBytes(
        byte[] sourceBytes,
        string label)
    {
        if (!TryReadZipEntries(sourceBytes, label, out var entries, out var opfPath))
        {
            var repaired = TryRepairZip(sourceBytes);
            if (repaired == null || !TryReadZipEntries(repaired, label, out entries, out opfPath))
                return null;
        }

        if (entries.Count == 0)
            return null;

        string? bookTitle = null;
        List<string> orderedHtml = new();

        if (!string.IsNullOrWhiteSpace(opfPath) && entries.TryGetValue(opfPath, out var opfBytes))
        {
            try
            {
                var opfText = ReadTextFromBytes(opfBytes);
                var opfDir = NormalizeZipDir(Path.GetDirectoryName(opfPath) ?? string.Empty);
                var doc = XDocument.Parse(opfText);

                bookTitle = doc.Descendants()
                    .FirstOrDefault(el => el.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase))
                    ?.Value
                    ?.Trim();

                var items = doc.Descendants()
                    .Where(el => el.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase))
                    .Select(el => new
                    {
                        Id = el.Attribute("id")?.Value,
                        Href = el.Attribute("href")?.Value
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
                    .ToDictionary(
                        item => item.Id!,
                        item => NormalizeZipPath(ResolveOpfHref(opfDir, item.Href!)),
                        StringComparer.OrdinalIgnoreCase);

                var spine = doc.Descendants()
                    .Where(el => el.Name.LocalName.Equals("itemref", StringComparison.OrdinalIgnoreCase))
                    .Select(el => el.Attribute("idref")?.Value)
                    .Where(idref => !string.IsNullOrWhiteSpace(idref))
                    .Select(idref => items.TryGetValue(idref!, out var href) ? href : null)
                    .Where(href => !string.IsNullOrWhiteSpace(href))
                    .Select(href => FindEntry(entries, href!))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                orderedHtml = spine;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[epub] Failed to parse OPF for tolerant fallback ({label}): {ex.Message}");
            }
        }

        if (orderedHtml.Count == 0)
        {
            orderedHtml = entries.Keys
                .Where(IsHtmlEntry)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var chapters = new List<FlatChapter>();
        var index = 0;
        foreach (var path in orderedHtml)
        {
            if (!entries.TryGetValue(path, out var data))
                continue;

            var html = ReadTextFromBytes(data);
            var text = HtmlToPlainText(html);
            var words = CountWords(text);
            var title = ExtractTitleFromHtml(html) ?? Path.GetFileNameWithoutExtension(path);
            chapters.Add(new FlatChapter(index++, title, 0, text, words));
        }

        if (chapters.Count == 0)
            return null;

        Console.WriteLine($"[epub] Tolerant fallback used for {label}. Chapters={chapters.Count}");
        return (bookTitle, chapters);
    }

    private static bool TryReadZipEntries(
        byte[] sourceBytes,
        string label,
        out Dictionary<string, byte[]> entries,
        out string? opfPath)
    {
        entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        opfPath = null;

        try
        {
            using var inputStream = new MemoryStream(sourceBytes);
            using var zipInput = new ZipInputStream(inputStream);
            ZipEntry? entry;
            while ((entry = zipInput.GetNextEntry()) != null)
            {
                if (!entry.IsFile) continue;
                var name = NormalizeZipPath(entry.Name);
                if (!IsHtmlEntry(name) && !name.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var buffer = new MemoryStream();
                zipInput.CopyTo(buffer);
                entries[name] = buffer.ToArray();
                if (opfPath == null && name.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
                    opfPath = name;
            }

            return entries.Count > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[epub] Failed to read zip entries for tolerant fallback ({label}): {ex.Message}");
            return false;
        }
    }

    private static string NormalizeZipPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path.Replace('\\', '/').TrimStart('/');
        var fragmentIndex = normalized.IndexOf('#');
        if (fragmentIndex >= 0)
            normalized = normalized[..fragmentIndex];
        return normalized;
    }

    private static string NormalizeZipDir(string path)
    {
        var normalized = NormalizeZipPath(path);
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized.TrimEnd('/');
    }

    private static string ResolveOpfHref(string opfDir, string href)
    {
        var decoded = Uri.UnescapeDataString(href);
        decoded = decoded.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(opfDir))
            return decoded;
        return $"{opfDir}/{decoded}";
    }

    private static string? FindEntry(Dictionary<string, byte[]> entries, string href)
    {
        var normalized = NormalizeZipPath(href);
        if (entries.ContainsKey(normalized))
            return normalized;

        var match = entries.Keys.FirstOrDefault(key =>
            key.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase) ||
            key.EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
        return match;
    }

    private static bool IsHtmlEntry(string path)
    {
        return path.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadTextFromBytes(byte[] data)
    {
        using var stream = new MemoryStream(data);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string? ExtractTitleFromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var titleMatch = Regex.Match(html, @"<title[^>]*>(?<t>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (titleMatch.Success)
        {
            var title = WebUtility.HtmlDecode(titleMatch.Groups["t"].Value).Trim();
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }

        var h1Match = Regex.Match(html, @"<h1[^>]*>(?<t>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (h1Match.Success)
        {
            var title = WebUtility.HtmlDecode(titleMatch.Groups["t"].Value).Trim();
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }

        var h2Match = Regex.Match(html, @"<h2[^>]*>(?<t>.*?)</h2>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (h2Match.Success)
        {
            var title = WebUtility.HtmlDecode(titleMatch.Groups["t"].Value).Trim();
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }

        return null;
    }

    private static byte[]? TryRepairZip(byte[] sourceBytes)
    {
        try
        {
            using var inputStream = new MemoryStream(sourceBytes);
            using var zipInput = new ZipInputStream(inputStream);
            var entries = new List<(string Name, byte[] Data)>();
            ZipEntry? entry;
            while ((entry = zipInput.GetNextEntry()) != null)
            {
                if (!entry.IsFile) continue;
                using var buffer = new MemoryStream();
                zipInput.CopyTo(buffer);
                entries.Add((entry.Name, buffer.ToArray()));
            }

            if (entries.Count == 0)
                return null;

            var mimeEntry = entries.FirstOrDefault(e => string.Equals(e.Name, "mimetype", StringComparison.OrdinalIgnoreCase));
            var others = entries.Where(e => !string.Equals(e.Name, "mimetype", StringComparison.OrdinalIgnoreCase)).ToList();

            using var outputStream = new MemoryStream();
            using var zipOutput = new ZipOutputStream(outputStream);
            zipOutput.SetLevel(6);

            if (!string.IsNullOrEmpty(mimeEntry.Name))
            {
                var crc32 = new ICSharpCode.SharpZipLib.Checksum.Crc32();
                crc32.Update(mimeEntry.Data);
                var mimeZipEntry = new ZipEntry("mimetype")
                {
                    CompressionMethod = CompressionMethod.Stored,
                    Size = mimeEntry.Data.Length,
                    CompressedSize = mimeEntry.Data.Length,
                    Crc = crc32.Value
                };
                zipOutput.PutNextEntry(mimeZipEntry);
                zipOutput.Write(mimeEntry.Data, 0, mimeEntry.Data.Length);
                zipOutput.CloseEntry();
            }

            foreach (var item in others)
            {
                var newEntry = new ZipEntry(item.Name)
                {
                    CompressionMethod = CompressionMethod.Deflated
                };
                zipOutput.PutNextEntry(newEntry);
                zipOutput.Write(item.Data, 0, item.Data.Length);
                zipOutput.CloseEntry();
            }

            zipOutput.Finish();
            return outputStream.ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[epub] Zip repair failed: {ex.Message}");
            return null;
        }
    }

    private static byte[] EnsureCommonCoverEntries(byte[] sourceBytes)
    {
        var paths = new[]
        {
            "OEBPS/Images/Cover.png",
            "OEBPS/Images/cover.png",
            "OEBPS/Images/cover.jpg",
            "OEBPS/Cover.png",
            "OEBPS/cover.jpg",
            "cover.png",
            "cover.jpg"
        };

        var updated = sourceBytes;
        foreach (var path in paths)
        {
            updated = EnsureZipEntry(updated, path);
        }
        return updated;
    }

    private static string? ExtractMissingEpubPath(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var patterns = new[]
        {
            "file\\s+[\"“”'](?<path>[^\"“”']+)[\"“”']\\s+was not found",
            "file\\s+(?<path>[^\\s]+)\\s+was not found"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups["path"].Value;
        }

        var fallback = Regex.Match(message, @"(?<path>OEBPS/[^""\s]+)", RegexOptions.IgnoreCase);
        return fallback.Success ? fallback.Groups["path"].Value : null;
    }

    private static byte[] EnsureZipEntry(byte[] sourceBytes, string entryPath)
    {
        try
        {
            using var stream = new MemoryStream(sourceBytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);
            if (archive.GetEntry(entryPath) != null)
                return sourceBytes;

            var entry = archive.CreateEntry(entryPath);
            using var entryStream = entry.Open();
            entryStream.Flush();
            return stream.ToArray();
        }
        catch (InvalidDataException ex)
        {
            Console.WriteLine($"[epub] Invalid zip structure while adding '{entryPath}': {ex.Message}");
            return sourceBytes;
        }
    }

    private static async Task BuildIndexOnlyAsync(string filePath, string readerKey, string cacheDir)
    {
        await using var fileStream = File.OpenRead(filePath);
        using var ms = new MemoryStream();
        await fileStream.CopyToAsync(ms);
        var (title, flatChapters) = await GetFlatChaptersAsync(ms.ToArray(), $"library:{filePath}", filePath);
        flatChapters = flatChapters
            .Where(ch => ch.WordCount >= 50)
            .ToList();

        var chapterMetas = flatChapters
            .Select(ch => new CachedChapterMeta(
                ch.Id,
                ch.Title,
                ch.Level,
                ch.PlainText.Length,
                ch.WordCount,
                $"chapter-{ch.Id:D4}.txt",
                null,
                null))
            .ToList();

        var meta = new CachedChapterIndex(
            readerKey,
            title,
            DateTime.UtcNow,
            chapterMetas);

        var metaJson = JsonSerializer.Serialize(meta, CacheJsonOptions);
        await File.WriteAllTextAsync(Path.Combine(cacheDir, "metadata.json"), metaJson);
    }

    private static async Task<CachedChapterIndex?> TryReadIndex(string metaPath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(metaPath);
            return JsonSerializer.Deserialize<CachedChapterIndex>(json, CacheJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<FlatChapter> FlattenChapters(EpubBook book)
    {
        var results = new List<FlatChapter>();
        var index = 0;

        if (book.Navigation != null && book.Navigation.Any())
        {
            void Walk(IEnumerable<EpubNavigationItem> items, int level)
            {
                foreach (var nav in items)
                {
                    if (nav.Type == EpubNavigationItemType.LINK && nav.HtmlContentFile != null)
                    {
                        var currentId = index++;
                        var title = string.IsNullOrWhiteSpace(nav.Title)
                            ? $"Chapter {currentId + 1}"
                            : nav.Title.Trim();

                        var text = HtmlToPlainText(nav.HtmlContentFile.Content);
                        var words = CountWords(text);

                        results.Add(new FlatChapter(currentId, title, level, text, words));
                    }

                    if (nav.NestedItems?.Any() == true)
                        Walk(nav.NestedItems, level + 1);
                }
            }

            Walk(book.Navigation, 0);
        }

        if (results.Count == 0 && book.ReadingOrder != null && book.ReadingOrder.Any())
        {
            foreach (var file in book.ReadingOrder)
            {
                var currentId = index++;
                var title = string.IsNullOrWhiteSpace(file.FilePath)
                    ? $"Section {currentId + 1}"
                    : Path.GetFileNameWithoutExtension(file.FilePath);

                var text = HtmlToPlainText(file.Content);
                var words = CountWords(text);

                results.Add(new FlatChapter(currentId, title, 0, text, words));
            }
        }

        return results;
    }

    private static string HtmlToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var cleaned = Regex.Replace(
            html,
            "<(script|style)[^>]*?>.*?</\\1>",
            string.Empty,
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        cleaned = Regex.Replace(
            cleaned,
            @"<(br|p|div|h[1-6]|li)[^>]*>",
            "\n\n",
            RegexOptions.IgnoreCase);

        cleaned = Regex.Replace(cleaned, "<[^>]+>", " ");

        var decoded = WebUtility.HtmlDecode(cleaned);
        decoded = decoded.Replace("\r", "");

        decoded = Regex.Replace(decoded, @"[ \t]+", " ");
        decoded = Regex.Replace(decoded, @"(\n\s*){3,}", "\n\n");
        decoded = decoded.Trim();

        return decoded.Trim();
    }

    private static string ComputeHash(string value)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(value);
        var hashBytes = sha.ComputeHash(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return Regex.Matches(text, @"\b\w+\b").Count;
    }

    public static string GetCacheRoot() => EpubCacheRoot;
    public static string ComputeHashPublic(string value) => ComputeHash(value);

    public static async Task<string?> ReadChapterContentAsync(string filePath, int chapterId)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var (_, flatChapters) = await GetFlatChaptersAsync(ms.ToArray(), $"library:{filePath}", filePath);
            var target = flatChapters.FirstOrDefault(ch => ch.Id == chapterId);
            return target?.PlainText;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[library] Failed to read chapter {chapterId} from {filePath}: {ex.Message}");
            return null;
        }
    }

    public static async Task<string?> ReadChapterContentCachedAsync(string filePath, int chapterId)
    {
        var cacheKey = $"{filePath}::{chapterId}";
        if (ChapterContentCache.TryGetValue(cacheKey, out string cached))
            return cached;

        var content = await ReadChapterContentAsync(filePath, chapterId);
        if (content != null)
        {
            ChapterContentCache.Set(cacheKey, content, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(30)
            });
        }
        return content;
    }
}

// ========================================
// AI Content Cache System
// ========================================
public static class AiContentCache
{
    private static string GetCacheRoot()
    {
        var env = Environment.GetEnvironmentVariable("AI_CACHE_ROOT");
        return env ?? Path.Combine(Directory.GetCurrentDirectory(), "ai-cache");
    }

    private static string SanitizeForFilename(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(input.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        if (sanitized.Length > 200) sanitized = sanitized.Substring(0, 200);
        return sanitized;
    }

    public static string SanitizeKey(string input) => SanitizeForFilename(input);

    public static HashSet<string> GetExistingSummaryKeys()
    {
        var root = GetCacheRoot();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var subDirs = new[]
        {
            "chapter-summaries",
            "chapter-ultra-summaries",
            "section-summaries",
            "chunk-boundaries",
            "character-graphs"
        };

        foreach (var subDir in subDirs)
        {
            var path = Path.Combine(root, subDir);
            if (!Directory.Exists(path))
                continue;

            foreach (var dir in Directory.GetDirectories(path))
            {
                var name = Path.GetFileName(dir);
                if (!string.IsNullOrWhiteSpace(name))
                    keys.Add(name);
            }
        }

        return keys;
    }

    public static bool HasAnySummaries(string key, ISet<string> existingKeys)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var sanitized = SanitizeKey(key);
        return existingKeys.Contains(sanitized) || existingKeys.Contains(key);
    }

    public static string GetChapterSummaryCachePath(string dropboxPath, int chapterId)
    {
        var root = GetCacheRoot();
        var bookFolder = SanitizeForFilename(dropboxPath);
        var dir = Path.Combine(root, "chapter-summaries", bookFolder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"chapter-{chapterId}.json");
    }

    public static string GetUltraChapterSummaryCachePath(string dropboxPath, int chapterId)
    {
        var root = GetCacheRoot();
        var bookFolder = SanitizeForFilename(dropboxPath);
        var dir = Path.Combine(root, "chapter-ultra-summaries", bookFolder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"chapter-{chapterId}.json");
    }

    public static void SaveChapterSummary(string dropboxPath, int chapterId, object summaryData)
    {
        var path = GetChapterSummaryCachePath(dropboxPath, chapterId);
        var json = System.Text.Json.JsonSerializer.Serialize(summaryData, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
        Console.WriteLine($"💾 Saved chapter summary to: {path}");
    }

    public static void SaveUltraChapterSummary(string dropboxPath, int chapterId, object summaryData)
    {
        var path = GetUltraChapterSummaryCachePath(dropboxPath, chapterId);
        var json = System.Text.Json.JsonSerializer.Serialize(summaryData, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
        Console.WriteLine($"💾 Saved ultra chapter summary to: {path}");
    }

    public static T? LoadChapterSummary<T>(string dropboxPath, int chapterId) where T : class
    {
        var path = GetChapterSummaryCachePath(dropboxPath, chapterId);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to load chapter summary from {path}: {ex.Message}");
            return null;
        }
    }

    public static T? LoadUltraChapterSummary<T>(string dropboxPath, int chapterId) where T : class
    {
        var path = GetUltraChapterSummaryCachePath(dropboxPath, chapterId);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to load ultra chapter summary from {path}: {ex.Message}");
            return null;
        }
    }

    public static bool ChapterSummaryExists(string dropboxPath, int chapterId)
    {
        var path = GetChapterSummaryCachePath(dropboxPath, chapterId);
        return File.Exists(path);
    }

    public static bool UltraChapterSummaryExists(string dropboxPath, int chapterId)
    {
        var path = GetUltraChapterSummaryCachePath(dropboxPath, chapterId);
        return File.Exists(path);
    }

    public static void DeleteChapterSummary(string dropboxPath, int chapterId)
    {
        var path = GetChapterSummaryCachePath(dropboxPath, chapterId);
        if (File.Exists(path))
        {
            File.Delete(path);
            Console.WriteLine($"🗑️ Deleted chapter summary: {path}");
        }
    }

    public static void DeleteUltraChapterSummary(string dropboxPath, int chapterId)
    {
        var path = GetUltraChapterSummaryCachePath(dropboxPath, chapterId);
        if (File.Exists(path))
        {
            File.Delete(path);
            Console.WriteLine($"🗑️ Deleted ultra chapter summary: {path}");
        }
    }

    public static Dictionary<int, Dictionary<string, object>> LoadAllChapterSummaries(string dropboxPath)
    {
        var result = new Dictionary<int, Dictionary<string, object>>();
        var root = GetCacheRoot();
        var bookFolder = SanitizeForFilename(dropboxPath);
        var dir = Path.Combine(root, "chapter-summaries", bookFolder);

        if (!Directory.Exists(dir))
            return result;

        var files = Directory.GetFiles(dir, "chapter-*.json");
        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            // Extract chapter ID from filename like "chapter-0.json"
            if (fileName.StartsWith("chapter-") && int.TryParse(fileName.Substring(8), out var chapterId))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var summary = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    if (summary != null)
                    {
                        result[chapterId] = summary;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Failed to load summary from {file}: {ex.Message}");
                }
            }
        }

        Console.WriteLine($"📦 Loaded {result.Count} cached summaries for book: {dropboxPath}");
        return result;
    }

    // ─── Chunk boundary caching ──────────────────────────────────────────────
    public static string GetChunkBoundariesCachePath(string dropboxPath, int chapterId)
    {
        var root = GetCacheRoot();
        var bookFolder = SanitizeForFilename(dropboxPath);
        var dir = Path.Combine(root, "chunk-boundaries", bookFolder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"chapter-{chapterId}.json");
    }

    public static void SaveChunkBoundaries(string dropboxPath, int chapterId, List<ChunkBoundary> chunks)
    {
        var path = GetChunkBoundariesCachePath(dropboxPath, chapterId);
        var data = new ChunkBoundariesResponse(chapterId, chunks, DateTime.UtcNow);
        var json = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
        Console.WriteLine($"💾 Saved chunk boundaries to: {path}");
    }

    public static ChunkBoundariesResponse? LoadChunkBoundaries(string dropboxPath, int chapterId)
    {
        var path = GetChunkBoundariesCachePath(dropboxPath, chapterId);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<ChunkBoundariesResponse>(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to load chunk boundaries from {path}: {ex.Message}");
            return null;
        }
    }

    // ─── Section summary caching ──────────────────────────────────────────────
    public static string GetSectionSummaryCachePath(string dropboxPath, int chapterId, int sectionIndex)
    {
        var root = GetCacheRoot();
        var bookFolder = SanitizeForFilename(dropboxPath);
        var dir = Path.Combine(root, "section-summaries", bookFolder, $"chapter-{chapterId}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"section-{sectionIndex}.json");
    }

    public static void SaveSectionSummary(string dropboxPath, int chapterId, int sectionIndex, object summaryData)
    {
        var path = GetSectionSummaryCachePath(dropboxPath, chapterId, sectionIndex);
        var json = System.Text.Json.JsonSerializer.Serialize(summaryData, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
        Console.WriteLine($"💾 Saved section summary to: {path}");
    }

    public static SectionSummaryResponse? LoadSectionSummary(string dropboxPath, int chapterId, int sectionIndex)
    {
        var path = GetSectionSummaryCachePath(dropboxPath, chapterId, sectionIndex);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<SectionSummaryResponse>(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to load section summary from {path}: {ex.Message}");
            return null;
        }
    }

    public static bool SectionSummaryExists(string dropboxPath, int chapterId, int sectionIndex)
    {
        var path = GetSectionSummaryCachePath(dropboxPath, chapterId, sectionIndex);
        return File.Exists(path);
    }

    // ─── Section vocabulary caching ────────────────────────────────────────────
    public static string GetSectionVocabCachePath(string dropboxPath, int chapterId, int sectionIndex)
    {
        var root = GetCacheRoot();
        var bookFolder = SanitizeForFilename(dropboxPath);
        var dir = Path.Combine(root, "section-summaries", bookFolder, $"chapter-{chapterId}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"section-{sectionIndex}-vocab.json");
    }

    public static void SaveSectionVocab(string dropboxPath, int chapterId, int sectionIndex, List<FlashcardItem> vocabCards)
    {
        var path = GetSectionVocabCachePath(dropboxPath, chapterId, sectionIndex);
        var json = System.Text.Json.JsonSerializer.Serialize(vocabCards, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
        Console.WriteLine($"💾 Saved section vocab to: {path}");
    }

    public static List<FlashcardItem>? LoadSectionVocab(string dropboxPath, int chapterId, int sectionIndex)
    {
        var path = GetSectionVocabCachePath(dropboxPath, chapterId, sectionIndex);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<List<FlashcardItem>>(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to load section vocab from {path}: {ex.Message}");
            return null;
        }
    }

    public static List<string> GetAllChapterSummariesAsStrings(string dropboxPath)
    {
        var summaries = new List<string>();
        var chapterSummaries = LoadAllChapterSummaries(dropboxPath);

        foreach (var kvp in chapterSummaries.OrderBy(x => x.Key))
        {
            if (kvp.Value.TryGetValue("summary", out var summaryObj))
            {
                var summaryText = summaryObj?.ToString();
                if (!string.IsNullOrWhiteSpace(summaryText))
                {
                    summaries.Add(summaryText);
                }
            }
        }

        return summaries;
    }

    public static List<string> GetAllSectionSummaries(string dropboxPath)
    {
        var summaries = new List<string>();
        var root = GetCacheRoot();
        var bookFolder = SanitizeForFilename(dropboxPath);
        var sectionsDir = Path.Combine(root, "section-summaries", bookFolder);

        if (!Directory.Exists(sectionsDir))
            return summaries;

        // Get all chapter directories
        var chapterDirs = Directory.GetDirectories(sectionsDir)
            .OrderBy(d => d)
            .ToArray();

        foreach (var chapterDir in chapterDirs)
        {
            // Get all section files in this chapter
            var sectionFiles = Directory.GetFiles(chapterDir, "section-*.json")
                .OrderBy(f => f)
                .ToArray();

            foreach (var sectionFile in sectionFiles)
            {
                try
                {
                    var json = File.ReadAllText(sectionFile);
                    var summary = System.Text.Json.JsonSerializer.Deserialize<SectionSummaryResponse>(json);
                    if (summary != null && !string.IsNullOrWhiteSpace(summary.Summary))
                    {
                        summaries.Add(summary.Summary);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Failed to load section summary from {sectionFile}: {ex.Message}");
                }
            }
        }

        return summaries;
    }

    // ─── Delete all AI cache for a book ───────────────────────────────────────
    public static bool DeleteAllAiCacheForBook(string dropboxPath)
    {
        var root = GetCacheRoot();
        var bookFolder = SanitizeForFilename(dropboxPath);
        var deletedCount = 0;

        try
        {
            // Delete chapter summaries
            var chapterSummariesDir = Path.Combine(root, "chapter-summaries", bookFolder);
            if (Directory.Exists(chapterSummariesDir))
            {
                Directory.Delete(chapterSummariesDir, recursive: true);
                deletedCount++;
                Console.WriteLine($"🗑️ Deleted chapter summaries: {chapterSummariesDir}");
            }

            // Delete chunk boundaries
            var chunkBoundariesDir = Path.Combine(root, "chunk-boundaries", bookFolder);
            if (Directory.Exists(chunkBoundariesDir))
            {
                Directory.Delete(chunkBoundariesDir, recursive: true);
                deletedCount++;
                Console.WriteLine($"🗑️ Deleted chunk boundaries: {chunkBoundariesDir}");
            }

            // Delete ultra chapter summaries
            var ultraSummariesDir = Path.Combine(root, "chapter-ultra-summaries", bookFolder);
            if (Directory.Exists(ultraSummariesDir))
            {
                Directory.Delete(ultraSummariesDir, recursive: true);
                deletedCount++;
                Console.WriteLine($"🗑️ Deleted ultra chapter summaries: {ultraSummariesDir}");
            }

            // Delete section summaries (includes vocab files)
            var sectionSummariesDir = Path.Combine(root, "section-summaries", bookFolder);
            if (Directory.Exists(sectionSummariesDir))
            {
                Directory.Delete(sectionSummariesDir, recursive: true);
                deletedCount++;
                Console.WriteLine($"🗑️ Deleted section summaries and vocab: {sectionSummariesDir}");
            }

            // Delete character graph
            var characterGraphDir = Path.Combine(root, "character-graphs", bookFolder);
            if (Directory.Exists(characterGraphDir))
            {
                Directory.Delete(characterGraphDir, recursive: true);
                deletedCount++;
                Console.WriteLine($"🗑️ Deleted character graph: {characterGraphDir}");
            }

            Console.WriteLine($"✅ Deleted {deletedCount} AI cache directories for book");
            return deletedCount > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to delete AI cache: {ex.Message}");
            return false;
        }
    }

    // ─── Character graph caching ──────────────────────────────────────────────
    public static string GetCharacterGraphCachePath(string dropboxPath)
    {
        var root = GetCacheRoot();
        var bookFolder = SanitizeForFilename(dropboxPath);
        var dir = Path.Combine(root, "character-graphs", bookFolder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "graph.json");
    }

    public static void SaveCharacterGraph(string dropboxPath, CharacterGraphResponse graph)
    {
        var path = GetCharacterGraphCachePath(dropboxPath);
        var json = System.Text.Json.JsonSerializer.Serialize(graph, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
        Console.WriteLine($"💾 Saved character graph to: {path}");
    }

    public static CharacterGraphResponse? LoadCharacterGraph(string dropboxPath)
    {
        var path = GetCharacterGraphCachePath(dropboxPath);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<CharacterGraphResponse>(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to load character graph from {path}: {ex.Message}");
            return null;
        }
    }

    public static bool CharacterGraphExists(string dropboxPath)
    {
        var path = GetCharacterGraphCachePath(dropboxPath);
        return File.Exists(path);
    }
}

// Make Program accessible to integration tests
public partial class Program { }
