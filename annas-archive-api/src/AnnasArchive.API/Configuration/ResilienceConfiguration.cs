using System.Net;
using AnnasArchive.API.Constants;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Serilog;

namespace AnnasArchive.API.Configuration;

/// <summary>
/// Configuration for resilience policies (retry, circuit breaker, timeout) for HTTP clients.
/// </summary>
public static class ResilienceConfiguration
{
    /// <summary>
    /// Adds standard resilience handler to an HTTP client builder.
    /// Includes retry (3 attempts with exponential backoff), circuit breaker, and timeout.
    /// </summary>
    public static IHttpClientBuilder AddStandardResilience(this IHttpClientBuilder builder, string serviceName)
    {
        builder.AddResilienceHandler($"{serviceName}-resilience", (resilienceBuilder) =>
        {
            // Retry policy: 3 retries with exponential backoff
            resilienceBuilder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args.Outcome)),
                OnRetry = args =>
                {
                    Log.Warning("[{ServiceName}] Retry attempt {AttemptNumber} after {Delay}ms. Reason: {Reason}",
                        serviceName,
                        args.AttemptNumber,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message ?? args.Outcome.Result?.StatusCode.ToString() ?? "Unknown");
                    return ValueTask.CompletedTask;
                }
            });

            // Circuit breaker: Opens after 5 failures in 30 seconds, stays open for 30 seconds
            resilienceBuilder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args.Outcome)),
                OnOpened = args =>
                {
                    Log.Warning("[{ServiceName}] Circuit breaker OPENED. Will remain open for {BreakDuration}s",
                        serviceName, args.BreakDuration.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    Log.Information("[{ServiceName}] Circuit breaker CLOSED. Service recovered.", serviceName);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    Log.Information("[{ServiceName}] Circuit breaker HALF-OPENED. Testing service...", serviceName);
                    return ValueTask.CompletedTask;
                }
            });

            // Request timeout (per-request, not total)
            resilienceBuilder.AddTimeout(HttpTimeouts.StandardApiTimeout);
        });

        return builder;
    }

    /// <summary>
    /// Adds resilience handler optimized for AI/LLM services with longer timeouts.
    /// </summary>
    public static IHttpClientBuilder AddAiResilience(this IHttpClientBuilder builder, string serviceName)
    {
        builder.AddResilienceHandler($"{serviceName}-resilience", (resilienceBuilder) =>
        {
            // Retry policy: 2 retries with longer backoff for AI services
            resilienceBuilder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(2),
                ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args.Outcome)),
                OnRetry = args =>
                {
                    Log.Warning("[{ServiceName}] Retry attempt {AttemptNumber} after {Delay}ms. Reason: {Reason}",
                        serviceName,
                        args.AttemptNumber,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message ?? args.Outcome.Result?.StatusCode.ToString() ?? "Unknown");
                    return ValueTask.CompletedTask;
                }
            });

            // Circuit breaker with higher threshold for AI services
            resilienceBuilder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(60),
                MinimumThroughput = 3,
                BreakDuration = TimeSpan.FromSeconds(60),
                ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args.Outcome)),
                OnOpened = args =>
                {
                    Log.Warning("[{ServiceName}] Circuit breaker OPENED. AI service unavailable for {BreakDuration}s",
                        serviceName, args.BreakDuration.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    Log.Information("[{ServiceName}] Circuit breaker CLOSED. AI service recovered.", serviceName);
                    return ValueTask.CompletedTask;
                }
            });

            // Longer timeout for AI operations
            resilienceBuilder.AddTimeout(HttpTimeouts.AiOperationTimeout);
        });

        return builder;
    }

    /// <summary>
    /// Adds resilience handler for scraping services with domain fallback support.
    /// NO CIRCUIT BREAKER - scraping services have their own multi-domain fallback mechanism.
    /// A circuit breaker would block ALL domains when one fails, defeating the fallback logic.
    /// </summary>
    public static IHttpClientBuilder AddScrapingResilience(this IHttpClientBuilder builder, string serviceName)
    {
        builder.AddResilienceHandler($"{serviceName}-resilience", (resilienceBuilder) =>
        {
            // No retry at Polly level - the service's domain fallback handles retries
            // Adding retries here would just retry the same failing domain before fallback kicks in

            // Request timeout only - let the domain fallback logic handle failures
            resilienceBuilder.AddTimeout(HttpTimeouts.ScrapingTimeout);
        });

        return builder;
    }

    /// <summary>
    /// Determines if a response should trigger a retry.
    /// </summary>
    private static bool ShouldRetry(Outcome<HttpResponseMessage> outcome)
    {
        // Retry on exceptions (network errors, timeouts, etc.)
        if (outcome.Exception != null)
        {
            return outcome.Exception is HttpRequestException ||
                   outcome.Exception is TaskCanceledException ||
                   outcome.Exception is TimeoutException;
        }

        // Retry on transient HTTP status codes
        if (outcome.Result != null)
        {
            var statusCode = outcome.Result.StatusCode;
            return statusCode == HttpStatusCode.RequestTimeout ||
                   statusCode == HttpStatusCode.TooManyRequests ||
                   statusCode == HttpStatusCode.BadGateway ||
                   statusCode == HttpStatusCode.ServiceUnavailable ||
                   statusCode == HttpStatusCode.GatewayTimeout;
        }

        return false;
    }
}
