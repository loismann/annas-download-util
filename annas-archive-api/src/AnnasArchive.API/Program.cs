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
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using System.Threading;
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
        ValidateIssuer = false,
        ValidateAudience = false,
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

// ─── google drive service (OAuth 2.0) - lazy initialization ──────────────
DriveService? driveServiceInstance = null;
object driveLock = new object();

builder.Services.AddSingleton(provider =>
{
    lock (driveLock)
    {
        if (driveServiceInstance != null)
            return driveServiceInstance;

        var cfg = provider.GetRequiredService<IConfiguration>();
        var tokenPath = cfg["GoogleDrive:TokenPath"];

        if (File.Exists(tokenPath))
        {
            Console.WriteLine($"✅ Found existing OAuth token at {tokenPath}");
            var tokenJson = File.ReadAllText(tokenPath);
            var tokenData = JsonSerializer.Deserialize<JsonElement>(tokenJson);

            var tokenResponse = new TokenResponse
            {
                AccessToken = tokenData.GetProperty("access_token").GetString(),
                RefreshToken = tokenData.GetProperty("refresh_token").GetString(),
                ExpiresInSeconds = tokenData.GetProperty("expires_in").GetInt64(),
                Scope = tokenData.GetProperty("scope").GetString(),
                TokenType = tokenData.GetProperty("token_type").GetString(),
                IssuedUtc = DateTime.UtcNow.AddSeconds(-10) // Assume token was just issued
            };

            var secrets = JsonSerializer.Deserialize<JsonElement>(
                File.ReadAllText(cfg["GoogleDrive:OAuthClientPath"]!));
            var web = secrets.GetProperty("web");

            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = web.GetProperty("client_id").GetString(),
                    ClientSecret = web.GetProperty("client_secret").GetString()
                },
                Scopes = new[] { DriveService.ScopeConstants.DriveFile }
            });

            var credential = new UserCredential(flow, "user", tokenResponse);

            driveServiceInstance = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Anna-Boox-Proxy"
            });

            Console.WriteLine("✅ Google Drive OAuth credentials loaded successfully");
        }
        else
        {
            Console.WriteLine($"⚠️  No OAuth token found. Please visit /api/auth/google to authorize.");
            driveServiceInstance = null!;
        }

        return driveServiceInstance!;
    }
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

app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

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

