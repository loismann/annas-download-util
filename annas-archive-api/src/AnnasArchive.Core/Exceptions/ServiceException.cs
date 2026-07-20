using System;
using System.Net;

namespace AnnasArchive.Core.Exceptions;

/// <summary>
/// Base exception for all service-level errors in the application.
/// </summary>
public class ServiceException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ServiceName { get; }

    public ServiceException(string message, HttpStatusCode statusCode = HttpStatusCode.InternalServerError)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public ServiceException(string message, string serviceName, HttpStatusCode statusCode = HttpStatusCode.InternalServerError)
        : base(message)
    {
        StatusCode = statusCode;
        ServiceName = serviceName;
    }

    public ServiceException(string message, Exception innerException, HttpStatusCode statusCode = HttpStatusCode.InternalServerError)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public ServiceException(string message, string serviceName, Exception innerException, HttpStatusCode statusCode = HttpStatusCode.InternalServerError)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ServiceName = serviceName;
    }
}
