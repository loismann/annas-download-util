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
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddAuthorization();

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
    .WithOrigins("http://fs01pfbooks.synology.me", "http://localhost:4200")
    .AllowAnyHeader()
    .AllowAnyMethod());

// ─── security headers ────────────────────────────────────────────────
app.Use(async (context, next) =>
{
    context.Response.Headers.Add("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Add("X-Frame-Options", "DENY");
    context.Response.Headers.Add("X-XSS-Protection", "1; mode=block");
    context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'");
    context.Response.Headers.Add("Referrer-Policy", "strict-origin-when-cross-origin");
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

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
.RequireAuthorization();

// ─── 2) non-member download ──────────────────────────────────────────────
app.MapGet("/api/anna/book/{md5}/download", async (
    [FromRoute] string md5,
    AnnaArchiveService svc) =>
{
    var links = await svc.GetDownloadLinksAsync(md5);
    return links.Any()
        ? Results.Ok(new { id = md5, downloadLinks = links })
        : Results.NotFound(new { message = "No download links found." });
})
.RequireAuthorization();

// ─── 3) member download (url + counters) ─────────────────────────────────
app.MapGet("/api/anna/book/{md5}/download/member", async (
    [FromRoute] string md5,
    AnnaArchiveService svc) =>
{
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
.RequireAuthorization();

// ─── 4) send-to-boox (via Dropbox) ──────────────────────────────────────
app.MapPost("/api/anna/book/{md5}/send-to-boox", async (
    [FromRoute] string md5,
    [FromQuery] string? title,
    AnnaArchiveService anna,
    DropboxClient dropbox,
    IConfiguration cfg) =>
{
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
            message         = $"Dropbox upload failed: {ex.Message}",
            details         = details,
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
            message         = $"Dropbox upload failed: {ex.Message}",
            details         = $"StatusCode: {ex.StatusCode}, Uri: {ex.RequestUri}, Exception: {details}",
            accountFastInfo = acctInfo
        });
    }
    catch (DropboxException ex)
    {
        Console.WriteLine($"❌ Dropbox upload failed (DropboxException): {ex}");
        return Results.Ok(new
        {
            success         = false,
            message         = $"Dropbox upload failed: {ex.Message}",
            details         = ex.ToString(),
            accountFastInfo = acctInfo
        });
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"❌ Dropbox upload failed (HTTP): {ex}");
        return Results.Ok(new
        {
            success         = false,
            message         = $"Dropbox upload failed: {ex.Message}",
            details         = ex.ToString(),
            accountFastInfo = acctInfo
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Dropbox upload failed: {ex.Message}");

        return Results.Ok(new
        {
            success         = false,
            message         = ex.Message,
            accountFastInfo = acctInfo
        });
    }
})
.RequireAuthorization();

// ─── 5) send-to-kindle ───────────────────────────────────────────────────
app.MapPost("/api/anna/book/{md5}/send-to-kindle", async (
    [FromRoute] string md5,
    [FromQuery] string? title,
    AnnaArchiveService anna,
    IEmailService emailService,
    IConfiguration cfg) =>
{
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

        // Send email
        var kindleEmail = cfg["Email:KindleEmail"] ?? throw new InvalidOperationException("Email:KindleEmail not configured");
        await emailService.SendEmailWithAttachmentAsync(
            kindleEmail,
            "Book from Anna's Archive",
            $"Sent from Anna's Archive: {rawTitle}",
            tempFilePath,
            fileName);

        return Results.Ok(new
        {
            success         = true,
            message         = "Book sent to Kindle",
            accountFastInfo = acctInfo
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Send to Kindle failed: {ex.Message}");
        return Results.Ok(new
        {
            success         = false,
            message         = ex.Message,
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
.RequireAuthorization();

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

    // Find valid code
    var validCode = codes.FirstOrDefault(c =>
        c.Code.Equals(request.Code, StringComparison.OrdinalIgnoreCase));

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
});

app.Run();

// ─── DTOs ────────────────────────────────────────────────────────────────
record CodeLoginRequest(string Code);
record AccessCode(string Code, string Name, bool IsAdmin);