// ─── 4) send-to-drive ────────────────────────────────────────────────────
app.MapPost("/api/anna/book/{md5}/send-to-drive", async (
    [FromRoute] string md5,
    [FromQuery] string? title,
    AnnaArchiveService anna,
    DriveService drive,
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

    using var stream = await resp.Content.ReadAsStreamAsync();

    var fileMeta = new Google.Apis.Drive.v3.Data.File
    {
        Name    = fileName,
        Parents = new[] { cfg["GoogleDrive:UploadFolderId"] }
    };

    try
    {
        Console.WriteLine($"Attempting to upload file '{fileName}' to Google Drive folder '{cfg["GoogleDrive:UploadFolderId"]}'");
        
        var upload = drive.Files.Create(fileMeta, stream, resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
        upload.Fields = "id, webViewLink";
        
        Console.WriteLine("Starting Google Drive upload...");
        var uploadResult = await upload.UploadAsync();
        
        Console.WriteLine($"Upload result status: {uploadResult.Status}");
        
        if (uploadResult.Status != Google.Apis.Upload.UploadStatus.Completed)
        {
            var errorMessage = uploadResult.Exception?.Message ?? $"Upload failed with status: {uploadResult.Status}";
            Console.WriteLine($"❌ Google Drive upload failed: {errorMessage}");
            return Results.Ok(new
            {
                success         = false,
                message         = errorMessage,
                accountFastInfo = acctInfo
            });
        }

        var driveFile = upload.ResponseBody;
        if (driveFile == null)
        {
            Console.WriteLine("❌ Google Drive upload completed but response body is null");
            return Results.Ok(new
            {
                success         = false,
                message         = "Upload completed but no file information returned",
                accountFastInfo = acctInfo
            });
        }
        
        Console.WriteLine($"✅ Google Drive upload successful! File ID: {driveFile.Id}, Link: {driveFile.WebViewLink}");
        
        return Results.Ok(new
        {
            success         = true,
            driveFileId     = driveFile.Id,
            driveFileLink   = driveFile.WebViewLink,
            accountFastInfo = acctInfo
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Google Drive upload failed: {ex.Message}");
        Console.WriteLine($"Exception type: {ex.GetType().Name}");
        Console.WriteLine($"Stack trace: {ex.StackTrace}");
        
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
    var tempFilePath = Path.Combine(Path.GetTempPath(), fileName);

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

// ─── 6) OAuth endpoints ──────────────────────────────────────────────────
app.MapGet("/api/auth/google", (IConfiguration cfg) =>
{
    var secrets = JsonSerializer.Deserialize<JsonElement>(
        File.ReadAllText(cfg["GoogleDrive:OAuthClientPath"]!));
    var web = secrets.GetProperty("web");
    var clientId = web.GetProperty("client_id").GetString();
    var redirectUri = web.GetProperty("redirect_uris").EnumerateArray().First().GetString();

    var authUrl = $"https://accounts.google.com/o/oauth2/auth?" +
                  $"client_id={Uri.EscapeDataString(clientId!)}" +
                  $"&redirect_uri={Uri.EscapeDataString(redirectUri!)}" +
                  $"&response_type=code" +
                  $"&scope={Uri.EscapeDataString(DriveService.ScopeConstants.DriveFile)}" +
                  $"&access_type=offline" +
                  $"&prompt=consent";

    return Results.Redirect(authUrl);
});

app.MapGet("/oauth2callback", async ([FromQuery] string code, IConfiguration cfg) =>
{
    if (string.IsNullOrEmpty(code))
        return Results.BadRequest(new { error = "No authorization code received" });

    var secrets = JsonSerializer.Deserialize<JsonElement>(
        File.ReadAllText(cfg["GoogleDrive:OAuthClientPath"]!));
    var web = secrets.GetProperty("web");
    var clientId = web.GetProperty("client_id").GetString();
    var clientSecret = web.GetProperty("client_secret").GetString();
    var redirectUri = web.GetProperty("redirect_uris").EnumerateArray().First().GetString();

    // Exchange code for token
    var httpClient = new HttpClient();
    var tokenRequest = new Dictionary<string, string>
    {
        ["code"] = code,
        ["client_id"] = clientId!,
        ["client_secret"] = clientSecret!,
        ["redirect_uri"] = redirectUri!,
        ["grant_type"] = "authorization_code"
    };

    var tokenResponse = await httpClient.PostAsync(
        "https://oauth2.googleapis.com/token",
        new FormUrlEncodedContent(tokenRequest));

    var tokenJson = await tokenResponse.Content.ReadAsStringAsync();

    if (!tokenResponse.IsSuccessStatusCode)
        return Results.BadRequest(new { error = "Failed to exchange code for token", details = tokenJson });

    // Save token
    var tokenPath = cfg["GoogleDrive:TokenPath"];
    File.WriteAllText(tokenPath!, tokenJson);

    // Reset the DriveService singleton so it picks up the new token
    driveServiceInstance = null;

    return Results.Content(@"
        <html>
        <body style='font-family: sans-serif; padding: 40px; text-align: center;'>
            <h1>✅ Authorization Successful!</h1>
            <p>Your Google Drive has been connected.</p>
            <p>You can close this window and try uploading a book again.</p>
        </body>
        </html>
    ", "text/html");
});

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

// ─── 7) Password hash generator (temporary utility endpoint) ─────────────
app.MapGet("/api/auth/hash-password", ([FromQuery] string password) =>
{
    if (string.IsNullOrEmpty(password))
        return Results.BadRequest(new { error = "Password parameter required" });

    var hash = BCrypt.Net.BCrypt.HashPassword(password);
    return Results.Ok(new { password, hash });
});

app.Run();

// ─── DTOs ────────────────────────────────────────────────────────────────
record CodeLoginRequest(string Code);
record AccessCode(string Code, string Name, bool IsAdmin);
