using System.Net;

namespace AnnasArchive.Core.Exceptions;

/// <summary>
/// Exception thrown when authentication or authorization fails.
/// </summary>
public class UnauthorizedException : ServiceException
{
    public string? Reason { get; }

    public UnauthorizedException()
        : base("Unauthorized access.", HttpStatusCode.Unauthorized)
    {
    }

    public UnauthorizedException(string message)
        : base(message, HttpStatusCode.Unauthorized)
    {
    }

    public UnauthorizedException(string message, string reason)
        : base(message, HttpStatusCode.Unauthorized)
    {
        Reason = reason;
    }
}
