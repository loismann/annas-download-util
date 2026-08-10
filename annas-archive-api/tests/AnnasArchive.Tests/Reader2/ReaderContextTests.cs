using System.Security.Claims;
using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Lenses;
using Microsoft.AspNetCore.Http;

namespace AnnasArchive.Tests.Reader2;

/// <summary>
/// Resolution happens once, at the edge. These tests pin the three ways it can
/// fail, because every one of them is a case an endpoint would otherwise invent
/// its own answer to.
/// </summary>
public class ReaderContextTests : IDisposable
{
    private readonly Reader2Fixture _f = new();
    private readonly ILensRegistry _lenses = new LensRegistry([new LiteraryLens(), new TestLens()]);

    public void Dispose() => _f.Dispose();

    private IReaderContextResolver Resolver() => new ReaderContextResolver(_f.Books, _lenses);

    private static HttpContext SignedInAs(string? userId)
    {
        var context = new DefaultHttpContext();

        if (userId is not null)
            context.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

        return context;
    }

    [Fact]
    public async Task A_signed_in_reader_gets_the_book_and_its_lens()
    {
        var book = await _f.EnrolAsync("context.epub", "contents", TestLens.LensKey);

        var result = await Resolver().ResolveAsync(book, SignedInAs("paul"));

        result.Failure.Should().BeNull();
        result.Context!.UserId.Should().Be("paul");
        result.Context.Lens.Key.Should().Be(TestLens.LensKey);
        result.Context.Ref.Should().Be(book);
        result.Context.Book.Title.Should().Be("A Book");
    }

    [Fact]
    public async Task An_unenrolled_book_is_reported_as_unknown()
    {
        var result = await Resolver().ResolveAsync(BookRef.Parse("0123456789abcdef"), SignedInAs("paul"));

        result.Context.Should().BeNull();
        result.Failure.Should().Be(ReaderContextFailure.UnknownBook);
    }

    [Fact]
    public async Task An_anonymous_request_is_refused_before_the_book_is_looked_up()
    {
        var book = await _f.EnrolAsync("anon.epub", "contents");

        var result = await Resolver().ResolveAsync(book, SignedInAs(null));

        result.Failure.Should().Be(ReaderContextFailure.NoUser);
    }

    /// <summary>
    /// A lens can be deleted from the code while books are still enrolled under
    /// it. Falling back to the default would reinterpret the book and serve one
    /// reading's artifacts as though they belonged to another.
    /// </summary>
    [Fact]
    public async Task A_book_enrolled_under_a_lens_that_no_longer_exists_is_an_error_not_a_default()
    {
        var book = await _f.EnrolAsync("retired.epub", "contents", "retired-lens");

        var result = await Resolver().ResolveAsync(book, SignedInAs("paul"));

        result.Context.Should().BeNull();
        result.Failure.Should().Be(ReaderContextFailure.UnknownLens);
    }
}
