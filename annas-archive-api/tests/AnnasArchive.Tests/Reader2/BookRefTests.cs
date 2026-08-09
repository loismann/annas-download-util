using System.Text;
using AnnasArchive.API.Reader2.Domain;

namespace AnnasArchive.Tests.Reader2;

public class BookRefTests
{
    private const string Digest = "A1B2C3D4E5F60718293A4B5C6D7E8F901234567890ABCDEF1234567890ABCDEF";

    [Fact]
    public void FromContentHash_takes_the_first_16_hex_characters_lowercased()
    {
        BookRef.FromContentHash(Digest).Value.Should().Be("a1b2c3d4e5f60718");
    }

    [Fact]
    public void FromContentHash_rejects_a_digest_that_is_too_short()
    {
        var act = () => BookRef.FromContentHash("abc123");
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// The whole reason identity is the content hash: renaming a library file
    /// must not orphan the book's artifacts, which is exactly what Reader I's
    /// path-derived cache key does.
    /// </summary>
    [Fact]
    public async Task Identical_bytes_under_different_names_produce_the_same_id()
    {
        var bytes = Encoding.UTF8.GetBytes("the same epub, twice");

        var first = await BookRef.FromStreamAsync(new MemoryStream(bytes));
        var second = await BookRef.FromStreamAsync(new MemoryStream(bytes));

        second.Should().Be(first);
    }

    [Fact]
    public async Task Different_bytes_produce_different_ids()
    {
        var a = await BookRef.FromStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes("war and peace")));
        var b = await BookRef.FromStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes("anna karenina")));

        b.Should().NotBe(a);
    }

    [Theory]
    [InlineData("a1b2c3d4e5f60718", true)]
    [InlineData("A1B2C3D4E5F60718", true)]
    [InlineData("a1b2c3d4e5f6071", false)]     // too short
    [InlineData("a1b2c3d4e5f607189", false)]   // too long
    [InlineData("a1b2c3d4e5f6071g", false)]    // not hex
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParse_accepts_only_16_hex_characters(string? value, bool expected)
    {
        BookRef.TryParse(value, out var book).Should().Be(expected);
        if (expected) book.Value.Should().Be(value!.ToLowerInvariant());
    }

    [Fact]
    public void Parse_is_case_insensitive_so_a_route_value_round_trips()
    {
        BookRef.Parse("A1B2C3D4E5F60718").Should().Be(BookRef.Parse("a1b2c3d4e5f60718"));
    }
}
