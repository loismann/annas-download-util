using Microsoft.Extensions.Configuration;

namespace AnnasArchive.API.Configuration;

/// <summary>
/// Appends <c>GoogleBooks:ApiKey</c> to every request on the named "GoogleBooks"
/// client.
///
/// Done here rather than at each call site because it was missing from three of the
/// four call paths — including <c>GoogleBooksService</c> in Core, which seven other
/// files depend on. Only <c>AudiobookEnrichmentService</c> ever sent it, so most
/// Google Books traffic was running on the anonymous quota. Putting it on the client
/// means a future caller cannot forget it, and keeps Core free of
/// <see cref="IConfiguration"/>.
///
/// No key configured is a supported state: Google Books answers anonymous requests,
/// just with a much lower quota, so this is a no-op rather than a failure.
/// </summary>
public sealed class GoogleBooksApiKeyHandler : DelegatingHandler
{
    private readonly string? _apiKey;

    public GoogleBooksApiKeyHandler(IConfiguration configuration)
    {
        _apiKey = configuration["GoogleBooks:ApiKey"];
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_apiKey) && request.RequestUri is not null)
        {
            var query = request.RequestUri.Query;

            // AudiobookEnrichmentService already appends its own — don't send two.
            // Matching on "key=" as a whole parameter avoids tripping on a query
            // that merely contains the word (e.g. "...&q=monkey=...").
            var alreadyPresent = query.Contains("?key=", StringComparison.OrdinalIgnoreCase)
                              || query.Contains("&key=", StringComparison.OrdinalIgnoreCase);

            if (!alreadyPresent)
            {
                var separator = string.IsNullOrEmpty(query) ? "?" : "&";
                request.RequestUri = new Uri(
                    $"{request.RequestUri}{separator}key={Uri.EscapeDataString(_apiKey)}");
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
