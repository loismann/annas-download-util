using AnnasArchive.API.Data;
using AnnasArchive.API.Models;
using AnnasArchive.API.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Services;

public sealed class AudiobookRequestSafetyTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"audiobook-request-{Guid.NewGuid():N}.db");

    [Fact]
    public void PreviewAndReleaseTokens_AreOwnerScopedAndSingleUse()
    {
        var tokens = new AudiobookRequestTokenStore(TimeProvider.System);
        var preview = tokens.CreatePreview("owner-a", "B012345678", "us", autoSearch: false);

        tokens.ConsumePreview("owner-b", preview.Token).Should().BeNull();
        // A failed owner check consumes the capability, so it cannot be probed
        // and then replayed by the original owner.
        tokens.ConsumePreview("owner-a", preview.Token).Should().BeNull();

        var release = tokens.CreateRelease("owner-a", 42, "B012345678", "upstream-reference");
        tokens.ConsumeRelease("owner-a", 99, release.Token).Should().BeNull();
        tokens.ConsumeRelease("owner-a", 42, release.Token).Should().BeNull();

        var series = tokens.CreateSeries("owner-a", "B0SERIES01", "us", ["B012345678"]);
        tokens.ConsumeSeries("owner-b", series.Token).Should().BeNull();
        tokens.ConsumeSeries("owner-a", series.Token).Should().BeNull();
    }

    /// <summary>The browser reports the auto-search decision; it cannot make
    /// one. Whatever the server decided during preview is what the token
    /// carries into confirmation.</summary>
    [Fact]
    public void PreviewToken_CarriesTheServersAutoSearchDecision()
    {
        var tokens = new AudiobookRequestTokenStore(TimeProvider.System);

        var reviewed = tokens.CreatePreview("owner-a", "B012345678", "us", autoSearch: false);
        var automatic = tokens.CreatePreview("owner-a", "B087654321", "us", autoSearch: true);

        tokens.ConsumePreview("owner-a", reviewed.Token)!.AutoSearch.Should().BeFalse();
        tokens.ConsumePreview("owner-a", automatic.Token)!.AutoSearch.Should().BeTrue();
    }

    [Fact]
    public void Store_AddsOneListenarrRequestAndUniqueRequesterAttribution()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:Path"] = _databasePath }).Build();
        var store = new AudiobookRequestStore(new AppDatabase(configuration));
        var item = new ListenarrLibraryItem(
            42, "B012345678", "Test Book", ["Author"], ["Narrator"], [], true, null);
        var now = DateTimeOffset.UtcNow;

        store.SaveRequestAndRequester(
            item, "B012345678", [], "Test Book", "Author", "Monitored",
            "user-a", "Paul", now).Should().BeTrue();
        store.SaveRequestAndRequester(
            item, "B012345678", [], "Test Book", "Author", "Monitored",
            "user-a", "Paul", now).Should().BeFalse();
        store.SaveRequestAndRequester(
            item, "B012345678", [], "Test Book", "Author", "Monitored",
            "user-b", "Mom", now).Should().BeTrue();

        store.GetByAsin("b012345678")!.ListenarrId.Should().Be(42);
        store.GetOwnerLabels(42).Should().Equal("Paul", "Mom");
    }

    /// <summary>Measured against the live indexers on 2026-08-02: an apostrophe
    /// must become a space. "Pandora's Star" and "Pandoras Star" both return
    /// zero releases, while "Pandora Star" returns three. The leftover "s"
    /// token is harmless — "Pandora s Star" returns the same three — so this
    /// deliberately does not special-case possessives.</summary>
    [Theory]
    [InlineData("Pandora's Star", "Pandora s Star")]
    [InlineData("Carl's Doomsday Scenario", "Carl s Doomsday Scenario")]
    [InlineData("Judas Unchained", "Judas Unchained")]
    [InlineData("Peter F. Hamilton", "Peter F Hamilton")]
    [InlineData("  Spaced   Out  ", "Spaced Out")]
    [InlineData(null, "")]
    public void ReleaseQuery_ReplacesPunctuationWithSpace_RatherThanDeletingIt(
        string? input, string expected) =>
        AudiobookRequestService.NormalizeQuery(input).Should().Be(expected);

    [Theory]
    [InlineData("Peter F. Hamilton", "Peter F. Hamilton")]
    [InlineData("Neil Gaiman, Terry Pratchett", "Neil Gaiman")]
    [InlineData("", "")]
    public void ReleaseQuery_UsesOnlyTheFirstAuthor(string authors, string expected) =>
        AudiobookRequestService.FirstAuthor(authors).Should().Be(expected);

    public void Dispose()
    {
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
            if (File.Exists(path)) File.Delete(path);
    }
}
