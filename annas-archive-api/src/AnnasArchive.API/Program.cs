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
