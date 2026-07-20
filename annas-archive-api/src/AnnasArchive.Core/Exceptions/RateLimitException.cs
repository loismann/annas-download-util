using System;
using System.Net;

namespace AnnasArchive.Core.Exceptions;

/// <summary>
/// Exception thrown when a rate limit is exceeded.
/// </summary>
public class RateLimitException : ExternalApiException
{
    public TimeSpan? RetryAfter { get; }
    public int? RemainingRequests { get; }

    public RateLimitException(string serviceName, string message)
        : base(serviceName, message, HttpStatusCode.TooManyRequests, isTransient: true)
    {
    }

    public RateLimitException(string serviceName, string message, TimeSpan retryAfter)
        : base(serviceName, message, HttpStatusCode.TooManyRequests, isTransient: true)
    {
        RetryAfter = retryAfter;
    }

    public RateLimitException(string serviceName, TimeSpan retryAfter)
        : base(serviceName, $"Rate limit exceeded. Retry after {retryAfter.TotalSeconds:F0} seconds.", HttpStatusCode.TooManyRequests, isTransient: true)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Creates a RateLimitException from a Retry-After header value.
    /// </summary>
    public static RateLimitException FromRetryAfterHeader(string serviceName, string? retryAfterHeader)
    {
        if (string.IsNullOrEmpty(retryAfterHeader))
        {
            return new RateLimitException(serviceName, "Rate limit exceeded.");
        }

        // Try parsing as seconds
        if (int.TryParse(retryAfterHeader, out var seconds))
        {
            return new RateLimitException(serviceName, TimeSpan.FromSeconds(seconds));
        }

        // Try parsing as HTTP date
        if (DateTimeOffset.TryParse(retryAfterHeader, out var date))
        {
            var retryAfter = date - DateTimeOffset.UtcNow;
            if (retryAfter > TimeSpan.Zero)
            {
                return new RateLimitException(serviceName, retryAfter);
            }
        }

        return new RateLimitException(serviceName, "Rate limit exceeded.");
    }
}
