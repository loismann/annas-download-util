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
var searchLimit = builder.Configuration.GetValue<int>("Anna:SearchLimit", 50);

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

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// ─── validation helpers ──────────────────────────────────────────────────
static bool IsValidMd5(string md5) =>
    !string.IsNullOrWhiteSpace(md5) &&
    Regex.IsMatch(md5, "^[a-f0-9]{32}$", RegexOptions.IgnoreCase);

// ─── 1) search ───────────────────────────────────────────────────────────
app.MapGet("/api/anna/book", async (
    [FromQuery] string name,
    AnnaArchiveService svc,
    [FromQuery] bool exact = false) =>
{
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { error = "Query parameter 'name' is required." });

    var books = (await svc.SearchAsync(name, searchLimit)).ToList();

    if (exact)
        books = books
            .Where(b => string.Equals(b.Title?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

    return books.Any()
        ? books.Count == 1 ? Results.Ok(books[0]) : Results.Ok(books)
        : Results.NotFound(new { message = "No books found matching that name." });
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
        : Results.NotFound(new { message = "No download links found." });
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
        ? Results.NotFound(new { message = "No download URL found." })
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

    if (title?.Length > 500)
        return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

    var doc = await anna.GetMemberDownloadDocumentAsync(md5, memberKey);

    string? downloadUrl = null;
    if (doc.TryGetProperty("download_url", out var du))
        downloadUrl = du.ValueKind == JsonValueKind.String
                    ? du.GetString()
                    : du.EnumerateArray().FirstOrDefault().GetString();

    if (string.IsNullOrEmpty(downloadUrl))
        return Results.Ok(new { success = false, message = "No download URL found.", accountFastInfo = (object?)null });

    AccountFastDownloadInfoDto? acctInfo = null;
    if (doc.TryGetProperty("account_fast_download_info", out var ai) &&
        ai.ValueKind == JsonValueKind.Object)
        acctInfo = new AccountFastDownloadInfoDto(
            ai.GetProperty("downloads_left").GetInt32(),
            ai.GetProperty("downloads_per_day").GetInt32());

    using var resp = await anna.HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
    if (!resp.IsSuccessStatusCode)
        return Results.Ok(new { success = false, status = (int)resp.StatusCode, accountFastInfo = acctInfo });

    var rawTitle  = !string.IsNullOrWhiteSpace(title) ? title : md5;
    var safeTitle = Regex.Replace(rawTitle, $"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]", "_");

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
        return Results.Problem("Unable to list Dropbox files right now.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapGet("/api/anna/dropbox/epub/chapters", async (
    [FromQuery] string path,
    DropboxClient dropbox) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only .epub files are supported." });

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
        return Results.Problem("Unable to download EPUB from Dropbox.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to load EPUB: {ex.Message}");
        return Results.Problem("Unable to read the EPUB file.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapGet("/api/anna/dropbox/epub/chapter", async (
    [FromQuery] string path,
    [FromQuery] int chapterId,
    DropboxClient dropbox) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only .epub files are supported." });

    if (chapterId < 0)
        return Results.BadRequest(new { error = "chapterId must be zero or positive." });

    try
    {
        var (index, cacheDir) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, path);
        var chapter = index.Chapters.FirstOrDefault(c => c.Id == chapterId);

        if (chapter is null || string.IsNullOrWhiteSpace(chapter.FileName))
            return Results.NotFound(new { message = "Chapter not found." });

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
        return Results.Problem("Unable to download EPUB from Dropbox.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to load EPUB: {ex.Message}");
        return Results.Problem("Unable to read the EPUB file.");
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
        return Results.Problem("Unable to fetch cache status.");
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
        return Results.Problem("Unable to start indexing for this book.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

app.MapDelete("/api/anna/dropbox/epub/index", async (
    [FromQuery] string path) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Query parameter 'path' is required." });

    if (!path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only .epub files are supported." });

    try
    {
        var removed = DropboxEpubCache.DeleteCache(path);
        return Results.Ok(new { removed });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to delete cache: {ex.Message}");
        return Results.Problem("Unable to delete cache for this book.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5e) Summarize via OpenAI ────────────────────────────────────────────
app.MapPost("/api/ai/summarize", async (
    [FromBody] SummarizeRequest request,
    IConfiguration cfg) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Text))
        return Results.BadRequest(new { error = "Text is required." });

    // Check token limit
    var allowance = cfg.GetValue<long?>("OpenAI:MonthlyTokenAllowance");
    if (allowance.HasValue && OpenAiUsageTracker.IsOverLimit(allowance.Value))
    {
        return Results.Problem(
            detail: "Monthly token allowance has been exceeded. The service will reset at the beginning of next month.",
            statusCode: 429,
            title: "Token Limit Exceeded"
        );
    }

    var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Problem("OpenAI API key not configured.");

    try
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        http.DefaultRequestHeaders.Add("OpenAI-Beta", "responses=v1");
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
            reasoning = new { effort = "none" },
            max_output_tokens = 1000,
            temperature = 0.3
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
        return Results.Problem("Failed to summarize text.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5f) Learn more about a vocab term ──────────────────────────
app.MapPost("/api/ai/vocab/learn-more", async (
    [FromBody] LearnMoreRequest request,
    IConfiguration cfg) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Term))
        return Results.BadRequest(new { error = "Term is required." });

    // Check token limit
    var allowance = cfg.GetValue<long?>("OpenAI:MonthlyTokenAllowance");
    if (allowance.HasValue && OpenAiUsageTracker.IsOverLimit(allowance.Value))
    {
        return Results.Problem(
            detail: "Monthly token allowance has been exceeded. The service will reset at the beginning of next month.",
            statusCode: 429,
            title: "Token Limit Exceeded"
        );
    }

    var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Problem("OpenAI API key not configured.");

    try
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        http.DefaultRequestHeaders.Add("OpenAI-Beta", "responses=v1");
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
            reasoning = new { effort = "none" },
            max_output_tokens = 1200,
            temperature = 0.4
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
        return Results.Problem("Failed to fetch details.");
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
    IConfiguration cfg) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Term) || string.IsNullOrWhiteSpace(request.DropboxPath))
        return Results.BadRequest(new { error = "Term and dropboxPath are required." });

    // Check token limit
    var allowance = cfg.GetValue<long?>("OpenAI:MonthlyTokenAllowance");
    if (allowance.HasValue && OpenAiUsageTracker.IsOverLimit(allowance.Value))
    {
        return Results.Problem(
            detail: "Monthly token allowance has been exceeded. The service will reset at the beginning of next month.",
            statusCode: 429,
            title: "Token Limit Exceeded"
        );
    }

    var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Problem("OpenAI API key not configured.");

    try
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        http.DefaultRequestHeaders.Add("OpenAI-Beta", "responses=v1");

        var prompt = $@"You are a flashcard generator.
INPUT TEXT (may be a single term/phrase or a passage): ""{request.Term}""
CONTEXT: Book={request.BookTitle ?? "(none)"} | Source path={request.DropboxPath} | Existing definition={request.Definition ?? "(none)"} | Passage context={request.Context ?? "(none)"}

TASK:
- If the input is a single term/phrase, create ONE flashcard for it.
- If the input is a sentence or paragraph, identify 3-8 important terms/phrases/concepts from it and create a flashcard for EACH.

Return ONLY valid JSON, no markdown. Structure:
[
  {{ ""term"": ""..."", ""definition"": ""..."", ""etymology"": ""..."", ""usageExamples"": [""..."", ""...""], ""notes"": ""..."" }},
  ...
]

Rules:
- Definitions: 1-2 sentences; concise and clear.
- Usage examples: 2 short sentences (if none, provide 1).
- Etymology: short phrase; ""Unknown"" if not obvious.
- Notes: optional; use for key nuance/discipline-specific meaning; or """" if none.
- Prioritize terms that are uncommon, discipline-specific, or conceptually important.";

        var fullInput = prompt;
        var model = GetModelFast(cfg);

        var payload = new
        {
            model,
            input = fullInput,
            reasoning = new { effort = "none" },
            max_output_tokens = 600,
            temperature = 0.35
        };

        var response = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload);
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
            var promptTokens = usage.GetProperty("input_tokens").GetInt32();
            var completionTokens = usage.GetProperty("output_tokens").GetInt32();
            OpenAiUsageTracker.AddUsage(promptTokens, completionTokens);
        }

        List<FlashcardItem> cardsParsed;
        try
        {
            cardsParsed = JsonSerializer.Deserialize<List<FlashcardItem>>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new Exception("Invalid flashcard JSON array");
        }
        catch
        {
            try
            {
                var single = JsonSerializer.Deserialize<FlashcardItem>(content, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (single != null)
                    cardsParsed = new List<FlashcardItem> { single };
                else
                    throw new Exception("Invalid flashcard JSON");
            }
            catch
            {
                // Fallback: wrap text
                cardsParsed = new List<FlashcardItem>
                {
                    new FlashcardItem(
                        request.Term,
                        request.Definition ?? "Definition unavailable.",
                        "Etymology unavailable.",
                        new List<string> { content },
                        "Raw AI output could not be parsed."
                    )
                };
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
        return Results.Problem("Failed to create flashcard.");
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
        return Results.Problem("Failed to clear flashcards.");
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

static string GetChapterSummaryPath(string dropboxPath, int chapterId, out string cacheDir)
{
    cacheDir = Path.Combine(DropboxEpubCache.GetCacheRoot(), DropboxEpubCache.ComputeHashPublic(dropboxPath));
    Directory.CreateDirectory(cacheDir);
    return Path.Combine(cacheDir, $"chapter-summary-{chapterId}.json");
}

static async Task SaveChapterSummaryAsync(string dropboxPath, int chapterId, FullChapterSummaryResponse resp)
{
    var path = GetChapterSummaryPath(dropboxPath, chapterId, out _);
    var json = JsonSerializer.Serialize(resp, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    });
    await File.WriteAllTextAsync(path, json);
}

static async Task<FullChapterSummaryResponse?> LoadChapterSummaryAsync(string dropboxPath, int chapterId)
{
    var path = GetChapterSummaryPath(dropboxPath, chapterId, out _);
    if (!File.Exists(path)) return null;
    try
    {
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<FullChapterSummaryResponse>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }
    catch
    {
        return null;
    }
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
        return Results.Problem("Unable to search this book right now.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// ─── 5h) Full-chapter summary (chunked) ────────────────────────────────
app.MapPost("/api/ai/summarize/chapter", async (
    [FromBody] FullChapterSummaryRequest request,
    DropboxClient dropbox,
    IConfiguration cfg) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.DropboxPath))
        return Results.BadRequest(new { error = "dropboxPath is required." });
    if (request.ChapterId < 0)
        return Results.BadRequest(new { error = "chapterId must be zero or positive." });

    // Check token limit
    var allowance = cfg.GetValue<long?>("OpenAI:MonthlyTokenAllowance");
    if (allowance.HasValue && OpenAiUsageTracker.IsOverLimit(allowance.Value))
    {
        return Results.Problem(
            detail: "Monthly token allowance has been exceeded. The service will reset at the beginning of next month.",
            statusCode: 429,
            title: "Token Limit Exceeded"
        );
    }

    var apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Problem("OpenAI API key not configured.");

    try
    {
        var (index, cacheDir) = await DropboxEpubCache.GetOrBuildChapterIndexAsync(dropbox, request.DropboxPath);
        var chapter = index.Chapters.FirstOrDefault(c => c.Id == request.ChapterId);
        if (chapter is null || string.IsNullOrWhiteSpace(chapter.FileName))
            return Results.NotFound(new { message = "Chapter not found." });

        var chapterPath = Path.Combine(cacheDir, chapter.FileName);
        if (!File.Exists(chapterPath))
            await DropboxEpubCache.EnsureCacheBuildAsync(dropbox, request.DropboxPath, cacheDir);

        var content = await File.ReadAllTextAsync(chapterPath);
        if (string.IsNullOrWhiteSpace(content))
            return Results.BadRequest(new { error = "Chapter content is empty." });

        var chunks = SplitIntoChunks(content, 2000); // Larger chunks for better context
        var chunkSummaries = new List<string>();
        var promptTokensTotal = 0;
        var completionTokensTotal = 0;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        http.DefaultRequestHeaders.Add("OpenAI-Beta", "responses=v1");
        var model = GetModelDeep(cfg);

        // TIER 1: Detailed chunk summaries
        var chunkInstructions = @"You are an educational guide helping someone deeply understand complex texts. Analyze this passage with rich detail:

1. **What's Happening**: Summarize the main points, arguments, or narrative events
2. **Key Concepts**: Explain any philosophical, theoretical, or technical ideas being discussed - define terms as if for an intelligent reader unfamiliar with the subject
3. **Historical/Political Context**: If the author references events, movements, or debates, briefly explain what they're responding to or building upon
4. **Rhetorical Strategy**: How is the author making their case? (Examples, analogies, logical arguments, etc.)

Write 300-400 words. Be thorough and educational - assume the reader is intelligent but may not have extensive background in this specific area.";

        foreach (var chunk in chunks)
        {
            var chunkInput = $"{chunkInstructions}\n\n{chunk}";

            var payload = new
            {
                model,
                input = chunkInput,
                reasoning = new { effort = "medium" },
                max_output_tokens = 650,
                temperature = 0.3
            };

            var response = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ OpenAI chunk summary failed status={(int)response.StatusCode} body={body}");
                return Results.Problem($"OpenAI request failed while chunking: {(int)response.StatusCode}");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var chunkSummary = AiResponseParser.ExtractText(doc.RootElement) ?? string.Empty;

            chunkSummaries.Add(chunkSummary);

            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                promptTokensTotal += usage.GetProperty("input_tokens").GetInt32();
                completionTokensTotal += usage.GetProperty("output_tokens").GetInt32();
            }
        }

        // TIER 2: Section-level synthesis (group every 4 chunks)
        var sectionSummaries = new List<string>();
        var chunksPerSection = 4;

        for (var i = 0; i < chunkSummaries.Count; i += chunksPerSection)
        {
            var sectionChunks = chunkSummaries.Skip(i).Take(chunksPerSection).ToList();
            if (sectionChunks.Count == 0) continue;

            var sectionInstructions = @"You are synthesizing multiple passage analyses into a coherent section summary. Create a unified narrative that:

1. **Traces the Development**: How do the ideas/arguments/events progress through these passages?
2. **Identifies Core Themes**: What are the central concerns of this section?
3. **Contextualizes**: What intellectual traditions, historical debates, or prior thinkers is the author engaging with?
4. **Clarifies**: Explain difficult concepts in accessible terms

Write 400-500 words. Maintain educational depth while creating a flowing narrative.";

            var sectionInput = $"{sectionInstructions}\n\n{string.Join("\n\n---\n\n", sectionChunks)}";

            var sectionPayload = new
            {
                model,
                input = sectionInput,
                reasoning = new { effort = "medium" },
                max_output_tokens = 750,
                temperature = 0.35
            };

            var sectionResponse = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", sectionPayload);
            if (!sectionResponse.IsSuccessStatusCode)
            {
                var body = await sectionResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ OpenAI section summary failed status={(int)sectionResponse.StatusCode} body={body}");
                return Results.Problem($"OpenAI request failed for section summary: {(int)sectionResponse.StatusCode}");
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

        // TIER 3: Final chapter synthesis
        var finalInstructions = @"You are a master educator synthesizing a complete chapter analysis. Your reader is intelligent and eager to learn, but may not have deep background in this subject area.

Create a comprehensive chapter summary (700-900 words) that:

1. **Overview**: What is this chapter about? What is the author trying to accomplish?

2. **Historical & Intellectual Context**:
   - When was this written and what was happening politically/culturally?
   - What intellectual traditions or prior thinkers is the author building on or responding to?
   - What debates or conversations in the field does this engage with?

3. **Core Arguments & Ideas**:
   - What are the main claims or narrative developments?
   - Explain key philosophical/theoretical concepts in accessible terms
   - How does the author support their arguments?

4. **Significance & Interpretation**:
   - Why does this chapter matter in the broader work?
   - How have scholars or readers typically understood this?
   - What makes this important or interesting?

5. **Connections**:
   - How does this relate to other thinkers, movements, or texts?
   - What contemporary issues or questions does this illuminate?

Write as if teaching an intelligent student. Define specialized terms, explain references, and provide context that helps someone new to this material truly understand what's going on and why it matters. Be thorough and educational.";

        var userContent = $"Book context: {string.Join(" | ", contextParts)}\n\nSection summaries:\n{string.Join("\n\n---\n\n", sectionSummaries)}";
        var fullInput = $"{finalInstructions}\n\n{userContent}";

        var finalPrompt = new
        {
            model = model,
            input = fullInput,
            reasoning = new { effort = "high" },
            max_output_tokens = 1400,
            temperature = 0.3
        };

        var finalResponse = await http.PostAsJsonAsync("https://api.openai.com/v1/responses", finalPrompt);
        if (!finalResponse.IsSuccessStatusCode)
        {
            var body = await finalResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ OpenAI final summary failed status={(int)finalResponse.StatusCode} body={body}");
            return Results.Problem($"OpenAI request failed for final summary: {(int)finalResponse.StatusCode}");
        }

        using var finalStream = await finalResponse.Content.ReadAsStreamAsync();
        using var finalDoc = await JsonDocument.ParseAsync(finalStream);
        var finalSummary = AiResponseParser.ExtractText(finalDoc.RootElement) ?? "No summary returned.";

        if (finalDoc.RootElement.TryGetProperty("usage", out var finalUsage))
        {
            promptTokensTotal += finalUsage.GetProperty("input_tokens").GetInt32();
            completionTokensTotal += finalUsage.GetProperty("output_tokens").GetInt32();
        }

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

        var resp = new FullChapterSummaryResponse(
            finalSummary,
            promptTokensTotal,
            completionTokensTotal,
            promptTokensTotal + completionTokensTotal,
            percent,
            remaining,
            DateTime.UtcNow);

        return Results.Ok(resp);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Full-chapter summary failed: {ex.Message}");
        return Results.Problem("Failed to summarize chapter.");
    }
})
.RequireAuthorization()
.RequireRateLimiting("api");

// Retrieve cached full-chapter summary (if any)
app.MapGet("/api/ai/summarize/chapter", async (
    [FromQuery] string dropboxPath,
    [FromQuery] int chapterId) =>
{
    if (string.IsNullOrWhiteSpace(dropboxPath) || chapterId < 0)
        return Results.BadRequest(new { error = "dropboxPath and valid chapterId are required." });

    var cached = await LoadChapterSummaryAsync(dropboxPath, chapterId);
    if (cached == null)
        return Results.NotFound(new { message = "No summary cached for this chapter." });

    return Results.Ok(cached);
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

    var resp = new TokenUsageResponse(
        totals.Prompt,
        totals.Completion,
        totals.Total,
        allowance,
        percent,
        remaining,
        reset);
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

    if (title?.Length > 500)
        return Results.BadRequest(new { error = "Title too long. Maximum 500 characters." });

    if (string.IsNullOrWhiteSpace(target) || (target != "dad" && target != "mom"))
        return Results.BadRequest(new { error = "Invalid target. Must be 'dad' or 'mom'." });

    var doc = await anna.GetMemberDownloadDocumentAsync(md5, memberKey);

    string? downloadUrl = null;
    if (doc.TryGetProperty("download_url", out var du))
        downloadUrl = du.ValueKind == JsonValueKind.String
                    ? du.GetString()
                    : du.EnumerateArray().FirstOrDefault().GetString();

    if (string.IsNullOrEmpty(downloadUrl))
        return Results.Ok(new { success = false, message = "No download URL found.", accountFastInfo = (object?)null });

    AccountFastDownloadInfoDto? acctInfo = null;
    if (doc.TryGetProperty("account_fast_download_info", out var ai) &&
        ai.ValueKind == JsonValueKind.Object)
        acctInfo = new AccountFastDownloadInfoDto(
            ai.GetProperty("downloads_left").GetInt32(),
            ai.GetProperty("downloads_per_day").GetInt32());

    using var resp = await anna.HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
    if (!resp.IsSuccessStatusCode)
        return Results.Ok(new { success = false, status = (int)resp.StatusCode, accountFastInfo = acctInfo });

    var rawTitle  = !string.IsNullOrWhiteSpace(title) ? title : md5;
    var safeTitle = Regex.Replace(rawTitle, $"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]", "_");

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
    var tempFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{fileName}");

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
            $"Sent from Anna's Archive: {rawTitle}",
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


app.Run();

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
record FlashcardRequest(string Term, string? Definition, string? DropboxPath, string? BookTitle, string? Context);
record FlashcardItem(string Term, string Definition, string Etymology, List<string> UsageExamples, string? Notes);
record FlashcardResult(List<FlashcardItem> Cards);
record WikiImagesResponse(List<string> Images);
record SummarizeResponse(string Summary);
record FullChapterSummaryRequest(string DropboxPath, int ChapterId, string? BookTitle, string? Author, int? Year, string? Premise);
record FullChapterSummaryResponse(string Summary, int PromptTokens, int CompletionTokens, int TotalTokens, double? AllowanceUsedPercent, long? TokensRemaining, DateTime CachedAt);
record ChapterSummaryCacheResponse(string Summary, int PromptTokens, int CompletionTokens, int TotalTokens, DateTime CachedAt);
record TokenUsageResponse(long PromptTokens, long CompletionTokens, long TotalTokens, long? Allowance, double? AllowanceUsedPercent, long? TokensRemaining, DateTime? ResetsAtUtc);

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
            if (root.TryGetProperty("output", out var output) &&
                output.ValueKind == JsonValueKind.Array &&
                output.GetArrayLength() > 0)
            {
                var first = output[0];
                if (first.TryGetProperty("content", out var contentElem))
                {
                    if (contentElem.ValueKind == JsonValueKind.String)
                        return contentElem.GetString();
                    if (contentElem.ValueKind == JsonValueKind.Array && contentElem.GetArrayLength() > 0)
                    {
                        var firstContent = contentElem[0];
                        if (firstContent.ValueKind == JsonValueKind.String)
                            return firstContent.GetString();
                        if (firstContent.TryGetProperty("text", out var textElem) &&
                            textElem.ValueKind == JsonValueKind.String)
                            return textElem.GetString();
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
