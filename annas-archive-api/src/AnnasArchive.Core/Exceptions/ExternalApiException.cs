using System;
using System.Net;

namespace AnnasArchive.Core.Exceptions;

/// <summary>
/// Exception thrown when an external API call fails.
/// Includes information about whether the failure is transient (retryable).
/// </summary>
public class ExternalApiException : ServiceException
{
    public bool IsTransient { get; }
    public string? ApiEndpoint { get; }
    public HttpStatusCode? ApiStatusCode { get; }

    public ExternalApiException(string serviceName, string message, bool isTransient = true)
        : base(message, serviceName, HttpStatusCode.BadGateway)
    {
        IsTransient = isTransient;
    }

    public ExternalApiException(string serviceName, string message, HttpStatusCode apiStatusCode, bool isTransient = true)
        : base(message, serviceName, HttpStatusCode.BadGateway)
    {
        ApiStatusCode = apiStatusCode;
        IsTransient = isTransient;
    }

    public ExternalApiException(string serviceName, string message, string apiEndpoint, Exception innerException, bool isTransient = true)
        : base(message, serviceName, innerException, HttpStatusCode.BadGateway)
    {
        ApiEndpoint = apiEndpoint;
        IsTransient = isTransient;
    }

    public ExternalApiException(string serviceName, string message, string apiEndpoint, HttpStatusCode apiStatusCode, Exception innerException, bool isTransient = true)
        : base(message, serviceName, innerException, HttpStatusCode.BadGateway)
    {
        ApiEndpoint = apiEndpoint;
        ApiStatusCode = apiStatusCode;
        IsTransient = isTransient;
    }

    /// <summary>
    /// Determines if an HTTP status code represents a transient failure that should be retried.
    /// </summary>
    public static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.RequestTimeout ||
               statusCode == HttpStatusCode.TooManyRequests ||
               statusCode == HttpStatusCode.BadGateway ||
               statusCode == HttpStatusCode.ServiceUnavailable ||
               statusCode == HttpStatusCode.GatewayTimeout ||
               (int)statusCode >= 500;
    }
}
