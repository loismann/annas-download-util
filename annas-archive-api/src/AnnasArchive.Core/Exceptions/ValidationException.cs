using System;
using System.Collections.Generic;
using System.Net;

namespace AnnasArchive.Core.Exceptions;

/// <summary>
/// Exception thrown when input validation fails.
/// </summary>
public class ValidationException : ServiceException
{
    public IDictionary<string, string[]>? Errors { get; }

    public ValidationException(string message)
        : base(message, HttpStatusCode.BadRequest)
    {
    }

    public ValidationException(string message, IDictionary<string, string[]> errors)
        : base(message, HttpStatusCode.BadRequest)
    {
        Errors = errors;
    }

    public ValidationException(string field, string error)
        : base($"Validation failed for {field}: {error}", HttpStatusCode.BadRequest)
    {
        Errors = new Dictionary<string, string[]>
        {
            { field, new[] { error } }
        };
    }
}
