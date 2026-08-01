using System.Text.Json;
using AnnasArchive.API.Services.Library;

namespace AnnasArchive.Tests.Services.Library;

public class LibraryMetadataRulesTests
{
    // ── IsMetadataReliable ────────────────────────────────────────────────

    [Theory]
    [InlineData("Dune", new[] { "Frank Herbert" }, true)]
    [InlineData("Dune", new string[0], true)]          // no authors is fine
    [InlineData("Dune", null, true)]
    [InlineData("It", new[] { "Stephen King" }, false)] // title under 3 chars
    [InlineData("", new[] { "Stephen King" }, false)]
    [InlineData("   ", new[] { "Stephen King" }, false)]
    [InlineData(null, new[] { "Stephen King" }, false)]
    public void IsMetadataReliable_NeedsARealTitle(string? title, string[]? authors, bool expected)
    {
        LibraryMetadataRules.IsMetadataReliable(title, authors).Should().Be(expected);
    }

    [Fact]
    public void IsMetadataReliable_AcceptsIfAnySingleAuthorLooksReal()
    {
        // One usable name is enough — initials and stray characters alongside it
        // are normal in scraped metadata and shouldn't disqualify the record.
        LibraryMetadataRules.IsMetadataReliable("Dune", ["J.", "Frank Herbert"]).Should().BeTrue();
    }

    [Fact]
    public void IsMetadataReliable_RejectsWhenEveryAuthorIsTooShort()
    {
        LibraryMetadataRules.IsMetadataReliable("Dune", ["J.", "", "  "]).Should().BeFalse();
    }

    // ── ShouldUseParsedTitle ──────────────────────────────────────────────

    [Fact]
    public void ShouldUseParsedTitle_IsFalseWithoutAParsedTitle()
    {
        LibraryMetadataRules.ShouldUseParsedTitle("Dune", null, "dune_1965").Should().BeFalse();
        LibraryMetadataRules.ShouldUseParsedTitle("Dune", "  ", "dune_1965").Should().BeFalse();
    }

    [Fact]
    public void ShouldUseParsedTitle_IsTrueWhenNothingIsStoredYet()
    {
        LibraryMetadataRules.ShouldUseParsedTitle(null, "Dune", "dune_1965").Should().BeTrue();
        LibraryMetadataRules.ShouldUseParsedTitle("", "Dune", "dune_1965").Should().BeTrue();
    }

    [Theory]
    [InlineData("dune_1965")]      // identical to the raw base name
    [InlineData("DUNE_1965")]      // ...case-insensitively
    [InlineData("Dune_Copy")]      // contains an underscore
    [InlineData("Dune B08XYZQ12F")] // ends in a long uppercase/digit run
    [InlineData("Dune 9780441013593")]
    public void ShouldUseParsedTitle_ReplacesTitlesThatAreObviouslyFilenameDebris(string existing)
    {
        LibraryMetadataRules.ShouldUseParsedTitle(existing, "Dune", "dune_1965").Should().BeTrue();
    }

    [Theory]
    [InlineData("Dune")]
    [InlineData("Dune: Messiah")]
    [InlineData("Dune B08XY")]   // uppercase run under 8 chars — left alone
    public void ShouldUseParsedTitle_LeavesAPlausibleStoredTitleAlone(string existing)
    {
        LibraryMetadataRules.ShouldUseParsedTitle(existing, "Something Else", "dune_1965").Should().BeFalse();
    }

    // ── IsLocalCover ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("_covers/abc.jpg", true)]
    [InlineData("_COVERS/abc.jpg", true)]
    [InlineData("https://covers.openlibrary.org/b/isbn/x-L.jpg", false)]
    [InlineData("nested/_covers/abc.jpg", false)]  // must be the prefix
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLocalCover_MatchesOnlyThePrefix(string? url, bool expected)
    {
        LibraryMetadataRules.IsLocalCover(url).Should().Be(expected);
    }

    // ── FormatFileSize ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "0B")]
    [InlineData(-1, "0B")]
    [InlineData(512, "512.0B")]
    [InlineData(1024, "1.0KB")]
    [InlineData(1536, "1.5KB")]
    [InlineData(1048576, "1.0MB")]
    [InlineData(1073741824, "1.0GB")]
    public void FormatFileSize_StepsUpAtEach1024(long bytes, string expected)
    {
        LibraryMetadataRules.FormatFileSize(bytes).Should().Be(expected);
    }

    [Theory]
    [InlineData(1023, "1023.0B")]
    [InlineData(1010, "1010.0B")]
    public void FormatFileSize_StepsUpAt1024Not1000(long bytes, string expected)
    {
        // Anything in the 1000-1023 gap is what distinguishes the two thresholds.
        // Without a case in that range, changing `>= 1024` to `>= 1000` passes
        // every other assertion here.
        LibraryMetadataRules.FormatFileSize(bytes).Should().Be(expected);
    }

    [Fact]
    public void FormatFileSize_StopsAtGigabytesRatherThanInventingATerabyteUnit()
    {
        LibraryMetadataRules.FormatFileSize(5L * 1024 * 1024 * 1024 * 1024)
            .Should().Be("5120.0GB");
    }

    // ── Loosely-typed JSON reads ──────────────────────────────────────────

