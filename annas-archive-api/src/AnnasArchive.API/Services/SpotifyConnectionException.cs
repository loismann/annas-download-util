using System.Net;

namespace AnnasArchive.API.Services;

public sealed class SpotifyConnectionException : InvalidOperationException
{
    public SpotifyConnectionException(
        string message,
        string state,
        HttpStatusCode statusCode = HttpStatusCode.Conflict,
        TimeSpan? retryAfter = null)
        : base(message)
    {
        State = state;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public string State { get; }
    public HttpStatusCode StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
}
