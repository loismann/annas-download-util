using System.Globalization;
using System.Text.Json;

namespace AnnasArchive.API.Reader2.Storage;

/// <summary>
/// How Reader II writes to and reads from SQLite: one JSON configuration and one
/// timestamp format, shared by every store.
///
/// <para>Two stores with two serializer configurations is a trap rather than a
/// style question — the moment a payload gains a property, one store round-trips
/// it and the other silently drops it. Same for timestamps: a value written with
/// one format and parsed with another fails only for some inputs, which is worse
/// than failing for all of them.</para>
/// </summary>
internal static class StorageConventions
{
    /// <summary>
    /// Case-insensitive on read so a payload written before a property was
    /// renamed still loads, which keeps a rename from silently emptying a field.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Round-trip ("o") format: sortable as text, so <c>ORDER BY</c> on a
    /// timestamp column is chronological without parsing.</summary>
    public static string NowIso() => DateTime.UtcNow.ToString("o");

    public static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);
}
