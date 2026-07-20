using AnnasArchive.API.Helpers;
using AnnasArchive.Core.Services;
using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Service for fetching book descriptions from multiple sources with cascading fallback.
/// Consolidates the common pattern of trying Google Books -> Open Library -> GPT-4.
/// </summary>
public class DescriptionFetcherService : IDescriptionFetcherService
{
    private readonly IGoogleBooksService _googleBooksService;
    private readonly IOpenLibraryService _openLibraryService;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOpenAiModelHelper _modelHelper;
    private readonly IAiResponseParser _aiResponseParser;
    private readonly IModelSelectionService _modelSelection;

    public DescriptionFetcherService(
        IGoogleBooksService googleBooksService,
        IOpenLibraryService openLibraryService,
        IHttpClientFactory httpFactory,
        IOpenAiModelHelper modelHelper,
        IAiResponseParser aiResponseParser,
        IModelSelectionService modelSelection)
    {
        _googleBooksService = googleBooksService;
        _openLibraryService = openLibraryService;
        _httpFactory = httpFactory;
        _modelHelper = modelHelper;
        _aiResponseParser = aiResponseParser;
        _modelSelection = modelSelection;
    }

    public async Task<DescriptionFetchResult> FetchDescriptionAsync(
        string title,
        string? author = null,
        string? isbn = null,
        bool includeAiFallback = true)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new DescriptionFetchResult(null, null);

        Log.Information("[DescriptionFetcher] Starting cascade for '{Title}' by '{Author}'", title, author ?? "unknown");

        // 1. Try Google Books
        var result = await FetchFromGoogleBooksAsync(title, author, isbn);
        if (!string.IsNullOrWhiteSpace(result.Description))
        {
            Log.Information("[DescriptionFetcher] Found description from Google Books");
            return result;
        }

        // 2. Try Open Library
        result = await FetchFromOpenLibraryAsync(title, author, isbn);
        if (!string.IsNullOrWhiteSpace(result.Description))
        {
            Log.Information("[DescriptionFetcher] Found description from Open Library");
            return result;
        }

        // 3. Fall back to GPT-4 if enabled
        if (includeAiFallback)
        {
            result = await FetchFromAiAsync(title, author);
            if (!string.IsNullOrWhiteSpace(result.Description))
            {
                Log.Information("[DescriptionFetcher] Generated description from GPT-4");
                return result;
            }
        }

        Log.Information("[DescriptionFetcher] No description found for '{Title}'", title);
        return new DescriptionFetchResult(null, null);
    }

    public async Task<DescriptionFetchResult> FetchFromGoogleBooksAsync(
        string title,
        string? author = null,
        string? isbn = null)
    {
        try
        {
            var description = await _googleBooksService.GetBookDescriptionAsync(title, author ?? "", isbn);
            return new DescriptionFetchResult(description, string.IsNullOrWhiteSpace(description) ? null : "Google Books");
        }
        catch (Exception ex)
        {
            Log.Warning("[DescriptionFetcher] Google Books lookup failed: {Message}", ex.Message);
            return new DescriptionFetchResult(null, null);
        }
    }

    public async Task<DescriptionFetchResult> FetchFromOpenLibraryAsync(
        string title,
        string? author = null,
        string? isbn = null)
    {
        try
        {
            var description = await _openLibraryService.GetBookDescriptionAsync(title, author ?? "", isbn);
            return new DescriptionFetchResult(description, string.IsNullOrWhiteSpace(description) ? null : "Open Library");
        }
        catch (Exception ex)
        {
            Log.Warning("[DescriptionFetcher] Open Library lookup failed: {Message}", ex.Message);
            return new DescriptionFetchResult(null, null);
        }
    }

    public async Task<DescriptionFetchResult> FetchFromAiAsync(string title, string? author = null)
    {
        try
        {
            using var http = _httpFactory.CreateClient("OpenAI");
            var model = _modelSelection.GetModelFast();

            var description = await AiDescriptionHelpers.GenerateNoSpoilerDescriptionAsync(
                title,
                author ?? "Unknown",
                http,
                model,
                _modelHelper,
                _aiResponseParser);

            return new DescriptionFetchResult(
                string.IsNullOrWhiteSpace(description) ? null : description,
                string.IsNullOrWhiteSpace(description) ? null : "GPT-4");
        }
        catch (Exception ex)
        {
            Log.Warning("[DescriptionFetcher] GPT-4 generation failed: {Message}", ex.Message);
            return new DescriptionFetchResult(null, null);
        }
    }
}
