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
        var preview = tokens.CreatePreview("owner-a", "B012345678", "us");

        tokens.ConsumePreview("owner-b", preview.Token).Should().BeNull();
        // A failed owner check consumes the capability, so it cannot be probed
        // and then replayed by the original owner.
        tokens.ConsumePreview("owner-a", preview.Token).Should().BeNull();

        var release = tokens.CreateRelease("owner-a", 42, "B012345678", "upstream-reference");
        tokens.ConsumeRelease("owner-a", 99, release.Token).Should().BeNull();
        tokens.ConsumeRelease("owner-a", 42, release.Token).Should().BeNull();
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

    public void Dispose()
    {
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
            if (File.Exists(path)) File.Delete(path);
    }
}
