#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AnnasArchive.Core.Models;

/// <summary>
/// Minimal DTO sent to the Angular front-end.  
/// Top-level positional parameters are required by every search result;
/// optional fields live in the object-initializer section.
/// The global JSON options in <see cref="Program.cs"/> convert the
/// Pascal-case property names to camel-case automatically.
/// </summary>
public record BookDto(
    string Title,
    string Md5,
    List<string> Authors,
    string Language,
    string Format,
    string Source,
    string FileSize,
    string BookType,
    string Publisher,
    int?   PublicationYear,
    double? BaseScore,
    double? FinalScore
)
{
    /// <summary>13- or 10-digit ISBN; omitted when not available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Isbn { get; set; }

    /// <summary>Ordered list of URLs to try for the cover image.</summary>
    public List<string> CoverCandidates { get; init; } = new();
}

#nullable restore