    [Fact]
    public void TryGetDouble_AcceptsANumberOrANumericString()
    {
        var el = Json("""{ "a": 4.5, "b": "4.5", "c": "nope", "d": null }""");

        LibraryMetadataRules.TryGetDouble(el, "a").Should().Be(4.5);
        LibraryMetadataRules.TryGetDouble(el, "b").Should().Be(4.5);
        LibraryMetadataRules.TryGetDouble(el, "c").Should().BeNull();
        LibraryMetadataRules.TryGetDouble(el, "d").Should().BeNull();
        LibraryMetadataRules.TryGetDouble(el, "missing").Should().BeNull();
    }

    [Fact]
    public void TryGetInt_AcceptsANumberOrANumericString()
    {
        var el = Json("""{ "a": 7, "b": "7", "c": "7.5", "d": "nope" }""");

        LibraryMetadataRules.TryGetInt(el, "a").Should().Be(7);
        LibraryMetadataRules.TryGetInt(el, "b").Should().Be(7);
        LibraryMetadataRules.TryGetInt(el, "c").Should().BeNull();  // not an integer
        LibraryMetadataRules.TryGetInt(el, "d").Should().BeNull();
    }

    // ── Meta dictionary helpers ───────────────────────────────────────────

    [Fact]
    public void TryGetMetaArray_ReturnsNullRatherThanThrowingOnTheWrongType()
    {
        var meta = new Dictionary<string, object?> { ["authors"] = "not an array" };

        LibraryMetadataRules.TryGetMetaArray(meta, "authors").Should().BeNull();
        LibraryMetadataRules.TryGetMetaValue(meta, "missing").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]      // key present, value null
    [InlineData("")]        // present but blank
    [InlineData("   ")]
    public void SetIfMissing_TreatsAPresentButEmptyValueAsMissing(object? existing)
    {
        var meta = new Dictionary<string, object?> { ["title"] = existing };

        LibraryMetadataRules.SetIfMissing(meta, "title", "Dune");

        meta["title"].Should().Be("Dune");
    }

    [Fact]
    public void SetIfMissing_TreatsAnEmptyArrayAsMissing()
    {
        // This is what enrichment actually produces when a lookup comes back
        // empty, so it has to count as "nothing useful is there yet".
        var meta = new Dictionary<string, object?> { ["authors"] = Array.Empty<string>() };

        LibraryMetadataRules.SetIfMissing(meta, "authors", new[] { "Frank Herbert" });

        meta["authors"].Should().BeEquivalentTo(new[] { "Frank Herbert" });
    }

    [Fact]
    public void SetIfMissing_DoesNotOverwriteSomethingReal()
    {
        var meta = new Dictionary<string, object?>
        {
            ["title"] = "Dune",
            ["authors"] = new[] { "Frank Herbert" }
        };

        LibraryMetadataRules.SetIfMissing(meta, "title", "Overwritten");
        LibraryMetadataRules.SetIfMissing(meta, "authors", new[] { "Overwritten" });

        meta["title"].Should().Be("Dune");
        meta["authors"].Should().BeEquivalentTo(new[] { "Frank Herbert" });
    }

    [Fact]
    public void SetIfMissing_AddsAKeyThatIsNotThereAtAll()
    {
        var meta = new Dictionary<string, object?>();

        LibraryMetadataRules.SetIfMissing(meta, "series", "Dune");

        meta["series"].Should().Be("Dune");
    }

    // ── ExtractResponseText ───────────────────────────────────────────────

    [Fact]
    public void ExtractResponseText_DigsTheAssistantTextOutOfTheResponsesShape()
    {
        var el = Json("""
            { "output": [ { "content": [ { "text": "hello" } ] } ] }
            """);

        LibraryMetadataRules.ExtractResponseText(el).Should().Be("hello");
    }

    [Fact]
    public void ExtractResponseText_SkipsOutputItemsWithNoContentArray()
    {
        var el = Json("""
            {
              "output": [
                { "type": "reasoning" },
                { "content": "not an array" },
                { "content": [ { "annotations": [] }, { "text": "hello" } ] }
              ]
            }
            """);

        LibraryMetadataRules.ExtractResponseText(el).Should().Be("hello");
    }

    [Theory]
    [InlineData("""{ }""")]
    [InlineData("""{ "output": "not an array" }""")]
    [InlineData("""{ "output": [] }""")]
    [InlineData("""{ "output": [ { "content": [] } ] }""")]
    public void ExtractResponseText_ReturnsNullForAnyOtherShape(string json)
    {
        LibraryMetadataRules.ExtractResponseText(Json(json)).Should().BeNull();
    }

    // ── Supported extensions ──────────────────────────────────────────────

    [Theory]
    [InlineData(".epub")]
    [InlineData(".EPUB")]
    [InlineData(".pdf")]
    [InlineData(".djvu")]
    public void SupportedExtensions_IsCaseInsensitive(string ext)
    {
        LibraryMetadataRules.SupportedExtensions.Should().Contain(ext);
    }

    [Theory]
    [InlineData(".mp3")]
    [InlineData(".jpg")]
    [InlineData("epub")]   // no dot
    public void SupportedExtensions_ExcludesEverythingElse(string ext)
    {
        LibraryMetadataRules.SupportedExtensions.Should().NotContain(ext);
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;
}
