using AnnasArchive.API.Helpers;
using AnnasArchive.API.Services;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// The video library's delete: the same shape as the book delete, minus a
/// personalization row (a video's editable metadata lives in its own sidecar, which
/// goes with it) and with a thumbnail in place of covers.
///
/// <para>Thumbnails are addressed by the video's <b>base name</b>, which caused two
/// problems. Deleting only the first matching extension orphaned the others — fixed,
/// since it needed no migration. Two videos differing only in container sharing one
/// thumbnail is <i>not</i> fixed: that is the naming convention itself, and changing
/// it would orphan every thumbnail already on disk. It is pinned below as current
/// behaviour so the decision stays a decision.</para>
///
/// <para>In the <c>Sequential</c> collection: the video root comes from the
/// <c>YOUTUBE_DOWNLOAD_ROOT</c> environment variable, which is process-global.</para>
/// </summary>
[Collection("Sequential")]
public sealed class VideoDeletionHelperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "video-deletion-tests", Guid.NewGuid().ToString("N"));

    private readonly string? _previousRoot;
    private readonly VideoIndexCache _cache;

    public VideoDeletionHelperTests()
    {
        Directory.CreateDirectory(_root);
        _previousRoot = Environment.GetEnvironmentVariable("YOUTUBE_DOWNLOAD_ROOT");
        Environment.SetEnvironmentVariable("YOUTUBE_DOWNLOAD_ROOT", _root);
        _cache = new VideoIndexCache();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("YOUTUBE_DOWNLOAD_ROOT", _previousRoot);
        _cache.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    private void GiveTheLibraryAVideo(string fileName, string thumbnailExtension = ".jpg")
    {
        File.WriteAllText(Path.Combine(_root, fileName), "the video itself");
        File.WriteAllText(Path.Combine(_root, fileName + ".meta.json"), "{}");
        File.WriteAllText(
            Path.Combine(_root, Path.GetFileNameWithoutExtension(fileName) + thumbnailExtension),
            "thumbnail");
    }

    private bool Exists(string relative) => File.Exists(Path.Combine(_root, relative));

    private bool Delete(string fileName) =>
        VideoDeletionHelper.DeleteVideoCompletely(fileName, _cache);

    // ─── removing every trace ─────────────────────────────────────────────

    /// <summary>
    /// The sidecar matters as much as the file: the video index builds from
    /// <c>*.meta.json</c>, so one left behind puts the video back on the shelf as an
    /// entry that will not play.
    /// </summary>
    [Fact]
    public void Deleting_a_video_removes_the_file_the_sidecar_and_the_thumbnail()
    {
        GiveTheLibraryAVideo("Dune.mp4");

        Delete("Dune.mp4").Should().BeTrue();

        Exists("Dune.mp4").Should().BeFalse();
        Exists("Dune.mp4.meta.json").Should().BeFalse();
        Exists("Dune.jpg").Should().BeFalse();
    }

    /// <summary>Every thumbnail extension the discovery helper searches is one the
    /// delete has to be able to remove, or deleting a webp-thumbnailed video leaves
    /// its artwork behind.</summary>
    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".webp")]
    [InlineData(".png")]
    public void A_thumbnail_is_removed_whichever_of_the_known_extensions_it_uses(string extension)
    {
        GiveTheLibraryAVideo("Dune.mp4", extension);

        Delete("Dune.mp4").Should().BeTrue();

        Exists("Dune" + extension).Should().BeFalse();
    }

    /// <summary>The index drops the entry incrementally rather than invalidating.</summary>
    [Fact]
    public void Deleting_a_video_drops_it_from_the_index_without_a_rebuild()
    {
        GiveTheLibraryAVideo("Dune.mp4");
        _cache.GetVideos("").Should().ContainSingle(v => v.FileName == "Dune.mp4");

        Delete("Dune.mp4");

        _cache.IsCached.Should().BeTrue("the index was patched, not dropped");
        _cache.GetVideos("").Should().BeEmpty();
    }

    /// <summary>Other videos are untouched.</summary>
    [Fact]
    public void Deleting_one_video_leaves_the_others_alone()
    {
        GiveTheLibraryAVideo("Dune.mp4");
        GiveTheLibraryAVideo("Arrival.mp4");

        Delete("Dune.mp4");

        Exists("Arrival.mp4").Should().BeTrue();
        Exists("Arrival.mp4.meta.json").Should().BeTrue();
        Exists("Arrival.jpg").Should().BeTrue();
    }

    // ─── two consequences of naming thumbnails by base name ───────────────

    /// <summary>
    /// <b>Two videos differing only in container share one thumbnail, and deleting
    /// either takes it from both.</b>
    ///
    /// <para>A thumbnail is <c>{base name}.jpg</c>, so <c>Dune.mp4</c> and
    /// <c>Dune.mkv</c> both resolve to <c>Dune.jpg</c> — in
    /// <c>VideoHelpers.FindLocalThumbnailUrl</c> as well as here. Keeping a re-encode
    /// alongside its original is enough to hit it: delete one and the other loses its
    /// artwork, silently, with no error.</para>
    ///
    /// <para>Pinned as the <i>current</i> behaviour, not as desirable. The convention
    /// is at least consistent between discovery and deletion, and changing it would
    /// orphan every thumbnail already on disk — so it is a decision to take
    /// deliberately, not a line to quietly edit.</para>
    /// </summary>
    [Fact]
    public void Two_videos_with_the_same_base_name_share_a_thumbnail_and_one_delete_takes_it()
    {
        GiveTheLibraryAVideo("Dune.mp4");
        File.WriteAllText(Path.Combine(_root, "Dune.mkv"), "the re-encode");
        File.WriteAllText(Path.Combine(_root, "Dune.mkv.meta.json"), "{}");

        Delete("Dune.mp4");

        Exists("Dune.mkv").Should().BeTrue("the other video itself survives");
        Exists("Dune.mkv.meta.json").Should().BeTrue();
        Exists("Dune.jpg").Should().BeFalse(
            "the shared thumbnail went with the first video — this is the trap, not the intent");
    }

    /// <summary>
    /// <b>Every format goes, not just the one being displayed.</b>
    ///
    /// <para>This was the other half of the base-name problem, and unlike the shared
    /// thumbnail it was fixable with no migration: discovery stops at the first
    /// extension it finds, so deleting only that one left any other orphaned — where
    /// it silently became the thumbnail for the next video to take that base name.
    /// A deleted film's artwork turning up on a different one.</para>
    /// </summary>
    [Fact]
    public void Thumbnails_in_every_format_go_not_just_the_displayed_one()
    {
        GiveTheLibraryAVideo("Dune.mp4");
        File.WriteAllText(Path.Combine(_root, "Dune.png"), "a second thumbnail");
        File.WriteAllText(Path.Combine(_root, "Dune.webp"), "a third");

        Delete("Dune.mp4");

        Exists("Dune.jpg").Should().BeFalse();
        Exists("Dune.png").Should().BeFalse("an orphan here becomes the next video's thumbnail");
        Exists("Dune.webp").Should().BeFalse();
    }

    /// <summary>One thumbnail that will not delete must not strand the others — the
    /// catch is per file for that reason.</summary>
    [Fact]
    public void A_video_with_no_thumbnail_at_all_still_deletes_cleanly()
    {
        File.WriteAllText(Path.Combine(_root, "Bare.mp4"), "no artwork");

        Delete("Bare.mp4").Should().BeTrue();

        Exists("Bare.mp4").Should().BeFalse();
    }

    // ─── refusing to reach outside the library ────────────────────────────

    /// <summary>
    /// The helper reduces whatever it is given to a bare name before touching disk.
    /// The endpoint rejects paths outright, but this layer holds the file handle and
    /// defends itself, the same way the book deletion does.
    /// </summary>
    [Theory]
    [InlineData("../outside.mp4")]
    [InlineData("/etc/outside.mp4")]
    [InlineData("sub/dir/outside.mp4")]
    public void A_path_is_reduced_to_a_bare_name_and_never_escapes_the_library(string attempt)
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "outside.mp4");
        File.WriteAllText(outside, "not the library's to delete");

        try
        {
            Delete(attempt).Should().BeFalse("no such video inside the library");
            File.Exists(outside).Should().BeTrue("nothing outside the root may be touched");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    // ─── reporting what it found ──────────────────────────────────────────

    /// <summary>Nothing to delete is reported rather than invented — the endpoint
    /// turns this into a 404.</summary>
    [Fact]
    public void Deleting_a_video_that_is_not_there_reports_that_it_was_not_found()
    {
        Delete("Never Existed.mp4").Should().BeFalse();
    }

    /// <summary>A lone leftover still counts as found, so it can be cleaned up — an
    /// orphaned sidecar would otherwise keep a ghost entry on the shelf with no way
    /// to remove it.</summary>
    [Theory]
    [InlineData("Ghost.mp4")]
    [InlineData("Ghost.mp4.meta.json")]
    [InlineData("Ghost.jpg")]
    public void Any_single_surviving_trace_is_enough_to_count_as_found(string leftover)
    {
        File.WriteAllText(Path.Combine(_root, leftover), "leftover");

        Delete("Ghost.mp4").Should().BeTrue();

        Exists(leftover).Should().BeFalse();
    }

    /// <summary>
    /// A video that cannot be deleted raises rather than reporting success. The
    /// endpoint catches it and answers 500; swallowing it would tell the caller the
    /// video was destroyed while it is still sitting there.
    /// </summary>
    /// <remarks>
    /// <b>This is not the thumbnail catch, and that gap is deliberate.</b> The only
    /// portable way to make a file undeletable is to drop write permission on its
    /// directory — and the video shares that directory, so the video fails first and
    /// the thumbnail is never reached. Making just the thumbnail immutable needs
    /// <c>chflags</c> (BSD/macOS) or <c>chattr</c> as root (Linux), neither of which
    /// runs everywhere this suite does. So the thumbnail's try/catch is recorded as
    /// correct-by-reading, not as covered, rather than propped up by a test that
    /// exercises a different line and claims otherwise.
    /// </remarks>
    [Fact]
    public void A_video_that_cannot_be_deleted_raises_rather_than_reporting_success()
    {
        var stuck = Path.Combine(_root, "stuck");
        Directory.CreateDirectory(stuck);
        Environment.SetEnvironmentVariable("YOUTUBE_DOWNLOAD_ROOT", stuck);

        File.WriteAllText(Path.Combine(stuck, "Dune.mp4"), "the video");
        File.WriteAllText(Path.Combine(stuck, "Dune.jpg"), "thumbnail");

        File.SetUnixFileMode(stuck, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var precondition = () => File.Delete(Path.Combine(stuck, "Dune.mp4"));
            precondition.Should().Throw<UnauthorizedAccessException>(
                "this test needs a video that genuinely will not delete; as root it cannot have one");

            var delete = () => Delete("Dune.mp4");

            delete.Should().Throw<UnauthorizedAccessException>(
                "a video that will not delete is a real failure and must reach the endpoint's "
                + "catch, rather than being reported as a successful destruction");
        }
        finally
        {
            File.SetUnixFileMode(stuck,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("YOUTUBE_DOWNLOAD_ROOT", _root);
        }
    }
}
