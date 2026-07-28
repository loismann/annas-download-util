using AnnasArchive.API.Configuration;
using AnnasArchive.API.Endpoints;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ─── Configuration ───────────────────────────────────────────────────────
builder.Configuration
       .SetBasePath(Directory.GetCurrentDirectory())
       .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
       .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
       .AddEnvironmentVariables();

// ─── Logging ─────────────────────────────────────────────────────────────
builder.AddSerilogLogging();

// ─── Validate required configuration (fail fast) ─────────────────────────
builder.ValidateRequiredConfiguration();

// ─── Register all application services ───────────────────────────────────
builder.Services.AddApplicationServices(builder.Configuration);
builder.Services.AddHealthCheckServices(builder.Configuration);

// ─── Configure Kestrel for large file uploads ────────────────────────────
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 500 * 1024 * 1024; // 500MB
});

var app = builder.Build();

// ─── Development tools ───────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Anna's Archive v1"));
}

// ─── Middleware ──────────────────────────────────────────────────────────
app.UseCorrelationId();
// Emits one structured log line per request with the elapsed time as a real
// numeric field — this is what "endpoint duration over time" charts in Seq
// key off of, with zero manual instrumentation per endpoint.
app.UseSerilogRequestLogging();
app.UseGlobalExceptionHandler();
app.UseAppCors();

// Without explicit cache headers, browsers apply their own heuristic caching
// to every static file — including index.html — which can make a real
// deploy look like it never took effect: the browser keeps serving a stale
// index.html that still points at an old (now-deleted) hashed JS bundle
// filename. index.html and the runtime-generated version.json (see
// Dockerfile's runtime stage) must always be revalidated; everything else
// Angular emits is content-hashed by filename, so a changed file always gets
// a new name — safe to cache forever.
var noCacheStaticFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index.html", "version.json" };
var staticFileOptions = new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["Cache-Control"] = noCacheStaticFileNames.Contains(ctx.File.Name)
            ? "no-cache, no-store, must-revalidate"
            : "public, max-age=31536000, immutable";
    }
};
app.UseStaticFiles(staticFileOptions);
app.UseSecurityHeaders();
app.UseRequestBodySizeLimit(maxBodySize: 500 * 1024 * 1024); // 500MB to match upload endpoint
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseUserActivityTracking();

// ═══════════════════════════════════════════════════════════════════════════
// ═══ ENDPOINT MAPPINGS ═════════════════════════════════════════════════════
// ═══════════════════════════════════════════════════════════════════════════

app.MapAuthEndpoints();
app.MapAnnaDownloadEndpoints();
app.MapBookSearchEndpoints();
app.MapLibGenEndpoints();
if (app.Configuration.GetValue<bool>("Gaming:Enabled", false))
{
    app.MapGamingEndpoints();
}
app.MapMediaEndpoints();
app.MapYouTubeDownloadEndpoints();
app.MapSpotifyEndpoints();
app.MapVideoLibraryBrowserEndpoints();
app.MapVideoLibraryMetadataEndpoints();
app.MapQuizEndpoints();
app.MapVocabEndpoints();
app.MapDropboxReaderEndpoints();
app.MapLibraryKindleEndpoints();
app.MapLibraryCoverEndpoints();
app.MapLibraryBrowserEndpoints();
app.MapLibraryMetadataEndpoints();
app.MapLibraryReviewEndpoints();
app.MapLibraryReaderEndpoints();
app.MapLibraryUploadEndpoints();
app.MapAiUsageEndpoints();
app.MapAiFlashcardsEndpoints();
app.MapAiVocabEndpoints();
app.MapAiBookSearchEndpoints();
app.MapAiMediaSearchEndpoints();
app.MapAiCharacterEndpoints();
app.MapAiSummarizeEndpoints();
app.MapAiSectionSummaryEndpoints();
app.MapVpnSettingsEndpoints();
app.MapMediaRequestEndpoints();
app.MapDateNightEndpoints();
app.MapMediaLibraryEndpoints();
app.MapAudiobookLibraryEndpoints();
app.MapAudiobookEnrichmentEndpoints();
app.MapSystemStatsEndpoints();
app.MapDevEndpoints();
app.MapHealthCheckEndpoints();

// SPA fallback: any request that doesn't match an API route or a static file
// falls back to index.html so Angular's client-side router can handle it.
// Passing the same staticFileOptions ensures the no-cache header above still
// applies here too — this is the path actually hit for routes like /media,
// not the plain /index.html static file request.
app.MapFallbackToFile("index.html", staticFileOptions);

app.Run();

// Make Program accessible to integration tests
public partial class Program { }
