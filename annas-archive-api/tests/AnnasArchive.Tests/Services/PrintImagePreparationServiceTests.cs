using AnnasArchive.API.Infrastructure;
using AnnasArchive.API.Services.PhotoPrint;
using FluentAssertions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// Round-trips real image bytes through the print preparation service. The
/// geometry itself is covered by <see cref="PrintLayoutTests"/>; what needs real
/// pixels is EXIF orientation handling, which is where phone photos silently go
/// wrong, and the output metadata a print lab reads.
/// </summary>
public sealed class PrintImagePreparationServiceTests : IDisposable
{
    private readonly string _outputDirectory = Path.Combine(
        Path.GetTempPath(), $"print-prep-{Guid.NewGuid():N}");

    private static PrintImagePreparationService CreateService(int jpegQuality = 95) =>
        new(Options.Create(new PhotoPrintConfiguration
        {
            Dpi = 300,
            JpegQuality = jpegQuality,
            OutputRoot = Path.GetTempPath()
        }));

    /// <summary>A solid-colour image, optionally carrying an EXIF orientation flag.</summary>
    private static MemoryStream CreateJpeg(int width, int height, ushort? exifOrientation = null)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(120, 160, 200));

        if (exifOrientation is not null)
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, exifOrientation.Value);
            image.Metadata.ExifProfile = exif;
        }

        var stream = new MemoryStream();
        image.SaveAsJpeg(stream);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task LandscapePhoto_RendersAtSixByFourInchesTimesDpi()
    {
        using var source = CreateJpeg(4000, 3000);

        var result = await CreateService().PrepareAsync(
            source, "IMG_1234.jpg", PrintSize.FromCode("4x6"), _outputDirectory);

        File.Exists(result.OutputPath).Should().BeTrue();

        using var output = await Image.LoadAsync(result.OutputPath);
        output.Width.Should().Be(1800);
        output.Height.Should().Be(1200);
    }

    [Fact]
    public async Task PortraitPhoto_RendersAtFourBySixInchesTimesDpi()
    {
        using var source = CreateJpeg(3000, 4000);

        var result = await CreateService().PrepareAsync(
            source, "IMG_1234.jpg", PrintSize.FromCode("4x6"), _outputDirectory);

        using var output = await Image.LoadAsync(result.OutputPath);
        output.Width.Should().Be(1200);
        output.Height.Should().Be(1800);
    }

    [Fact]
    public async Task ExifRotatedPhoto_IsTreatedAsPortrait_NotAsItsStoredLandscapeShape()
    {
        // How virtually every phone stores a portrait shot: the pixels are landscape
        // and an EXIF flag says "rotate 90°". Orientation 6 = rotate 90° clockwise.
        // Measured before applying that flag, this looks like a 4000×3000 landscape
        // and would be cropped to a landscape print — cutting the subject's head off.
        using var source = CreateJpeg(4000, 3000, exifOrientation: 6);

        var result = await CreateService().PrepareAsync(
            source, "IMG_ROTATED.jpg", PrintSize.FromCode("4x6"), _outputDirectory);

        result.Plan.IsLandscape.Should().BeFalse(
            "the EXIF flag makes this a portrait photo, whatever the stored pixel order says");

        using var output = await Image.LoadAsync(result.OutputPath);
        output.Width.Should().Be(1200);
        output.Height.Should().Be(1800);
    }

    [Fact]
    public async Task OutputCarriesPrintResolutionMetadata()
    {
        using var source = CreateJpeg(4000, 3000);

        var result = await CreateService().PrepareAsync(
            source, "IMG_1234.jpg", PrintSize.FromCode("5x7"), _outputDirectory);

        using var output = await Image.LoadAsync(result.OutputPath);
        output.Metadata.ResolutionUnits.Should().Be(PixelResolutionUnit.PixelsPerInch);
        output.Metadata.HorizontalResolution.Should().Be(300);
        output.Metadata.VerticalResolution.Should().Be(300);
    }

    [Fact]
    public async Task SquareSize_ProducesASquareFile()
    {
        using var source = CreateJpeg(4000, 3000);

        var result = await CreateService().PrepareAsync(
            source, "IMG_1234.jpg", PrintSize.FromCode("4x4"), _outputDirectory);

        using var output = await Image.LoadAsync(result.OutputPath);
        output.Width.Should().Be(1200);
        output.Height.Should().Be(1200);
    }

    [Fact]
    public async Task LowResolutionSource_StillRenders_ButIsFlagged()
    {
        using var source = CreateJpeg(640, 480);

        var result = await CreateService().PrepareAsync(
            source, "old-photo.jpg", PrintSize.FromCode("11x14"), _outputDirectory);

        result.IsBelowQualityFloor.Should().BeTrue();
        File.Exists(result.OutputPath).Should().BeTrue("a soft print is still a print — this warns, it does not refuse");
    }

    [Fact]
    public async Task FileName_IdentifiesSizeAndOrientation_AndSanitisesTheSourceName()
    {
        using var source = CreateJpeg(4000, 3000);

        var result = await CreateService().PrepareAsync(
            source, "beach/trip: 2026.jpg", PrintSize.FromCode("8x10"), _outputDirectory);

        result.FileName.Should().EndWith(".jpg");
        result.FileName.Should().Contain("8x10");
        result.FileName.Should().Contain("land");
        // Deterministic across platforms — not dependent on Path.GetInvalidFileNameChars,
        // which lets ':' through on Unix but not on Windows.
        result.FileName.Should().NotContainAny("/", ":", " ");
        Path.GetFileName(result.OutputPath).Should().Be(result.FileName);
    }

    [Theory]
    [InlineData("...", "photo")]
    [InlineData("   ", "photo")]
    [InlineData("héllo wörld", "héllo_wörld")]  // accented letters are letters — kept
    public async Task DegenerateSourceNames_FallBackToAUsableStem(string sourceName, string expectedStem)
    {
        using var source = CreateJpeg(2000, 2000);

        var result = await CreateService().PrepareAsync(
            source, sourceName + ".jpg", PrintSize.FromCode("4x4"), _outputDirectory);

        result.FileName.Should().StartWith(expectedStem + "__");
    }

    [Fact]
    public async Task OutputDirectoryIsCreatedIfMissing()
    {
        var nested = Path.Combine(_outputDirectory, "run-1", "prints");
        using var source = CreateJpeg(2000, 2000);

        var result = await CreateService().PrepareAsync(
            source, "a.jpg", PrintSize.FromCode("4x4"), nested);

        Directory.Exists(nested).Should().BeTrue();
        result.FileSizeBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task EveryCatalogSize_RendersFromATypicalPhonePhoto()
    {
        foreach (var size in PrintSize.Catalog)
        {
            using var source = CreateJpeg(4032, 3024);

            var result = await CreateService().PrepareAsync(
                source, $"phone-{size.Code}.jpg", size, _outputDirectory);

            using var output = await Image.LoadAsync(result.OutputPath);
            output.Width.Should().Be(result.Plan.TargetWidthPx, $"{size.Code} width");
            output.Height.Should().Be(result.Plan.TargetHeightPx, $"{size.Code} height");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
            Directory.Delete(_outputDirectory, recursive: true);
    }
}
