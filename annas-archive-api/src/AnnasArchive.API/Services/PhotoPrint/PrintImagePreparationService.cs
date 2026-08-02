using AnnasArchive.API.Infrastructure;
using Microsoft.Extensions.Options;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.Processing;

namespace AnnasArchive.API.Services.PhotoPrint;

/// <summary>One print-ready file on disk, plus what the UI needs to say about it.</summary>
public sealed record PreparedPrintImage(
    string OutputPath,
    string FileName,
    PrintPlan Plan,
    long FileSizeBytes)
{
    public double EffectiveDpi => Plan.EffectiveDpi;
    public bool IsBelowQualityFloor => Plan.IsBelowQualityFloor;
}

public interface IPrintImagePreparationService
{
    /// <summary>
    /// Crops and resizes one source photo to one print size, writing the result
    /// into <paramref name="outputDirectory"/>.
    /// </summary>
    Task<PreparedPrintImage> PrepareAsync(
        Stream sourceImage,
        string sourceFileName,
        PrintSize size,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes a <see cref="PrintPlan"/> against real image bytes. All the geometry
/// decisions live in <see cref="PrintLayout"/>; this type only performs them, so
/// the rules stay testable without decoding images.
/// </summary>
public sealed class PrintImagePreparationService : IPrintImagePreparationService
{
    private readonly PhotoPrintConfiguration _config;

    public PrintImagePreparationService(IOptions<PhotoPrintConfiguration> config)
    {
        _config = config.Value;
    }

    public async Task<PreparedPrintImage> PrepareAsync(
        Stream sourceImage,
        string sourceFileName,
        PrintSize size,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceImage);
        ArgumentNullException.ThrowIfNull(size);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        using var image = await Image.LoadAsync(sourceImage, cancellationToken);

        // Must happen before anything reads Width/Height. Phone cameras record
        // orientation as an EXIF flag rather than rotating the pixels, so a
        // portrait photo commonly decodes as landscape with "rotate 90" attached.
        // Measuring first would pick the wrong print orientation and then crop the
        // subject out of the frame — silently, and only visible on the paper.
        image.Mutate(context => context.AutoOrient());

        var plan = PrintLayout.ComputePlan(image.Width, image.Height, size, _config.Dpi);

        image.Mutate(context => context
            .Crop(new Rectangle(plan.Crop.X, plan.Crop.Y, plan.Crop.Width, plan.Crop.Height))
            .Resize(new ResizeOptions
            {
                Size = new Size(plan.TargetWidthPx, plan.TargetHeightPx),
                // The crop above already matched the aspect ratio exactly, so this
                // is a pure rescale. Stretch mode avoids re-deriving a fit box and
                // re-introducing a rounding-sized letterbox.
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Lanczos3
            }));

        // Print labs read this to size the image on paper; without it some
        // pipelines assume 72dpi and scale the print wrongly.
        image.Metadata.ResolutionUnits = PixelResolutionUnit.PixelsPerInch;
        image.Metadata.HorizontalResolution = _config.Dpi;
        image.Metadata.VerticalResolution = _config.Dpi;

        Directory.CreateDirectory(outputDirectory);
        var fileName = BuildFileName(sourceFileName, size, plan);
        var outputPath = Path.Combine(outputDirectory, fileName);

        var encoder = new JpegEncoder
        {
            Quality = _config.JpegQuality,
            // 4:4:4. The default 4:2:0 discards colour resolution that survives
            // screen viewing but shows up as fringing on a physical print.
            ColorType = JpegEncodingColor.YCbCrRatio444
        };
        await image.SaveAsJpegAsync(outputPath, encoder, cancellationToken);

        var fileSize = new FileInfo(outputPath).Length;

        if (plan.IsBelowQualityFloor)
        {
            Log.Warning(
                "[PhotoPrint] {File} at {Size} resolves to {Dpi:F0} dpi, below the {Floor:F0} dpi floor — print will look soft",
                sourceFileName, size.Code, plan.EffectiveDpi, PrintPlan.QualityFloorDpi);
        }

        return new PreparedPrintImage(outputPath, fileName, plan, fileSize);
    }

    /// <summary>
    /// Names outputs so a human scanning the print-ready folder can tell what each
    /// file is without opening it: source name, size, and orientation.
    ///
    /// Sanitisation uses a fixed allowlist rather than <see cref="Path.GetInvalidFileNameChars"/>,
    /// which is platform-dependent — it omits ':' and '*' on Unix, so a name built
    /// on the Linux NAS would differ from one built on a dev Mac or Windows box,
    /// and these names are also submitted to CVS's uploader.
    /// </summary>
    private static string BuildFileName(string sourceFileName, PrintSize size, PrintPlan plan)
    {
        var stem = Path.GetFileNameWithoutExtension(sourceFileName);

        var safe = new string([.. stem.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')]);

        safe = safe.Trim('_', '.', ' ');
        if (string.IsNullOrEmpty(safe))
            safe = "photo";

        var orientation = size.IsSquare ? "sq" : plan.IsLandscape ? "land" : "port";
        return $"{safe}__{size.Code}_{orientation}.jpg";
    }
}
