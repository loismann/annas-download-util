using AnnasArchive.API.Helpers;
using AnnasArchive.API.Reader2.Lenses;

namespace AnnasArchive.API.Reader2.Domain;

/// <summary>
/// Everything below the HTTP edge needs to know about one request: which book,
/// read through which lens, by whom.
///
/// <para>Resolved once at the edge and passed down. Nothing deeper re-derives
/// identity, looks up a lens key, or touches a file path — the architecture test
/// enforces that, because a second place that decides which book this is, is
/// exactly how Reader I ended up with twelve path builders.</para>
/// </summary>
public sealed record ReaderContext(EnrolledBook Book, IReaderLens Lens, string UserId)
{
    public BookRef Ref => Book.Book;
}

/// <summary>Why a book could not be opened, in the terms the HTTP edge answers in.</summary>
public enum ReaderContextFailure
{
    /// <summary>No such enrolled book.</summary>
    UnknownBook,

    /// <summary>The book is enrolled under a lens that is no longer registered.</summary>
    UnknownLens,

    /// <summary>Nobody is signed in.</summary>
    NoUser
}

/// <summary>The resolved context, or the reason there isn't one.</summary>
public sealed record ReaderContextResult(ReaderContext? Context, ReaderContextFailure? Failure)
{
    public static ReaderContextResult Ok(ReaderContext context) => new(context, null);
    public static ReaderContextResult Failed(ReaderContextFailure failure) => new(null, failure);
}

public interface IReaderContextResolver
{
    Task<ReaderContextResult> ResolveAsync(BookRef book, HttpContext http, CancellationToken ct = default);
}

/// <summary>
/// Composes the registry and the lenses into a <see cref="ReaderContext"/>.
///
/// <para>Thin on purpose. It exists so that "which book, which lens, which user"
/// is answered in one place with one set of failure modes, rather than each
/// endpoint doing two lookups and inventing its own idea of what a missing lens
/// means.</para>
/// </summary>
public sealed class ReaderContextResolver(IBookRegistry books, ILensRegistry lenses) : IReaderContextResolver
{
    public async Task<ReaderContextResult> ResolveAsync(
        BookRef book, HttpContext http, CancellationToken ct = default)
    {
        var userId = UserHelpers.GetUserIdFromContext(http);
        if (string.IsNullOrEmpty(userId)) return ReaderContextResult.Failed(ReaderContextFailure.NoUser);

        var enrolled = await books.GetAsync(book, ct);
        if (enrolled is null) return ReaderContextResult.Failed(ReaderContextFailure.UnknownBook);

        // A lens can be removed from the code while books are still enrolled under
        // it. Falling back to the default would silently reinterpret the book and
        // serve artifacts generated under one reading as though they belonged to
        // another, so this is an error the reader is told about.
        return lenses.TryGet(enrolled.LensKey, out var lens)
            ? ReaderContextResult.Ok(new ReaderContext(enrolled, lens, userId))
            : ReaderContextResult.Failed(ReaderContextFailure.UnknownLens);
    }
}
