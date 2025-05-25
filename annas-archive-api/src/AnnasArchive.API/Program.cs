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
using Google.Apis.Drive.v3;
using Google.Apis.Services;

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

// ─── Google Drive (service-account) ──────────────────────────────────────
builder.Services.AddSingleton(provider =>
{
    var cfg  = provider.GetRequiredService<IConfiguration>();
    var key  = cfg["GoogleDrive:ServiceAccountKeyPath"];
    var cred = GoogleCredential.FromFile(key)
                               .CreateScoped(DriveService.ScopeConstants.DriveFile);

    return new DriveService(new BaseClientService.Initializer
    {
        HttpClientInitializer = cred,
        ApplicationName = "Anna-Boox-Proxy"
    });
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
});

// ─── 2) non-member download ──────────────────────────────────────────────
app.MapGet("/api/anna/book/{md5}/download", async (
    [FromRoute] string md5,
    AnnaArchiveService svc) =>
{
    var links = await svc.GetDownloadLinksAsync(md5);
    return links.Any()
        ? Results.Ok(new { id = md5, downloadLinks = links })
        : Results.NotFound(new { message = "No download links found." });
});

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
});

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
        var upload = drive.Files.Create(fileMeta, stream, resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
        upload.Fields = "id, webViewLink";
        await upload.UploadAsync();

        var driveFile = upload.ResponseBody;
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
        return Results.Ok(new
        {
            success         = false,
            message         = ex.Message,
            accountFastInfo = acctInfo
        });
    }
});

app.Run();
