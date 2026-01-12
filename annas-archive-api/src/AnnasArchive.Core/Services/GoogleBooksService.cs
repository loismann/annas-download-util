using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace AnnasArchive.Core.Services;

public class GoogleBooksService : IGoogleBooksService
{
    private readonly IHttpClientFactory _httpFactory;

    public GoogleBooksService(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public async Task<string?> GetBookDescriptionAsync(string title, string author, string? isbn = null)
    {
        try
        {
            var http = _httpFactory.CreateClient("GoogleBooks");

            // Build query - prefer ISBN if available
            string query;
            if (!string.IsNullOrWhiteSpace(isbn))
            {
                query = $"isbn:{Uri.EscapeDataString(isbn)}";
            }
            else
            {
                query = $"intitle:{Uri.EscapeDataString(title)}+inauthor:{Uri.EscapeDataString(author)}";
            }

            var url = $"https://www.googleapis.com/books/v1/volumes?q={query}";

            var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[GoogleBooks] API request failed with status {response.StatusCode}");
                return null;
            }

            var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();

            // Check if we have items in the response
            if (doc?.RootElement.TryGetProperty("items", out var items) == true &&
                items.ValueKind == JsonValueKind.Array &&
                items.GetArrayLength() > 0)
            {
                // Get the first item
                var firstItem = items[0];

                if (firstItem.TryGetProperty("volumeInfo", out var volumeInfo) &&
                    volumeInfo.TryGetProperty("description", out var description) &&
                    description.ValueKind == JsonValueKind.String)
                {
                    var desc = description.GetString();
                    if (!string.IsNullOrWhiteSpace(desc))
                    {
                        Console.WriteLine($"[GoogleBooks] ✓ Found description for '{title}' by {author}");
                        return desc;
                    }
                }
            }

            Console.WriteLine($"[GoogleBooks] No description found for '{title}' by {author}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GoogleBooks] Error: {ex.Message}");
            return null;
        }
    }
}
