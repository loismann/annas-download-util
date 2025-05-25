using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AnnasArchive.Core.Models;

public record SearchResponseDto(
    [property: JsonPropertyName("books")]        List<BookDto> Books,
    [property: JsonPropertyName("totalResults")] int TotalResults
);


