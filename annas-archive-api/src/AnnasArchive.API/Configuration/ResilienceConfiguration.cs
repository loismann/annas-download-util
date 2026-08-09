using System.Net;
using AnnasArchive.API.Constants;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;
using Polly.Timeout;
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
    public static IHttpClientBuilder AddStandardResilience(
        this IHttpClientBuilder builder,
        string serviceName,
        TimeSpan? requestTimeout = null)
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
                OnRetry = args => LogRetry(serviceName, args)
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
            resilienceBuilder.AddTimeout(requestTimeout ?? HttpTimeouts.StandardApiTimeout);
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
                ShouldHandle = args => ShouldRetryAiCallAsync(args.Outcome),
                OnRetry = args => LogRetry(serviceName, args)
            });

            // Circuit breaker with higher threshold for AI services
            resilienceBuilder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(60),
                MinimumThroughput = 3,
                BreakDuration = TimeSpan.FromSeconds(60),
                ShouldHandle = args => ShouldRetryAiCallAsync(args.Outcome),
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
    /// Resilience for clients that proxy browser-driven media traffic (covers,
    /// audio/video streaming) alongside catalog calls — currently Audiobookshelf.
    /// NO CIRCUIT BREAKER: a single catalog page load fires hundreds of proxied
    /// cover requests that the browser freely aborts, and any breaker shared with
    /// the catalog calls turns that normal churn into "the whole section is down
    /// for 30s+". Retry + per-attempt timeout only; the timeout caps time-to-
    /// headers, so long-running audio streams (ResponseHeadersRead) are unaffected.
    /// </summary>
    public static IHttpClientBuilder AddMediaProxyResilience(this IHttpClientBuilder builder, string serviceName)
    {
        builder.AddResilienceHandler($"{serviceName}-resilience", (resilienceBuilder) =>
        {
            resilienceBuilder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args.Outcome)),
                OnRetry = args => LogRetry(serviceName, args)
            });

            resilienceBuilder.AddTimeout(HttpTimeouts.StandardApiTimeout);
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
    /// The one retry line, shared by all three strategies that have one.
    ///
    /// The exception goes to Serilog as an exception rather than into the
    /// template, so Seq records its type, stack and inner exceptions instead of
    /// just the sentence. <c>Reason</c> therefore names the *kind* of failure —
    /// the status code when the attempt produced a response, the exception type
    /// when it threw — and does not repeat the message that is already attached.
    /// </summary>
    private static ValueTask LogRetry(string serviceName, OnRetryArguments<HttpResponseMessage> args)
    {
        Log.Warning(args.Outcome.Exception,
            "[{ServiceName}] Retry attempt {AttemptNumber} after {Delay}ms. Reason: {Reason}",
            serviceName,
            args.AttemptNumber,
            args.RetryDelay.TotalMilliseconds,
            args.Outcome.Result?.StatusCode.ToString()
                ?? args.Outcome.Exception?.GetType().Name
                ?? "Unknown");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The error codes OpenAI returns, inside a 429, for conditions no amount of
    /// waiting resolves.
    /// </summary>
    private static readonly string[] NonTransientQuotaCodes =
    {
        "insufficient_quota",
        "credit_balance_exhausted",
        "billing_hard_limit_reached",
        "billing_not_active"
    };

    /// <summary>
    /// Retry rule for AI calls. Identical to <see cref="ShouldRetry"/> except for
    /// 429, which OpenAI overloads for two unrelated conditions: a real rate limit
    /// (transient — back off and it clears) and an exhausted credit balance
    /// (permanent — retrying cannot produce credits). Only the error body tells
    /// them apart.
    ///
    /// Treating both as transient is what turned "you have no credits remaining"
    /// into "the circuit is now open and is not allowing calls": three attempts
    /// per chunk, all failing, tripped the breaker inside one chapter — and since
    /// every AI feature shares the "OpenAI" client, that breaker then blanked
    /// flashcards, quiz and vocab for a minute too, for a billing problem no
    /// message ever named. Reading the body costs nothing on the success path;
    /// it only runs on a 429.
    /// </summary>
    private static async ValueTask<bool> ShouldRetryAiCallAsync(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Result is { StatusCode: HttpStatusCode.TooManyRequests } rateLimited)
            return !await IsQuotaExhaustedAsync(rateLimited);

        return ShouldRetry(outcome);
    }

    /// <summary>
    /// True when a 429 is a billing failure rather than a rate limit. An
    /// unreadable body falls back to "transient", the behaviour before this
    /// existed — a broken guess must not make a recoverable call unrecoverable.
    /// </summary>
    private static async ValueTask<bool> IsQuotaExhaustedAsync(HttpResponseMessage response)
    {
        try
        {
            // ReadAsStringAsync buffers, so the caller can still read the body.
            var body = await response.Content.ReadAsStringAsync();
            return NonTransientQuotaCodes.Any(code =>
                body.Contains(code, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Determines if a response should trigger a retry.
    /// </summary>
    private static bool ShouldRetry(Outcome<HttpResponseMessage> outcome)
    {
        // Retry on exceptions (network errors, timeouts, etc.).
        //
        // A canceled request only counts as a failure when it's HttpClient's own
        // timeout (TaskCanceledException wrapping a TimeoutException) or the
        // pipeline's timeout strategy (TimeoutRejectedException). A plain
        // TaskCanceledException means the *caller* aborted — which browsers do
        // routinely to proxied cover/stream requests (scrolling away, seeking) —
        // and treating those as failures poisoned the circuit breaker, taking
        // whole sections down for 30s+ while the upstream service was healthy.
        if (outcome.Exception != null)
        {
            return outcome.Exception is HttpRequestException ||
                   outcome.Exception is TimeoutRejectedException ||
                   outcome.Exception is TimeoutException ||
                   (outcome.Exception is TaskCanceledException tce && tce.InnerException is TimeoutException);
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
