using AnnasArchive.API.Configuration;
using AnnasArchive.API.Endpoints;

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

var app = builder.Build();

// ─── Development tools ───────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Anna's Archive v1"));
}

// ─── Middleware ──────────────────────────────────────────────────────────
app.UseCorrelationId();
app.UseGlobalExceptionHandler();
app.UseAppCors();
app.UseSecurityHeaders();
app.UseRequestBodySizeLimit();
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
app.MapGamingEndpoints();
app.MapMediaEndpoints();
app.MapQuizEndpoints();
app.MapVocabEndpoints();
app.MapDropboxReaderEndpoints();
app.MapLibraryEndpoints();
app.MapAiUsageEndpoints();
app.MapAiFlashcardsEndpoints();
app.MapAiVocabEndpoints();
app.MapAiBookSearchEndpoints();
app.MapAiCharacterEndpoints();
app.MapAiSummarizeEndpoints();
app.MapAiSectionSummaryEndpoints();
app.MapDevEndpoints();

app.Run();

// Make Program accessible to integration tests
public partial class Program { }
