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
using BCrypt.Net;
using System.Diagnostics;
using VersOne.Epub;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);
static string GetModelDeep(IConfiguration cfg) =>
    cfg["OpenAI:ModelDeep"]
    ?? Environment.GetEnvironmentVariable("OPENAI_MODEL_DEEP")
    ?? "gpt-5.2";

static string GetModelFast(IConfiguration cfg) =>
    cfg["OpenAI:ModelFast"]
    ?? Environment.GetEnvironmentVariable("OPENAI_MODEL_FAST")
    ?? "gpt-4o";

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

// ─── validation helpers ──────────────────────────────────────────────────
static bool IsValidMd5(string md5) =>
    !string.IsNullOrWhiteSpace(md5) &&
    Regex.IsMatch(md5, "^[a-f0-9]{32}$", RegexOptions.IgnoreCase);

static bool IsValidDropboxPath(string? path)
{
    if (string.IsNullOrWhiteSpace(path))
        return false;

    // Must start with /
    if (!path.StartsWith('/'))
        return false;

    // Check for path traversal attempts
    if (path.Contains("..") || path.Contains("~"))
        return false;

    // Must end with .epub
    if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        return false;

    // Reasonable length limit (max 500 chars)
    if (path.Length > 500)
        return false;

    return true;
}

static bool IsValidChapterId(int chapterId) =>
    chapterId >= 0 && chapterId < 10000; // Reasonable max chapter limit

static bool IsValidTextLength(string? text, int maxLength = 1_000_000)
{
    if (string.IsNullOrEmpty(text))
        return true; // Empty is valid, required checks should be separate

    return text.Length <= maxLength;
}

static bool IsValidSearchQuery(string? query, int minLength = 1, int maxLength = 500)
{
    if (string.IsNullOrWhiteSpace(query))
        return false;

    var trimmed = query.Trim();
    return trimmed.Length >= minLength && trimmed.Length <= maxLength;
}

static bool IsValidTitle(string? title, int maxLength = 500)
{
    if (string.IsNullOrEmpty(title))
        return true; // Empty is valid for optional fields

    return title.Length <= maxLength;
}

static IResult? CheckTokenLimit(IConfiguration cfg)
{
    var allowance = cfg.GetValue<long?>("OpenAI:MonthlyTokenAllowance");
    if (allowance.HasValue && OpenAiUsageTracker.IsOverLimit(allowance.Value))
    {
        return Results.Problem(
            detail: "Monthly token allowance has been exceeded. The service will reset at the beginning of next month.",
            statusCode: 429,
            title: "Token Limit Exceeded"
        );
    }

    return null;
}

static bool IsTokenLimitExceeded(IConfiguration cfg)
{
    var allowance = cfg.GetValue<long?>("OpenAI:MonthlyTokenAllowance");
    return allowance.HasValue && OpenAiUsageTracker.IsOverLimit(allowance.Value);
}

