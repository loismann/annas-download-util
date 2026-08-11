using AnnasArchive.API.Reader2.Domain;
using AnnasArchive.API.Reader2.Epub;
using AnnasArchive.API.Reader2.Lenses;
using Microsoft.AspNetCore.Mvc;
using static AnnasArchive.API.Reader2.Endpoints.Reader2Endpoints;

namespace AnnasArchive.API.Reader2.Endpoints;

/// <summary>The reader's shelf: what is enrolled, how it is being read, and removing it.</summary>
internal static class BookRoutes
{
    public static RouteGroupBuilder MapBookRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/books", HandleList);
        group.MapPost("/books", HandleEnrol);
        group.MapPatch("/books/{bookId}", HandleSetLens);
        group.MapDelete("/books/{bookId}", HandleRemove);

        return group;
    }

    private static async Task<IResult> HandleList(
        HttpContext context, IBookRegistry books, ILibraryBookSource library, CancellationToken ct) =>
        Results.Ok((await books.ListAsync(ct)).Select(b => Shelf(b, library, context)).ToArray());

    /// <summary>
    /// One shelf entry, with the library's cover attached.
    ///
    /// <para>Absolute, from the request's own scheme and host, because that is what
    /// the library page already serves and two conventions for one picture is how
    /// half of them end up broken behind a proxy.</para>
    /// </summary>
    private static BookResponse Shelf(
        EnrolledBook book, ILibraryBookSource library, HttpContext context) =>
        BookResponse.From(
            book,
            library.CoverUrl(book.FileName, $"{context.Request.Scheme}://{context.Request.Host}"));

    /// <summary>
    /// Enrols a library book.
    ///
    /// <para>Title and authors come from the EPUB rather than the request, so the
    /// shelf reads properly before anything is extracted and two clients cannot
    /// enrol the same book under different names. The file is opened twice — once
    /// to hash, once to read metadata — which is the honest cost of not caching
    /// something at enrolment that ingestion will read again anyway.</para>
    /// </summary>
    private static async Task<IResult> HandleEnrol(
        HttpContext context,
        [FromBody] EnrolBookRequest request,
        IBookRegistry books,
        ILibraryBookSource library,
        ContentHashCache hashes,
        ILensRegistry lenses,
        CancellationToken ct)
    {
        var fileName = request.FileName?.Trim();
        if (string.IsNullOrEmpty(fileName)) return BadRequest("A fileName is required.");

        // Omitting lensKey means "the default type"; naming one that does not exist
        // is refused rather than quietly defaulted.
        if (lenses.ForRequest(request.LensKey) is not { } lens)
            return UnknownLens(request.LensKey, lenses);

        var book = await hashes.GetAsync(fileName, ct);
        if (book is null) return NotFound($"'{fileName}' is not in the library.");

        await using var stream = library.OpenRead(fileName);
        if (stream is null) return NotFound($"'{fileName}' is not in the library.");

        BookMetadata metadata;
        try
        {
            metadata = BookMetadata.Read(stream, fileName);
        }
        catch (EpubException ex)
        {
            return BadRequest(ex.Message);
        }

        var enrolled = await books.EnrolAsync(
            book.Value, fileName, metadata.Title, metadata.Authors, lens.Key, ct);

        return Results.Ok(Shelf(enrolled, library, context));
    }

    /// <summary>
    /// Changes a book's type.
    ///
    /// <para>Existing artifacts are left alone. They are keyed by lens, so the old
    /// reading is still there if the reader switches back — and switching type is
    /// a decision about how to read next, not an instruction to throw away work
    /// somebody already paid for.</para>
    /// </summary>
    private static async Task<IResult> HandleSetLens(
        HttpContext context,
        string bookId,
        [FromBody] SetLensRequest request,
        IBookRegistry books,
        ILibraryBookSource library,
        ILensRegistry lenses,
        CancellationToken ct)
    {
        if (!TryBookRef(bookId, out var book, out var failure)) return failure;

        // No silent default here, unlike enrolment: an absent key on a PATCH means
        // the caller has lost track of what it is asking for.
        if (string.IsNullOrWhiteSpace(request.LensKey)) return BadRequest("A lensKey is required.");
        if (lenses.ForRequest(request.LensKey) is not { } lens) return UnknownLens(request.LensKey, lenses);

        if (!await books.SetLensAsync(book, lens.Key, ct)) return NotFound("No such book.");

        return Results.Ok(Shelf((await books.GetAsync(book, ct))!, library, context));
    }

    private static async Task<IResult> HandleRemove(
        string bookId, IBookRegistry books, CancellationToken ct)
    {
        if (!TryBookRef(bookId, out var book, out var failure)) return failure;

        return await books.RemoveAsync(book, ct)
            ? Results.NoContent()
            : NotFound("No such book.");
    }

    /// <summary>Names the types that do exist, so the caller can correct itself.</summary>
    private static IResult UnknownLens(string? key, ILensRegistry lenses) =>
        BadRequest(
            $"'{key}' is not a book type. Known types: {string.Join(", ", lenses.All.Select(l => l.Key))}.");
}
