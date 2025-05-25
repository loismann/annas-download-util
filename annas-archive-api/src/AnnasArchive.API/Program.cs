using System;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using AnnasArchive.Core.Models;
using AnnasArchive.Core.Services;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// ── Read the member key from config or environment
//    e.g. set environment variable ANNA_MEMBER_KEY=your-key
// var memberKey = builder.Configuration["ANNA_MEMBER_KEY"]
//     ?? throw new InvalidOperationException("Missing ANNA_MEMBER_KEY.");

var memberKey = "";

// ── Swagger ───────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo {
        Title = "Anna’s Archive Proxy API",
        Version = "v1"
    });
});

// ── HTTP client for Anna’s Archive ────────────────────────────────────────
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

// ── Serialization + CORS ──────────────────────────────────────────────────
builder.Services.ConfigureHttpJsonOptions(o =>
        o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.AddCors();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Anna’s Archive v1"));
}

app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
// app.UseHttpsRedirection(); // comment out during local dev if needed

// ── 1) Search ─────────────────────────────────────────────────────────────
app.MapGet("/api/anna/book", async (
    [FromQuery(Name = "name")] string name,
    AnnaArchiveService svc,
    [FromQuery(Name = "exact")] bool exact = false
) =>
{
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { error = "Query parameter 'name' is required." });

    var books = (await svc.SearchAsync(name, 10)).ToList();

    if (exact)
        books = books
            .Where(b => string.Equals(b.Title?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

    if (!books.Any())
        return Results.NotFound(new { message = "No books found matching that name." });

    return books.Count == 1 ? Results.Ok(books[0]) : Results.Ok(books);
});

// ── 2) Non-member download (unchanged) ─────────────────────────────────────
app.MapGet("/api/anna/book/{md5}/download", async (
    [FromRoute] string md5,
    AnnaArchiveService svc) =>
{
    var links = await svc.GetDownloadLinksAsync(md5);
    return links.Any()
        ? Results.Ok(new { Id = md5, DownloadLinks = links })
        : Results.NotFound(new { message = "No download links found." });
});

// ── Member download: redirect to the real S3 URL ─────────────────────────
app.MapGet("/api/anna/book/{md5}/download/member", async (
    [FromRoute] string md5,
    AnnaArchiveService svc) =>
{
    // 1) fetch the raw JSON doc (with download_url + account_fast_download_info)
    var doc = await svc.GetMemberDownloadDocumentAsync(md5, memberKey);

    // 2) pull out the download URL (string or first element of array)
    string? downloadUrl = null;
    if (doc.TryGetProperty("download_url", out var du))
    {
        if (du.ValueKind == JsonValueKind.String)
            downloadUrl = du.GetString();
        else if (du.ValueKind == JsonValueKind.Array)
            downloadUrl = du.EnumerateArray()
                            .FirstOrDefault()
                            .GetString();
    }

    // 3) pull out the account info
    AccountFastDownloadInfoDto? acctInfo = null;
    if (doc.TryGetProperty("account_fast_download_info", out var ai) &&
        ai.ValueKind == JsonValueKind.Object)
    {
        var left   = ai.GetProperty("downloads_left").GetInt32();
        var perDay = ai.GetProperty("downloads_per_day").GetInt32();
        acctInfo = new AccountFastDownloadInfoDto(left, perDay);
    }

    if (string.IsNullOrEmpty(downloadUrl))
        return Results.NotFound(new { message = "No download URL found." });

    // 4) send back both the URL and the updated counter
    return Results.Ok(new
    {
        downloadUrl,
        accountFastInfo = acctInfo
    });
});


app.Run();
