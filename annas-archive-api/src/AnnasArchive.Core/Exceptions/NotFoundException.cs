using System.Net;

namespace AnnasArchive.Core.Exceptions;

/// <summary>
/// Exception thrown when a requested resource is not found.
/// </summary>
public class NotFoundException : ServiceException
{
    public string? ResourceType { get; }
    public string? ResourceId { get; }

    public NotFoundException(string message)
        : base(message, HttpStatusCode.NotFound)
    {
    }

    public NotFoundException(string resourceType, string resourceId)
        : base($"{resourceType} with ID '{resourceId}' was not found.", HttpStatusCode.NotFound)
    {
        ResourceType = resourceType;
        ResourceId = resourceId;
    }

    public NotFoundException(string resourceType, string resourceId, string additionalInfo)
        : base($"{resourceType} with ID '{resourceId}' was not found. {additionalInfo}", HttpStatusCode.NotFound)
    {
        ResourceType = resourceType;
        ResourceId = resourceId;
    }
}
