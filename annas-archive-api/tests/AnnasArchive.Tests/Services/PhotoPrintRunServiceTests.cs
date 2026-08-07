using AnnasArchive.API.Data;
using AnnasArchive.API.Infrastructure;
using AnnasArchive.API.Services.PhotoPrint;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// Orchestration between "what was chosen" (the store) and "how one photo becomes
/// one print" (the preparation service). The behaviours that matter here are the
/// ones that cost money or cost the user their selection: the per-run ceiling, and
/// never discarding good renders because one photo failed.
/// </summary>
public sealed class PhotoPrintRunServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"print-run-{Guid.NewGuid():N}.db");
    private readonly string _outputRoot = Path.Combine(
        Path.GetTempPath(), $"print-out-{Guid.NewGuid():N}");

    private readonly PhotoPrintOrderStore _store;
    private readonly Mock<IImmichService> _immich = new();
    private readonly Mock<IPrintImagePreparationService> _preparation = new();

    private const string Paul = "paul";

    public PhotoPrintRunServiceTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = _databasePath })
            .Build();
        _store = new PhotoPrintOrderStore(new AppDatabase(config));

        _immich.Setup(i => i.OpenOriginalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3]));
    }

    private PhotoPrintRunService CreateService(int maxPrints = 200) =>
        new(_store, _immich.Object, _preparation.Object, Options.Create(new PhotoPrintConfiguration
        {
            OutputRoot = _outputRoot,
            MaxPrintsPerRun = maxPrints,
            PickupZipCode = "96813"
        }));

    /// <summary>Makes the preparation service write a real file, so resume logic has something to find.</summary>
    private void PrepareSucceeds(double dpi = 300, bool belowFloor = false)
    {
        _preparation
            .Setup(p => p.PrepareAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<PrintSize>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream _, string name, PrintSize size, string dir, CancellationToken _) =>
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)}_{size.Code}.jpg");
                File.WriteAllBytes(path, [0xFF, 0xD8]);
                var plan = PrintLayout.ComputePlan(4000, 3000, size);
                return new PreparedPrintImage(path, Path.GetFileName(path),
                    plan with { }, 2);
            });
    }

    // ─── Adding items ────────────────────────────────────────────────────

    [Fact]
    public void AddItem_RejectsAnUnknownSize()
    {
        var service = CreateService();
        var runId = service.CreateRun(Paul);

        var act = () => service.AddItem(Paul, runId, "asset", "a.jpg", "11x17", 1);

        act.Should().Throw<PhotoPrintValidationException>().WithMessage("*11x17*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(100)]
    public void AddItem_RejectsImplausibleQuantities(int quantity)
    {
        var service = CreateService();
        var runId = service.CreateRun(Paul);

        var act = () => service.AddItem(Paul, runId, "asset", "a.jpg", "4x6", quantity);

        act.Should().Throw<PhotoPrintValidationException>();
    }

    [Fact]
    public void PerRunCeiling_CountsPrints_NotRows()
    {
        // Six rows of one print each is fine; two rows of four is not, under a
        // ceiling of six. Quantity is what costs money.
        var service = CreateService(maxPrints: 6);
        var runId = service.CreateRun(Paul);

        service.AddItem(Paul, runId, "a", "a.jpg", "4x6", 4);

        var act = () => service.AddItem(Paul, runId, "b", "b.jpg", "4x6", 4);
        act.Should().Throw<PhotoPrintValidationException>().WithMessage("*8*6*");

        service.AddItem(Paul, runId, "b", "b.jpg", "4x6", 2);
        _store.TotalPrintCount(Paul, runId).Should().Be(6, "exactly at the ceiling is allowed");
    }

    [Fact]
    public void CeilingIsEnforcedServerSide_NotJustInTheBrowser()
    {
        var service = CreateService(maxPrints: 1);
        var runId = service.CreateRun(Paul);
        service.AddItem(Paul, runId, "a", "a.jpg", "4x6", 1);

        var act = () => service.AddItem(Paul, runId, "b", "b.jpg", "4x6", 1);

        act.Should().Throw<PhotoPrintValidationException>();
    }

    // ─── Preparation ─────────────────────────────────────────────────────

    [Fact]
    public async Task Prepare_RendersEveryItem_AndMarksTheRunReady()
    {
        PrepareSucceeds();
        var service = CreateService();
        var runId = service.CreateRun(Paul);
        service.AddItem(Paul, runId, "a", "a.jpg", "4x6", 2);
        service.AddItem(Paul, runId, "b", "b.jpg", "5x7", 1);

        var outcome = await service.PrepareAsync(Paul, runId);

        outcome.Prepared.Should().Be(2);
        outcome.Failed.Should().Be(0);
        _store.GetRun(Paul, runId)!.Status.Should().Be(PrintRunStatus.Ready);
        _store.ListItems(Paul, runId).Should().OnlyContain(i => i.Status == PrintItemStatus.Prepared);
    }

    [Fact]
    public async Task Prepare_WritesIntoARunScopedDirectory()
    {
        PrepareSucceeds();
        var service = CreateService();
        var runId = service.CreateRun(Paul);
        service.AddItem(Paul, runId, "a", "a.jpg", "4x6", 1);

        await service.PrepareAsync(Paul, runId);

        var run = _store.GetRun(Paul, runId)!;
        run.OutputDirectory.Should().Be(Path.Combine(_outputRoot, runId),
            "two runs must not overwrite each other's renders");
    }

    [Fact]
    public async Task OneBadPhoto_DoesNotDiscardTheOthers()
    {
        // The resumability guarantee. Losing a whole curated selection because
        // one source was unreadable would be a miserable way to lose ten minutes.
        var calls = 0;
        _preparation
            .Setup(p => p.PrepareAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<PrintSize>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream _, string name, PrintSize size, string dir, CancellationToken _) =>
            {
                if (++calls == 1) throw new InvalidOperationException("corrupt source");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"{name}.jpg");
                File.WriteAllBytes(path, [0xFF]);
                return new PreparedPrintImage(path, name, PrintLayout.ComputePlan(4000, 3000, size), 1);
            });

        var service = CreateService();
        var runId = service.CreateRun(Paul);
        service.AddItem(Paul, runId, "bad", "bad.jpg", "4x6", 1);
        service.AddItem(Paul, runId, "good", "good.jpg", "4x6", 1);

        var outcome = await service.PrepareAsync(Paul, runId);

        outcome.Prepared.Should().Be(1);
        outcome.Failed.Should().Be(1);

        var items = _store.ListItems(Paul, runId);
        items[0].Status.Should().Be(PrintItemStatus.Failed);
        items[1].Status.Should().Be(PrintItemStatus.Prepared, "the good render survives");
    }

    [Fact]
    public async Task APartialRunStaysPreparing_SoItIsNeverMistakenForComplete()
    {
        _preparation
            .Setup(p => p.PrepareAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<PrintSize>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("nope"));

        var service = CreateService();
        var runId = service.CreateRun(Paul);
        service.AddItem(Paul, runId, "a", "a.jpg", "4x6", 1);

        await service.PrepareAsync(Paul, runId);

        var run = _store.GetRun(Paul, runId)!;
        run.Status.Should().Be(PrintRunStatus.Preparing,
            "marking it Ready would silently under-order at the counter");
        run.LastError.Should().Contain("1");
    }

    [Fact]
    public async Task RetryingARun_SkipsWorkAlreadyDone()
    {
        PrepareSucceeds();
        var service = CreateService();
        var runId = service.CreateRun(Paul);
        service.AddItem(Paul, runId, "a", "a.jpg", "4x6", 1);

        await service.PrepareAsync(Paul, runId);
        _preparation.Invocations.Clear();

        var second = await service.PrepareAsync(Paul, runId);

        second.Prepared.Should().Be(1);
        _preparation.Verify(p => p.PrepareAsync(
            It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<PrintSize>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "an already-rendered item with its file on disk is not re-fetched or re-rendered");
    }

    [Fact]
    public async Task AVanishedRenderIsRebuilt_NotAssumedPresent()
    {
        PrepareSucceeds();
        var service = CreateService();
        var runId = service.CreateRun(Paul);
        service.AddItem(Paul, runId, "a", "a.jpg", "4x6", 1);
        await service.PrepareAsync(Paul, runId);

        // Someone cleaned out the print-ready folder between attempts.
        File.Delete(_store.ListItems(Paul, runId)[0].PreparedPath!);
        _preparation.Invocations.Clear();

        await service.PrepareAsync(Paul, runId);

        _preparation.Verify(p => p.PrepareAsync(
            It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<PrintSize>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once,
            "the database says prepared, but the file is gone — trust the disk");
    }

    [Fact]
    public async Task QualityFloorIsCountedAndPersisted()
    {
        PrepareSucceeds();
        _preparation
            .Setup(p => p.PrepareAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<PrintSize>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream _, string name, PrintSize size, string dir, CancellationToken _) =>
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"{name}.jpg");
                File.WriteAllBytes(path, [0xFF]);
                // 640px across a 16x20 — genuinely below the floor.
                return new PreparedPrintImage(path, name, PrintLayout.ComputePlan(640, 480, size), 1);
            });

        var service = CreateService();
        var runId = service.CreateRun(Paul);
        service.AddItem(Paul, runId, "a", "a.jpg", "16x20", 1);

        var outcome = await service.PrepareAsync(Paul, runId);

        outcome.BelowQualityFloor.Should().Be(1);
        _store.ListItems(Paul, runId)[0].BelowQualityFloor.Should().BeTrue();
    }

    [Fact]
    public async Task MissingPhotoGivesAPlainExplanation_NotAStackTrace()
    {
        _immich.Setup(i => i.OpenOriginalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ImmichAssetNotFoundException("gone"));

        var service = CreateService();
        var runId = service.CreateRun(Paul);
        service.AddItem(Paul, runId, "gone", "gone.jpg", "4x6", 1);

        await service.PrepareAsync(Paul, runId);

        _store.ListItems(Paul, runId)[0].LastError
            .Should().Be("The photo is no longer in Immich.");
    }

    [Fact]
    public async Task PreparingAnEmptyRunIsRejected()
    {
        var service = CreateService();
        var runId = service.CreateRun(Paul);

        var act = async () => await service.PrepareAsync(Paul, runId);

        await act.Should().ThrowAsync<PhotoPrintValidationException>().WithMessage("*at least one*");
    }

    [Fact]
    public async Task AnotherUsersRunIsNotReachable()
    {
        PrepareSucceeds();
        var service = CreateService();
        var runId = service.CreateRun(Paul);
        service.AddItem(Paul, runId, "a", "a.jpg", "4x6", 1);

        var act = async () => await service.PrepareAsync("someone-else", runId);

        await act.Should().ThrowAsync<PhotoPrintValidationException>();
    }

    [Fact]
    public void NewRunCarriesTheConfiguredPickupZip() =>
        _store.GetRun(Paul, CreateService().CreateRun(Paul))!.PickupZip.Should().Be("96813");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path)) File.Delete(path);
        }
        if (Directory.Exists(_outputRoot)) Directory.Delete(_outputRoot, recursive: true);
    }
}
