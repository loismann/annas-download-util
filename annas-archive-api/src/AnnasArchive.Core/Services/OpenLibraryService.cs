using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Http;

namespace AnnasArchive.Core.Services;

public class OpenLibraryService : IOpenLibraryService
{
    private readonly IHttpClientFactory _httpFactory;

    public OpenLibraryService(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public async Task<string?> GetBookDescriptionAsync(string title, string author, string? isbn = null)
    {
        // Step 1: Try to find the book using Search API
        var workKey = await FindWorkKeyAsync(title, author, isbn);
        if (workKey == null)
        {
            Console.WriteLine($"[OpenLibrary] Could not find work key for '{title}' by {author}");
            return null;
        }

        // Step 2: Try to get description from Works API
        var description = await TryGetWorkDescriptionAsync(workKey);
        if (!string.IsNullOrWhiteSpace(description))
        {
            Console.WriteLine($"[OpenLibrary] Found description from Works API for '{title}'");
            return description;
        }

        // Step 3: Try to get description from first edition
        var editionKey = await FindEditionKeyAsync(workKey);
        if (editionKey != null)
        {
            description = await TryGetEditionDescriptionAsync(editionKey);
            if (!string.IsNullOrWhiteSpace(description))
            {
                Console.WriteLine($"[OpenLibrary] Found description from Edition API for '{title}'");
                return description;
            }
        }

        // Step 4: Try to get first_sentence from Search API
        // NOTE: Skipping Books API excerpts as they often contain actual book text rather than summaries
        description = await TryGetFirstSentenceAsync(title, author);
        if (!string.IsNullOrWhiteSpace(description))
        {
            Console.WriteLine($"[OpenLibrary] Found first_sentence from Search API for '{title}'");
            return description;
        }

        Console.WriteLine($"[OpenLibrary] No description found for '{title}' by {author}");
        return null;
    }

    private async Task<string?> FindWorkKeyAsync(string title, string author, string? isbn)
    {
        try
        {
            var http = _httpFactory.CreateClient("OpenLibrary");

            // If we have an ISBN, try that first (most accurate)
            if (!string.IsNullOrWhiteSpace(isbn))
            {
                var isbnUrl = $"https://openlibrary.org/isbn/{isbn}.json";
                var isbnResponse = await http.GetAsync(isbnUrl);
                if (isbnResponse.IsSuccessStatusCode)
                {
                    var isbnDoc = await isbnResponse.Content.ReadFromJsonAsync<JsonDocument>();
                    if (isbnDoc?.RootElement.TryGetProperty("works", out var works) == true &&
                        works.ValueKind == JsonValueKind.Array &&
                        works.GetArrayLength() > 0)
                    {
                        var firstWork = works[0];
                        if (firstWork.TryGetProperty("key", out var keyProp))
                        {
                            return keyProp.GetString();
                        }
                    }
                }
            }

            // Fallback to search API
            var searchQuery = $"title:{Uri.EscapeDataString(title)} author:{Uri.EscapeDataString(author)}";
            var searchUrl = $"https://openlibrary.org/search.json?q={searchQuery}&fields=key&limit=1";

            var response = await http.GetAsync(searchUrl);
            if (!response.IsSuccessStatusCode)
                return null;

            var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
            if (doc?.RootElement.TryGetProperty("docs", out var docs) == true &&
                docs.ValueKind == JsonValueKind.Array &&
                docs.GetArrayLength() > 0)
            {
                var firstDoc = docs[0];
                if (firstDoc.TryGetProperty("key", out var keyProp))
                {
                    return keyProp.GetString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenLibrary] Error finding work key: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> TryGetWorkDescriptionAsync(string workKey)
    {
        try
        {
            var http = _httpFactory.CreateClient("OpenLibrary");
            var url = $"https://openlibrary.org{workKey}.json";

            var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
            if (doc?.RootElement.TryGetProperty("description", out var descProp) == true)
            {
                // Description can be a string or an object with a "value" property
                if (descProp.ValueKind == JsonValueKind.String)
                {
                    return descProp.GetString();
                }
                else if (descProp.ValueKind == JsonValueKind.Object &&
                         descProp.TryGetProperty("value", out var valueProp))
                {
                    return valueProp.GetString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenLibrary] Error getting work description: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> FindEditionKeyAsync(string workKey)
    {
        try
        {
            var http = _httpFactory.CreateClient("OpenLibrary");
            var url = $"https://openlibrary.org{workKey}.json";

            var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();

            // Try to get first edition from editions array
            if (doc?.RootElement.TryGetProperty("editions", out var editions) == true &&
                editions.ValueKind == JsonValueKind.Array &&
                editions.GetArrayLength() > 0)
            {
                var firstEdition = editions[0];
                if (firstEdition.TryGetProperty("key", out var keyProp))
                {
                    return keyProp.GetString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenLibrary] Error finding edition key: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> TryGetEditionDescriptionAsync(string editionKey)
    {
        try
        {
            var http = _httpFactory.CreateClient("OpenLibrary");
            var url = $"https://openlibrary.org{editionKey}.json";

            var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
            if (doc?.RootElement.TryGetProperty("description", out var descProp) == true)
            {
                // Description can be a string or an object with a "value" property
                if (descProp.ValueKind == JsonValueKind.String)
                {
                    return descProp.GetString();
                }
                else if (descProp.ValueKind == JsonValueKind.Object &&
                         descProp.TryGetProperty("value", out var valueProp))
                {
                    return valueProp.GetString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenLibrary] Error getting edition description: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> TryGetExcerptsAsync(string isbn)
    {
        try
        {
            var http = _httpFactory.CreateClient("OpenLibrary");
            var url = $"https://openlibrary.org/api/books?bibkeys=ISBN:{isbn}&jscmd=data&format=json";

            var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
            var bibkey = $"ISBN:{isbn}";

            if (doc?.RootElement.TryGetProperty(bibkey, out var bookData) == true &&
                bookData.TryGetProperty("excerpts", out var excerpts) &&
                excerpts.ValueKind == JsonValueKind.Array &&
                excerpts.GetArrayLength() > 0)
            {
                var firstExcerpt = excerpts[0];
                if (firstExcerpt.TryGetProperty("text", out var textProp))
                {
                    return textProp.GetString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenLibrary] Error getting excerpts: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> TryGetFirstSentenceAsync(string title, string author)
    {
        try
        {
            var http = _httpFactory.CreateClient("OpenLibrary");
            var searchQuery = $"title:{Uri.EscapeDataString(title)} author:{Uri.EscapeDataString(author)}";
            var url = $"https://openlibrary.org/search.json?q={searchQuery}&fields=first_sentence&limit=1";

            var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
            if (doc?.RootElement.TryGetProperty("docs", out var docs) == true &&
                docs.ValueKind == JsonValueKind.Array &&
                docs.GetArrayLength() > 0)
            {
                var firstDoc = docs[0];
                if (firstDoc.TryGetProperty("first_sentence", out var sentenceProp))
                {
                    // first_sentence can be a string or an array
                    if (sentenceProp.ValueKind == JsonValueKind.String)
                    {
                        return sentenceProp.GetString();
                    }
                    else if (sentenceProp.ValueKind == JsonValueKind.Array &&
                             sentenceProp.GetArrayLength() > 0)
                    {
                        return sentenceProp[0].GetString();
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenLibrary] Error getting first sentence: {ex.Message}");
            return null;
        }
    }
}