// ─── 1) search ───────────────────────────────────────────────────────────
app.MapGet("/api/anna/book", async (
    [FromQuery] string name,
    AnnaArchiveService svc,
    [FromQuery] bool exact = false) =>
{
    if (!IsValidSearchQuery(name))
        return Results.BadRequest(new {
            error = "Query parameter 'name' is required and must be between 1 and 500 characters."
        });

    var books = (await svc.SearchAsync(name, searchLimit)).ToList();

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

// ─── 2) non-member download ──────────────────────────────────────────────
app.MapGet("/api/anna/book/{md5}/download", async (
    [FromRoute] string md5,
    AnnaArchiveService svc) =>
{
    if (!IsValidMd5(md5))
        return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

    var links = await svc.GetDownloadLinksAsync(md5);
    return links.Any()
        ? Results.Ok(new { id = md5, downloadLinks = links })
        : ApiResponse.NotFound("No download links found.");
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 3) member download (url + counters) ─────────────────────────────────
app.MapGet("/api/anna/book/{md5}/download/member", async (
    [FromRoute] string md5,
    AnnaArchiveService svc) =>
{
    if (!IsValidMd5(md5))
        return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

    var doc = await svc.GetMemberDownloadDocumentAsync(md5, memberKey);

    string? downloadUrl = null;
    if (doc.TryGetProperty("download_url", out var du))
        downloadUrl = du.ValueKind == JsonValueKind.String
                    ? du.GetString()
                    : du.EnumerateArray().FirstOrDefault().GetString();

    AccountFastDownloadInfoDto? acctInfo = null;
    if (doc.TryGetProperty("account_fast_download_info", out var ai) &&
        ai.ValueKind == JsonValueKind.Object)
        acctInfo = new AccountFastDownloadInfoDto(
            ai.GetProperty("downloads_left").GetInt32(),
            ai.GetProperty("downloads_per_day").GetInt32());

    return string.IsNullOrEmpty(downloadUrl)
        ? ApiResponse.NotFound("No download URL found.")
        : Results.Ok(new { downloadUrl, accountFastInfo = acctInfo });
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 4) send-to-boox (via Dropbox) ──────────────────────────────────────
app.MapPost("/api/anna/book/{md5}/send-to-boox", async (
    [FromRoute] string md5,
    [FromQuery] string? title,
    AnnaArchiveService anna,
    DropboxClient dropbox,
    IConfiguration cfg) =>
{
    if (!IsValidMd5(md5))
        return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

    if (!IsValidTitle(title))
        return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

    // Use shared helper to download book from Anna's Archive
    var (resp, fileName, acctInfo, errorMessage) = await DownloadBookFromAnnaArchiveAsync(md5, title, anna, memberKey);

    if (errorMessage != null)
        return Results.Ok(new { success = false, message = errorMessage, accountFastInfo = acctInfo });

    if (resp == null || fileName == null)
        return Results.Ok(new { success = false, message = "Failed to download book.", accountFastInfo = acctInfo });

    using (resp)
    {
        var uploadPath = $"{cfg["Dropbox:UploadFolderPath"]}/{fileName}";
        using var stream = await resp.Content.ReadAsStreamAsync();

    try
    {
        Console.WriteLine($"Uploading '{fileName}' to Dropbox: {uploadPath}");

        var uploaded = await dropbox.Files.UploadAsync(
            uploadPath,
            WriteMode.Overwrite.Instance,
            body: stream
        );

        Console.WriteLine($"✅ Dropbox upload successful! File: {uploaded.PathDisplay}");

        return Results.Ok(new
        {
            success         = true,
            dropboxPath     = uploaded.PathDisplay,
            dropboxFileId   = uploaded.Id,
            accountFastInfo = acctInfo
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
    DropboxClient dropbox) =>
{
    if (!IsValidDropboxPath(path))
        return Results.BadRequest(new {
            error = "Invalid Dropbox path. Must start with '/', end with '.epub', and be less than 500 characters."
        });

    try
    {
        var (index, _) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, path);

        var response = new DropboxEpubChaptersResponse(
            index.Title,
            index.Chapters
                .Where(ch => ch.WordCount >= 50)
                .Select(ch => new DropboxChapterDto(ch.Id, ch.Title, ch.Level, ch.WordCount))
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
    DropboxClient dropbox) =>
{
    if (!IsValidDropboxPath(path))
        return Results.BadRequest(new {
            error = "Invalid Dropbox path. Must start with '/', end with '.epub', and be less than 500 characters."
        });

    if (!IsValidChapterId(chapterId))
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
    IConfiguration cfg) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Text))
        return Results.BadRequest(new { error = "Text is required." });

    if (!IsValidTextLength(request.Text))
        return Results.BadRequest(new { error = "Text too long. Maximum 1,000,000 characters." });

    var tokenLimitResult = CheckTokenLimit(cfg);
    if (tokenLimitResult is not null) return tokenLimitResult;

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");
        var model = GetModelFast(cfg);

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
                        Offset = ExtractWordOffset(Path.GetFileNameWithoutExtension(f))
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

        var userPrompt = BuildAnalysisPrompt(contextBlock, previousAnalyses, request.Text);
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

        var summary = AiResponseParser.ExtractText(doc.RootElement);

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("input_tokens").GetInt32();
            var completionTokens = usage.GetProperty("output_tokens").GetInt32();
            OpenAiUsageTracker.AddUsage(promptTokens, completionTokens);
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
    IConfiguration cfg) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Term))
        return Results.BadRequest(new { error = "Term is required." });

    var tokenLimitResult = CheckTokenLimit(cfg);
    if (tokenLimitResult is not null) return tokenLimitResult;

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");
        var model = GetModelDeep(cfg);

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
        var detail = AiResponseParser.ExtractText(doc.RootElement);

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("input_tokens").GetInt32();
            var completionTokens = usage.GetProperty("output_tokens").GetInt32();
            OpenAiUsageTracker.AddUsage(promptTokens, completionTokens);
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
app.MapGet("/api/ai/flashcards", ([FromQuery] string path) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    var flashcards = LoadFlashcards(path);
    return Results.Ok(flashcards);
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapPost("/api/ai/flashcards", async (
    [FromBody] FlashcardRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Term) || string.IsNullOrWhiteSpace(request.DropboxPath))
        return Results.BadRequest(new { error = "Term and dropboxPath are required." });

    var tokenLimitResult = CheckTokenLimit(cfg);
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
        var payload = OpenAiModelHelper.BuildChatCompletionPayload(
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
        var content = AiResponseParser.ExtractText(doc.RootElement) ?? "{}";

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            OpenAiUsageTracker.AddUsage(promptTokens, completionTokens);
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

        var list = LoadFlashcards(request.DropboxPath!);
        foreach (var card in cardsParsed)
        {
            var existing = list.FindIndex(x => string.Equals(x.Term, card.Term, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
                list[existing] = card;
            else
                list.Add(card);
        }

        SaveFlashcards(request.DropboxPath!, list);
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

app.MapDelete("/api/ai/flashcards", ([FromQuery] string path) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    try
    {
        var (_, filePath) = GetFlashcardPath(path);
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

// Helper function to extract word offset from summary filename
static int ExtractWordOffset(string filenameWithoutExtension)
{
    // Format: "summary-{chapterId}-{wordOffset}"
    var parts = filenameWithoutExtension.Split('-');
    if (parts.Length >= 3 && int.TryParse(parts[2], out var offset))
        return offset;
    return int.MaxValue; // If we can't parse, put at the end
}

// Helper function to build the analysis prompt
static string BuildAnalysisPrompt(string contextBlock, string? previousAnalyses, string currentText)
{
    var prompt = new System.Text.StringBuilder();

    prompt.AppendLine($"{contextBlock}\n");

    if (!string.IsNullOrWhiteSpace(previousAnalyses))
    {
        prompt.AppendLine("Previous analyses from this reading session:");
        prompt.AppendLine(previousAnalyses);
        prompt.AppendLine("\n---\n");
    }

    prompt.AppendLine("Analyze this passage:");
    prompt.AppendLine(currentText);

    return prompt.ToString();
}

static List<string> SplitIntoChunks(string text, int maxWords)
{
    if (string.IsNullOrWhiteSpace(text) || maxWords <= 0)
        return new List<string>();

    var words = Regex.Split(text, @"\s+").Where(w => !string.IsNullOrWhiteSpace(w)).ToArray();
    var chunks = new List<string>();
    for (var i = 0; i < words.Length; i += maxWords)
    {
        var slice = words.Skip(i).Take(maxWords);
        chunks.Add(string.Join(" ", slice));
    }
    return chunks;
}

static (string cacheDir, string filePath) GetFlashcardPath(string dropboxPath)
{
    var cacheDir = Path.Combine(DropboxEpubCache.GetCacheRoot(), DropboxEpubCache.ComputeHashPublic(dropboxPath));
    Directory.CreateDirectory(cacheDir);
    var filePath = Path.Combine(cacheDir, "flashcards.json");
    return (cacheDir, filePath);
}

static List<FlashcardItem> LoadFlashcards(string dropboxPath)
{
    try
    {
        var (_, filePath) = GetFlashcardPath(dropboxPath);
        if (!System.IO.File.Exists(filePath)) return new List<FlashcardItem>();
        var json = System.IO.File.ReadAllText(filePath);
        var cards = JsonSerializer.Deserialize<List<FlashcardItem>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return cards ?? new List<FlashcardItem>();
    }
    catch
    {
        return new List<FlashcardItem>();
    }
}

static void SaveFlashcards(string dropboxPath, List<FlashcardItem> cards)
{
    var (cacheDir, filePath) = GetFlashcardPath(dropboxPath);
    Directory.CreateDirectory(cacheDir);
    var json = JsonSerializer.Serialize(cards, new JsonSerializerOptions { WriteIndented = true });
    System.IO.File.WriteAllText(filePath, json);
}

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
    IConfiguration cfg) =>
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

    // Check if cached summary exists
    var cached = AiContentCache.LoadChapterSummary<Dictionary<string, object>>(request.DropboxPath, request.ChapterId);
    if (cached != null)
    {
        Console.WriteLine($"📦 Returning cached chapter summary for {request.DropboxPath} chapter {request.ChapterId}");
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.Append("Cache-Control", "no-cache");
        context.Response.Headers.Append("Connection", "keep-alive");

        var completeEvent = new
        {
            summary = cached.GetValueOrDefault("summary", ""),
            promptTokens = cached.TryGetValue("promptTokens", out var pt) ? Convert.ToInt64(pt) : 0L,
            completionTokens = cached.TryGetValue("completionTokens", out var ct) ? Convert.ToInt64(ct) : 0L,
            totalTokens = cached.TryGetValue("totalTokens", out var tt) ? Convert.ToInt64(tt) : 0L,
            cachedAt = cached.GetValueOrDefault("cachedAt", DateTime.UtcNow)
        };

        await ServerSentEventsHelper.SendEventAsync(context.Response, completeEvent, "complete");
        return;
    }

    // Check token limit
    var tokenLimitResult = CheckTokenLimit(cfg);
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

    try
    {
        // Load chapter content using helper
        var content = await LoadChapterContentAsync(dropbox, request.DropboxPath, request.ChapterId);
        if (content is null)
        {
            await ServerSentEventsHelper.SendEventAsync(context.Response, new { message = "Chapter not found or empty." }, "error");
            return;
        }

        // Prepare context for AI
        var (index, _) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, request.DropboxPath);
        var chapter = index.Chapters.FirstOrDefault(c => c.Id == request.ChapterId);

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
        var chunks = SplitIntoChunks(content, chunkSize);

        using var http = httpFactory.CreateClient("OpenAI");
        var model = GetModelDeep(cfg);

        // TIER 1: Summarize chunks using helper
        var (chunkSummaries, tier1PromptTokens, tier1CompletionTokens) =
            await SummarizeChunksAsync(http, model, chunks, contextLine, context.Response, cfg);

        // TIER 2: Synthesize sections using helper
        var (sectionSummaries, tier2PromptTokens, tier2CompletionTokens) =
            await SynthesizeSectionsAsync(http, model, chunkSummaries, contextLine, context.Response, cfg);

        // TIER 3: Create final summary using helper
        var (finalSummary, tier3PromptTokens, tier3CompletionTokens) =
            await CreateFinalSummaryAsync(http, model, sectionSummaries, contextParts, context.Response, cfg);

        // Calculate total tokens
        var promptTokensTotal = tier1PromptTokens + tier2PromptTokens + tier3PromptTokens;
        var completionTokensTotal = tier1CompletionTokens + tier2CompletionTokens + tier3CompletionTokens;

        OpenAiUsageTracker.AddUsage(promptTokensTotal, completionTokensTotal);
        var totals = OpenAiUsageTracker.GetTotals();
        var monthlyAllowance = cfg.GetValue<long?>("OpenAI:MonthlyTokenAllowance");
        double? percent = null;
        long? remaining = null;
        if (monthlyAllowance.HasValue && monthlyAllowance.Value > 0)
        {
            percent = Math.Round((double)totals.Total / monthlyAllowance.Value * 100, 2);
            remaining = monthlyAllowance.Value - totals.Total;
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
app.MapGet("/api/ai/usage", (IConfiguration cfg) =>
{
    var totals = OpenAiUsageTracker.GetTotals();
    var allowance = cfg.GetValue<long?>("OpenAI:MonthlyTokenAllowance");
    double? percent = null;
    long? remaining = null;
    if (allowance.HasValue && allowance.Value > 0)
    {
        percent = Math.Round((double)totals.Total / allowance.Value * 100, 2);
        remaining = allowance.Value - totals.Total;
    }

    var reset = ComputeResetDateUtc(cfg);
    var costUsd = OpenAiUsageTracker.CalculateCostUsd(totals.Prompt, totals.Completion);

    var resp = new TokenUsageResponse(
        totals.Prompt,
        totals.Completion,
        totals.Total,
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
app.MapPost("/api/ai/usage/reset", () =>
{
    OpenAiUsageTracker.Reset();
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
    IConfiguration cfg) =>
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

    // Not cached - detect boundaries with SSE progress
    Console.WriteLine($"🔍 Detecting chunk boundaries for chapter {chapterId}...");

    // Set up SSE
    context.Response.Headers["Content-Type"] = "text/event-stream";
    context.Response.Headers["Cache-Control"] = "no-cache";
    context.Response.Headers["Connection"] = "keep-alive";

    // Check token limit
    if (IsTokenLimitExceeded(cfg))
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
    var epubHash = DropboxEpubCache.ComputeHashPublic(dropboxPath);
    var cacheRoot = DropboxEpubCache.GetCacheRoot();
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
            await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, dropboxPath, cacheDir);
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

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");
        var model = "gpt-4o"; // Use GPT-4o for cost-effective chunking

        Console.WriteLine($"🤖 Using model for chunk detection: {model}");
        Console.WriteLine($"   Model info: {OpenAiModelHelper.GetModelDescription(model)}");

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

            var payload = OpenAiModelHelper.BuildChatCompletionPayload(
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

            var aiText = AiResponseParser.ExtractText(doc.RootElement);

            // Track token usage
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
                var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
                OpenAiUsageTracker.AddUsage(promptTokens, completionTokens);
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
    IConfiguration cfg) =>
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

    // Check token limit
    var tokenLimitResult = CheckTokenLimit(cfg);
    if (tokenLimitResult is not null) return tokenLimitResult;

    // Load chunk boundaries
    var boundaries = AiContentCache.LoadChunkBoundaries(request.DropboxPath, request.ChapterId);
    if (boundaries == null || request.SectionIndex >= boundaries.Chunks.Count)
        return Results.BadRequest(new { error = "Invalid sectionIndex or chunk boundaries not detected." });

    // Load chapter content
    var epubHash = DropboxEpubCache.ComputeHashPublic(request.DropboxPath);
    var cacheRoot = DropboxEpubCache.GetCacheRoot();
    var chapterPath = Path.Combine(cacheRoot, epubHash, $"chapter-{request.ChapterId:D4}.txt");

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

    try
    {
        using var http = httpFactory.CreateClient("OpenAI");
        var model = GetModelDeep(cfg);

        Console.WriteLine($"🤖 Using model: {model}");
        Console.WriteLine($"   Model info: {OpenAiModelHelper.GetModelDescription(model)}");

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

        var payload = OpenAiModelHelper.BuildChatCompletionPayload(
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

        var summary = AiResponseParser.ExtractText(doc.RootElement);
        Console.WriteLine($"✅ Summary generated: {summary?.Length ?? 0} characters");

        // Track token usage
        int promptTokens = 0, completionTokens = 0;
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            OpenAiUsageTracker.AddUsage(promptTokens, completionTokens);
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

// ─── Character Graph Generation ─────────────────────────────────────────────

app.MapPost("/api/ai/characters/graph", async (
    [FromBody] CharacterGraphRequest request,
    IHttpClientFactory httpFactory,
    IConfiguration cfg) =>
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

        var payload = OpenAiModelHelper.BuildChatCompletionPayload(
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
        var content = AiResponseParser.ExtractText(doc.RootElement);

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
            OpenAiUsageTracker.AddUsage(promptTokens, completionTokens);
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
    IConfiguration cfg) =>
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

        var payload = OpenAiModelHelper.BuildChatCompletionPayload(
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
        var content = AiResponseParser.ExtractText(doc.RootElement);

        if (string.IsNullOrWhiteSpace(content))
            return Results.Problem("No updated graph data returned.");

        // Track token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            OpenAiUsageTracker.AddUsage(promptTokens, completionTokens);
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
    [FromRoute] string md5,
    [FromQuery] string? title,
    [FromQuery] string target,
    AnnaArchiveService anna,
    IEmailService emailService,
    IConfiguration cfg) =>
{
    if (!IsValidMd5(md5))
        return Results.BadRequest(new { error = "Invalid MD5 format. Must be 32 hexadecimal characters." });

    if (!IsValidTitle(title))
        return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

    if (string.IsNullOrWhiteSpace(target) || (target != "dad" && target != "mom"))
        return Results.BadRequest(new { error = "Invalid target. Must be 'dad' or 'mom'." });

    // Use shared helper to download book from Anna's Archive
    var (resp, fileName, acctInfo, errorMessage) = await DownloadBookFromAnnaArchiveAsync(md5, title, anna, memberKey);

    if (errorMessage != null)
        return Results.Ok(new { success = false, message = errorMessage, accountFastInfo = acctInfo });

    if (resp == null || fileName == null)
        return Results.Ok(new { success = false, message = "Failed to download book.", accountFastInfo = acctInfo });

    var tempFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{fileName}");

    using (resp)
    {
        try
        {
            // Download to temp file
            using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await resp.Content.CopyToAsync(fileStream);
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

            return Results.Ok(new
            {
                success         = true,
                message         = $"Book sent to {target}'s Kindle",
                accountFastInfo = acctInfo
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
static async Task<string?> LoadChapterContentAsync(
    DropboxClient dropbox,
    string dropboxPath,
    int chapterId)
{
    var (index, cacheDir) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, dropboxPath);
    var chapter = index.Chapters.FirstOrDefault(c => c.Id == chapterId);

    if (chapter is null || string.IsNullOrWhiteSpace(chapter.FileName))
        return null;

    var chapterPath = Path.Combine(cacheDir, chapter.FileName);
    if (!File.Exists(chapterPath))
        await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, dropboxPath, cacheDir);

    var content = await File.ReadAllTextAsync(chapterPath);
    return string.IsNullOrWhiteSpace(content) ? null : content;
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
    IConfiguration cfg)
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

        var chunkSummary = AiResponseParser.ExtractText(doc.RootElement) ?? string.Empty;
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
        var sectionSummary = AiResponseParser.ExtractText(sectionDoc.RootElement) ?? string.Empty;

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
    IConfiguration cfg)
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

    string finalSummary = AiResponseParser.ExtractText(finalDoc.RootElement) ?? "No summary returned.";
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
/// - accountInfo: Account download info if available
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
    var doc = await anna.GetMemberDownloadDocumentAsync(md5, memberKey);

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
    var resp = await anna.HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
    if (!resp.IsSuccessStatusCode)
        return (null, null, acctInfo, $"Download failed with status {(int)resp.StatusCode}");

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
record DropboxChapterDto(int Id, string Title, int Level, int WordCount);
record DropboxChapterContentDto(int Id, string Title, string Content, int CharacterCount, int WordCount);
record FlatChapter(int Id, string Title, int Level, string PlainText, int WordCount);
record CachedChapterMeta(int Id, string Title, int Level, int CharacterCount, int WordCount, string FileName);
record CachedChapterIndex(string Path, string Title, DateTime CachedAt, List<CachedChapterMeta> Chapters);
record DropboxCacheStatusDto(bool Cached, bool InProgress, int ChaptersTotal, int ChaptersCached, double Percent, DateTime? CachedAt);
record DropboxSearchMatchDto(int ChapterId, string Title, int MatchCount, int Position, string Snippet);
record SummarizeRequest(string Text, string? BookTitle, string? Author, int? Year, string? Premise, string? DropboxPath, int? ChapterId, int? WordOffset, List<string>? KnownWords);
record LearnMoreRequest(string Term, string? Definition, string? DropboxPath, string? BookTitle, string? Context);
record LearnMoreResponse(string Detail);
record FlashcardRequest(string Term, string? Definition, string? DropboxPath, string? BookTitle, string? Context, List<string>? KnownWords);
public record FlashcardItem(string Term, string Definition, string Etymology, List<string> UsageExamples, string? Notes);
public record FlashcardResult(List<FlashcardItem> Cards);
record WikiImagesResponse(List<string> Images);
record SummarizeResponse(string Summary);
record FullChapterSummaryRequest(string DropboxPath, int ChapterId, string? BookTitle, string? Author, int? Year, string? Premise);
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

// ─── OpenAI Model Abstraction Helper ────────────────────────────────────
static class OpenAiModelHelper
{
    /// <summary>
    /// Checks if the model is part of the GPT-5 family (gpt-5, gpt-5.2, gpt-5-mini, etc.)
    /// </summary>
    public static bool IsGpt5Family(string model) =>
        model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if the model is part of the o1 family (o1-mini, o1-preview, o3, etc.)
    /// </summary>
    public static bool IsO1Family(string model) =>
        model.StartsWith("o1-", StringComparison.OrdinalIgnoreCase) ||
        model.Equals("o3", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a Chat Completions API payload with model-specific parameters.
    /// Handles GPT-5.2, o1, and GPT-4 model families correctly.
    /// </summary>
    public static object BuildChatCompletionPayload(
        string model,
        object[] messages,
        int? maxCompletionTokens = null,
        double? temperature = null,
        string? reasoningEffort = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages
        };

        if (maxCompletionTokens.HasValue)
        {
            payload["max_completion_tokens"] = maxCompletionTokens.Value;
        }

        // Handle temperature and reasoning based on model family
        if (IsGpt5Family(model))
        {
            // GPT-5 family: temperature only works with reasoning_effort = "none"
            if (temperature.HasValue)
            {
                payload["reasoning_effort"] = "none";
                payload["temperature"] = temperature.Value;
                Console.WriteLine($"🔧 GPT-5 model: Using temperature={temperature.Value} with reasoning_effort=none");
            }
            else if (!string.IsNullOrWhiteSpace(reasoningEffort))
            {
                payload["reasoning_effort"] = reasoningEffort;
                Console.WriteLine($"🔧 GPT-5 model: Using reasoning_effort={reasoningEffort} (no temperature)");
            }
            else
            {
                // Default to "none" for GPT-5.2
                payload["reasoning_effort"] = "none";
                Console.WriteLine($"🔧 GPT-5 model: Using default reasoning_effort=none");
            }
        }
        else if (IsO1Family(model))
        {
            // o1 family: no temperature support, reasoning is built-in
            Console.WriteLine($"🔧 o1 model: No temperature or reasoning_effort parameters");
            // o1 models don't support temperature, top_p, or explicit reasoning_effort
        }
        else
        {
            // GPT-4 and earlier: standard temperature support
            if (temperature.HasValue)
            {
                payload["temperature"] = temperature.Value;
                Console.WriteLine($"🔧 GPT-4 model: Using temperature={temperature.Value}");
            }
        }

        return payload;
    }

    /// <summary>
    /// Gets a user-friendly description of model capabilities
    /// </summary>
    public static string GetModelDescription(string model)
    {
        if (IsGpt5Family(model))
            return "GPT-5 family (supports reasoning_effort, temperature with effort=none)";
        if (IsO1Family(model))
            return "o1 family (built-in reasoning, no temperature support)";
        return "GPT-4 or earlier (standard temperature support)";
    }
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
        var inProgress = CacheBuildTasks.ContainsKey(cacheDir);

        if (!File.Exists(metaPath))
        {
            // Kick off background build if not present
            _ = CacheBuildTasks.GetOrAdd(cacheDir, _ => BuildCacheInternalAsync(dropbox, dropboxPath, cacheDir));
            return new DropboxCacheStatusDto(false, true, 0, 0, 0, null);
        }

        var meta = await TryReadIndex(metaPath);
        if (meta == null)
            return new DropboxCacheStatusDto(false, inProgress, 0, 0, 0, null);

        var total = meta.Chapters.Count;
        var cached = meta.Chapters.Count(ch => File.Exists(Path.Combine(cacheDir, ch.FileName)));
        var percent = total == 0 ? 0 : Math.Round((double)cached / total * 100, 2);

        return new DropboxCacheStatusDto(true, inProgress, total, cached, percent, meta.CachedAt);
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
        try
        {
            var download = await dropbox.Files.DownloadAsync(dropboxPath);
            await using var dropboxStream = await download.GetContentAsStreamAsync();
            using var ms = new MemoryStream();
            await dropboxStream.CopyToAsync(ms);
            ms.Position = 0;

            var book = await EpubReader.ReadBookAsync(ms);
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
                    fileName));
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
        }
        finally
        {
            CacheBuildTasks.TryRemove(cacheDir, out _);
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

static class AiResponseParser
{
    public static string? ExtractText(JsonElement root)
    {
        try
        {
            // Try Chat Completions API format first (choices[0].message.content)
            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString();
                }
            }

            // Fallback to Responses API format (output array)
            if (root.TryGetProperty("output", out var output) &&
                output.ValueKind == JsonValueKind.Array &&
                output.GetArrayLength() > 0)
            {
                // Responses API structure: output array contains reasoning + message items
                // Find the message type item
                foreach (var item in output.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var typeElem) &&
                        typeElem.GetString() == "message" &&
                        item.TryGetProperty("content", out var contentArray) &&
                        contentArray.ValueKind == JsonValueKind.Array &&
                        contentArray.GetArrayLength() > 0)
                    {
                        // Look for output_text type in content array
                        foreach (var contentItem in contentArray.EnumerateArray())
                        {
                            if (contentItem.TryGetProperty("type", out var contentType) &&
                                contentType.GetString() == "output_text" &&
                                contentItem.TryGetProperty("text", out var textElem) &&
                                textElem.ValueKind == JsonValueKind.String)
                            {
                                return textElem.GetString();
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // ignore parse errors, return null
        }
        return null;
    }
}

// ─── Helper for tracking OpenAI token usage with persistent file storage ─────────
static class OpenAiUsageTracker
{
    private static readonly string UsageFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".annas-archive",
        "openai-usage.json"
    );
    private static readonly object FileLock = new object();

    private class UsageData
    {
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public DateTime LastResetDate { get; set; }
    }

    private static UsageData LoadUsage()
    {
        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UsageFilePath)!);

                if (!File.Exists(UsageFilePath))
                {
                    return new UsageData
                    {
                        PromptTokens = 0,
                        CompletionTokens = 0,
                        LastResetDate = DateTime.UtcNow
                    };
                }

                var json = File.ReadAllText(UsageFilePath);
                return JsonSerializer.Deserialize<UsageData>(json) ?? new UsageData
                {
                    PromptTokens = 0,
                    CompletionTokens = 0,
                    LastResetDate = DateTime.UtcNow
                };
            }
            catch
            {
                return new UsageData
                {
                    PromptTokens = 0,
                    CompletionTokens = 0,
                    LastResetDate = DateTime.UtcNow
                };
            }
        }
    }

    private static void SaveUsage(UsageData data)
    {
        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UsageFilePath)!);
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(UsageFilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Failed to save token usage: {ex.Message}");
            }
        }
    }

    public static void AddUsage(int prompt, int completion)
    {
        var data = LoadUsage();
        data.PromptTokens += prompt;
        data.CompletionTokens += completion;
        SaveUsage(data);
    }

    public static (long Prompt, long Completion, long Total) GetTotals()
    {
        var data = LoadUsage();
        CheckAndAutoReset(ref data);
        return (data.PromptTokens, data.CompletionTokens, data.PromptTokens + data.CompletionTokens);
    }

    public static double CalculateCostUsd(long promptTokens, long completionTokens)
    {
        // GPT-5.2 pricing (as of Dec 2025):
        // Input: $5 per 1M tokens
        // Output: $15 per 1M tokens
        const double inputCostPer1M = 5.0;
        const double outputCostPer1M = 15.0;

        var inputCost = (promptTokens / 1_000_000.0) * inputCostPer1M;
        var outputCost = (completionTokens / 1_000_000.0) * outputCostPer1M;

        return Math.Round(inputCost + outputCost, 2);
    }

    public static bool IsOverLimit(long allowance)
    {
        var data = LoadUsage();
        CheckAndAutoReset(ref data);
        var total = data.PromptTokens + data.CompletionTokens;
        return total >= allowance;
    }

    public static void Reset()
    {
        var data = new UsageData
        {
            PromptTokens = 0,
            CompletionTokens = 0,
            LastResetDate = DateTime.UtcNow
        };
        SaveUsage(data);
    }

    private static void CheckAndAutoReset(ref UsageData data)
    {
        // Check if we've passed into a new month since last reset
        var now = DateTime.UtcNow;
        var lastReset = data.LastResetDate;

        // Reset if we're in a different month OR if it's been more than 30 days
        var shouldReset = (now.Year > lastReset.Year && now.Month >= lastReset.Month) ||
                         (now.Year == lastReset.Year && now.Month > lastReset.Month) ||
                         (now - lastReset).TotalDays >= 30;

        if (shouldReset)
        {
            Console.WriteLine($"📅 Auto-resetting token usage counter (last reset: {lastReset:yyyy-MM-dd}, now: {now:yyyy-MM-dd})");
            data.PromptTokens = 0;
            data.CompletionTokens = 0;
            data.LastResetDate = now;
            SaveUsage(data);
        }
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

    public static string GetChapterSummaryCachePath(string dropboxPath, int chapterId)
    {
        var root = GetCacheRoot();
        var bookFolder = SanitizeForFilename(dropboxPath);
        var dir = Path.Combine(root, "chapter-summaries", bookFolder);
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

    public static bool ChapterSummaryExists(string dropboxPath, int chapterId)
    {
        var path = GetChapterSummaryCachePath(dropboxPath, chapterId);
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
