using System.Security.Cryptography;

namespace AnnasArchive.API.Reader2.Domain;

/// <summary>
/// A book's identity, which is a prefix of the SHA-256 of the EPUB's bytes.
///
/// <para>Deliberately <b>not</b> the file path. Reader I keys its cache on the
/// sanitised path, so renaming a book in the library orphans every summary it
/// has; here a rename costs nothing and two copies of one book share a single
/// set of artifacts and one text extraction.</para>
///
/// <para>16 hex characters is 64 bits. At household scale — thousands of books,
/// not billions — the collision probability is negligible, and the shorter id
/// keeps directory names and log lines readable.</para>
/// </summary>
public readonly record struct BookRef(string Value)
{
    private const int HexLength = 16;

    /// <summary>Derives the id from a full hex SHA-256 digest.</summary>
    public static BookRef FromContentHash(string sha256Hex)
    {
        if (string.IsNullOrWhiteSpace(sha256Hex) || sha256Hex.Length < HexLength)
            throw new ArgumentException(
                $"Content hash must be at least {HexLength} hex characters.", nameof(sha256Hex));

        return new BookRef(sha256Hex[..HexLength].ToLowerInvariant());
    }

    /// <summary>Hashes a stream and derives the id. The stream is read to the end.</summary>
    public static async Task<BookRef> FromStreamAsync(Stream stream, CancellationToken ct = default)
    {
        var digest = await SHA256.HashDataAsync(stream, ct);
        return FromContentHash(Convert.ToHexString(digest));
    }

    /// <summary>Parses an id that has already been shortened (from the database or a route).</summary>
    public static BookRef Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != HexLength ||
            !value.All(Uri.IsHexDigit))
            throw new ArgumentException(
                $"Book id must be {HexLength} hex characters.", nameof(value));

        return new BookRef(value.ToLowerInvariant());
    }

    /// <summary>Non-throwing <see cref="Parse"/>, for route values that arrive from outside.</summary>
    public static bool TryParse(string? value, out BookRef book)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Length == HexLength &&
            value.All(Uri.IsHexDigit))
        {
            book = new BookRef(value.ToLowerInvariant());
            return true;
        }

        book = default;
        return false;
    }

    public override string ToString() => Value;
}
