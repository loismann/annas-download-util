using AnnasArchive.API.Data;
using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// The most destructive path in the repo: it deletes files off disk, permanently,
/// with no undo and no bin. It had no test naming it.
///
/// <para>Two callers share it — the library's delete button and the daily review's
/// "delete" decision — so both get the same removal. What has to hold: it removes
/// <b>every</b> trace (file, sidecar, covers, personalization row, index entry), it
/// removes <b>only</b> the named book, and it cannot be talked into touching
/// anything outside the library root.</para>
///
/// <para>Runs in the <c>Sequential</c> collection because the library root comes
/// from the <c>LIBRARY_ROOT</c> environment variable, which is process-global —
/// setting it while another test class reads it would point that class at this
/// one's temp directory.</para>
/// </summary>
[Collection("Sequential")]
public sealed class LibraryBookDeletionHelperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "deletion-tests", Guid.NewGuid().ToString("N"));

    private readonly string? _previousRoot;
    private readonly string _coverDir;
    private readonly LibraryIndexCache _cache;
    private readonly BookPersonalizationStore _personalization;

    public LibraryBookDeletionHelperTests()
    {
        _coverDir = Path.Combine(_root, "_covers");
        Directory.CreateDirectory(_coverDir);

        _previousRoot = Environment.GetEnvironmentVariable("LIBRARY_ROOT");
        Environment.SetEnvironmentVariable("LIBRARY_ROOT", _root);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(_root, "app.db")
            })
            .Build();

        _personalization = new BookPersonalizationStore(new AppDatabase(config));
        _cache = new LibraryIndexCache();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LIBRARY_ROOT", _previousRoot);
        _cache.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    /// <summary>Lays down every trace a real book leaves: the file, its sidecar, and
    /// two covers (the code globs <c>{name}.cover.*</c>, so more than one is possible).</summary>
    private void GiveTheLibraryABook(string fileName)
    {
        File.WriteAllText(Path.Combine(_root, fileName), "the book itself");
        File.WriteAllText(Path.Combine(_root, fileName + ".meta.json"), "{}");
        File.WriteAllText(Path.Combine(_coverDir, fileName + ".cover.jpg"), "cover");
        File.WriteAllText(Path.Combine(_coverDir, fileName + ".cover.webp"), "cover");
    }

    private bool AnyTraceOf(string fileName) =>
        File.Exists(Path.Combine(_root, fileName)) ||
        File.Exists(Path.Combine(_root, fileName + ".meta.json")) ||
        Directory.GetFiles(_coverDir, fileName + ".cover.*").Length > 0;

    private LibraryDeletionResult Delete(string fileName) =>
        LibraryBookDeletionHelper.DeleteBookCompletely(fileName, _cache, _personalization);

    // ─── removing every trace ─────────────────────────────────────────────

    /// <summary>
    /// The whole promise. A sidecar or cover left behind is not cosmetic: the index
    /// builds from <c>*.meta.json</c>, so an orphaned sidecar puts the book straight
    /// back on the shelf as an entry whose file will not open.
    /// </summary>
    [Fact]
    public void Deleting_a_book_removes_the_file_the_sidecar_and_every_cover()
    {
        GiveTheLibraryABook("Dune.epub");

        Delete("Dune.epub").Found.Should().BeTrue();

        AnyTraceOf("Dune.epub").Should().BeFalse();
    }

    /// <summary>
    /// The personalization row goes too, and this is deliberate rather than tidiness:
    /// the store is keyed on file name, so re-downloading a book with the same name
    /// would otherwise inherit the deleted one's rating, tags and favourites.
    /// </summary>
    [Fact]
    public void Deleting_a_book_removes_its_personalization_so_a_later_book_cannot_inherit_it()
    {
        GiveTheLibraryABook("Dune.epub");
        _personalization.Update("Dune.epub", p => p.PersonalRating = 5);
        _personalization.Get("Dune.epub").Should().NotBeNull();

        Delete("Dune.epub");

        _personalization.Get("Dune.epub").Should().BeNull();
    }

    /// <summary>The index drops the entry incrementally — a delete must not cost a
    /// full rebuild, and must not leave the book on screen until one happens.</summary>
    [Fact]
    public void Deleting_a_book_drops_it_from_the_index_without_a_rebuild()
    {
        GiveTheLibraryABook("Dune.epub");
        _cache.GetBooks("").Should().ContainSingle(b => b.FileName == "Dune.epub");

        Delete("Dune.epub");

        _cache.IsCached.Should().BeTrue("the index was patched, not dropped");
        _cache.GetBooks("").Should().BeEmpty();
    }

    // ─── removing only the named book ─────────────────────────────────────

    /// <summary>
    /// Everything else in the library survives. The covers are found by globbing
    /// <c>{name}.cover.*</c>, which is the one place a careless pattern could reach
    /// past the book it was given.
    /// </summary>
    [Fact]
    public void Deleting_one_book_leaves_every_other_book_untouched()
    {
        GiveTheLibraryABook("Dune.epub");
        GiveTheLibraryABook("Dune Messiah.epub");
        GiveTheLibraryABook("Emma.epub");

        Delete("Dune.epub");

        AnyTraceOf("Dune.epub").Should().BeFalse();
        AnyTraceOf("Dune Messiah.epub").Should().BeTrue();
        AnyTraceOf("Emma.epub").Should().BeTrue();
    }

    /// <summary>
    /// A book whose name is a prefix of another's. Globbing <c>Dune.epub.cover.*</c>
    /// must not reach <c>Dune.epub.backup</c>'s covers — a delete that quietly takes a
    /// neighbour's artwork with it is the kind of thing nobody notices for months.
    /// </summary>
    [Fact]
    public void A_book_whose_name_extends_the_deleted_one_keeps_its_covers()
    {
        GiveTheLibraryABook("Dune.epub");
        GiveTheLibraryABook("Dune.epub.backup");

        Delete("Dune.epub");

        // Named specifically rather than through AnyTraceOf: that helper is an OR
        // across file, sidecar and covers, so the surviving book file alone would
        // satisfy it and a glob that swept up the neighbour's artwork would pass.
        Directory.GetFiles(_coverDir, "Dune.epub.backup.cover.*")
            .Should().HaveCount(2, "the neighbour's covers are not this book's to take");
        File.Exists(Path.Combine(_root, "Dune.epub.backup")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "Dune.epub.backup.meta.json")).Should().BeTrue();
    }

    // ─── refusing to reach outside the library ────────────────────────────

    /// <summary>
    /// The helper is handed a name, not a path, and reduces whatever it gets with
    /// <c>Path.GetFileName</c> before touching anything. Both callers validate first,
    /// but this is the layer actually holding the file handle, so it defends itself:
    /// a traversal collapses to a bare name inside the library root.
    /// </summary>
    [Theory]
    [InlineData("../outside.epub")]
    [InlineData("../../outside.epub")]
    [InlineData("/etc/outside.epub")]
    [InlineData("sub/dir/outside.epub")]
    public void A_path_is_reduced_to_a_bare_name_and_never_escapes_the_library(string attempt)
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "outside.epub");
        File.WriteAllText(outside, "not the library's to delete");

        try
        {
            Delete(attempt).Found.Should().BeFalse("no such book inside the library");
            File.Exists(outside).Should().BeTrue("nothing outside the root may be touched");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    /// <summary>The reduced name is still acted on, so the traversal deletes the
    /// in-library book of that name rather than silently doing nothing — the caller
    /// asked for a delete and gets one, just never outside the root.</summary>
    [Fact]
    public void A_traversal_that_names_a_real_book_deletes_the_one_inside_the_library()
    {
        GiveTheLibraryABook("Dune.epub");

        Delete("../../Dune.epub").Found.Should().BeTrue();

        AnyTraceOf("Dune.epub").Should().BeFalse();
    }

    // ─── reporting what it found ──────────────────────────────────────────

    /// <summary>
    /// Nothing to delete is reported, not invented. The endpoint turns this into a
    /// 404; answering "deleted" for a book that was never there would make a typo
    /// look like a successful destruction.
    /// </summary>
    [Fact]
    public void Deleting_a_book_that_is_not_there_reports_that_it_was_not_found()
    {
        Delete("Never Existed.epub").Found.Should().BeFalse();
    }

    /// <summary>
    /// A partial leftover still counts as found, so it can be cleaned up. An orphaned
    /// sidecar is the case that matters: the index is built from sidecars, so one left
    /// behind keeps a ghost entry on the shelf forever, and refusing to delete it
    /// would leave no way to remove that entry at all.
    /// </summary>
    [Theory]
    [InlineData("book")]
    [InlineData("sidecar")]
    [InlineData("cover")]
    public void Any_single_surviving_trace_is_enough_to_count_as_found(string trace)
    {
        var path = trace switch
        {
            "book" => Path.Combine(_root, "Ghost.epub"),
            "sidecar" => Path.Combine(_root, "Ghost.epub.meta.json"),
            _ => Path.Combine(_coverDir, "Ghost.epub.cover.jpg")
        };
        File.WriteAllText(path, "leftover");

        Delete("Ghost.epub").Found.Should().BeTrue();

        File.Exists(path).Should().BeFalse();
    }

    /// <summary>
    /// A cover that will not delete must not fail the delete. The book file is what
    /// the reader can still see; a stuck cover leaves a stray file, and the code
    /// deliberately logs it rather than aborting halfway with the book already gone
    /// and the caller told it failed.
    /// </summary>
    /// <remarks>
    /// The undeletable cover is made by dropping write permission on the covers
    /// directory. A first attempt used a <i>directory</i> named like a cover, which
    /// looked plausible and tested nothing: <c>Directory.GetFiles</c> only returns
    /// files, so the fake never entered the loop and the test passed with the
    /// try/catch removed. The precondition is asserted below for that reason — as
    /// root, unlink succeeds regardless and this test cannot do its job, which should
    /// be a loud failure rather than a green tick.
    /// </remarks>
    [Fact]
    public void A_cover_that_cannot_be_deleted_does_not_abort_the_rest()
    {
        GiveTheLibraryABook("Dune.epub");
        var cover = Path.Combine(_coverDir, "Dune.epub.cover.jpg");

        File.SetUnixFileMode(_coverDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var precondition = () => File.Delete(cover);
            precondition.Should().Throw<UnauthorizedAccessException>(
                "this test needs a cover that genuinely will not delete; as root it cannot have one");

            var delete = () => Delete("Dune.epub");

            delete.Should().NotThrow("a stuck cover is logged, not raised");
            File.Exists(Path.Combine(_root, "Dune.epub")).Should().BeFalse("the book still goes");
            File.Exists(Path.Combine(_root, "Dune.epub.meta.json")).Should().BeFalse();
        }
        finally
        {
            File.SetUnixFileMode(_coverDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
