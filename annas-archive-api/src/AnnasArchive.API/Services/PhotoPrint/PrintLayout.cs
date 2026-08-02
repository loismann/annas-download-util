namespace AnnasArchive.API.Services.PhotoPrint;

/// <summary>Region of the source image kept by the crop, in source pixels.</summary>
public readonly record struct CropBox(int X, int Y, int Width, int Height);

/// <summary>
/// Everything needed to turn one source photo into one print-ready file, with no
/// decisions left to make. Deliberately free of any imaging-library type so the
/// arithmetic below can be unit tested without decoding an image.
/// </summary>
/// <param name="EffectiveDpi">
/// Real resolution delivered to the paper: how many source pixels survive the crop
/// per inch of print. Distinct from the render DPI — upscaling a small photo to
/// 300dpi does not add detail, and this is the number that reveals that.
/// </param>
public sealed record PrintPlan(
    PrintSize Size,
    int TargetWidthPx,
    int TargetHeightPx,
    CropBox Crop,
    double EffectiveDpi,
    bool IsLandscape)
{
    /// <summary>
    /// Below this, prints visibly soften. Not a hard failure — a treasured photo off
    /// an old phone is still worth printing — so this surfaces as a UI warning
    /// rather than a rejection.
    /// </summary>
    public const double QualityFloorDpi = 150.0;

    public bool IsBelowQualityFloor => EffectiveDpi < QualityFloorDpi;

    /// <summary>Fraction of the source image discarded by the crop, 0.0–1.0.</summary>
    public double CroppedAwayFraction { get; init; }
}

/// <summary>
/// Pure geometry for fitting a photo to a print size. No I/O, no image decoding —
/// takes source dimensions in, returns a plan.
/// </summary>
public static class PrintLayout
{
    /// <summary>Standard photographic print resolution.</summary>
    public const int DefaultDpi = 300;

    /// <summary>
    /// Builds the crop-and-resize plan for one photo at one print size.
    ///
    /// Orientation is matched to the source first: a landscape photo on a "4x6"
    /// targets 6x4, not 4x6. Skipping this is the single most destructive bug
    /// available here — it would centre-crop a landscape group shot down to a
    /// portrait sliver and silently throw away most of the frame.
    /// </summary>
    public static PrintPlan ComputePlan(
        int sourceWidthPx, int sourceHeightPx, PrintSize size, int dpi = DefaultDpi)
    {
        ArgumentNullException.ThrowIfNull(size);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidthPx);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeightPx);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);

        // A square source is treated as landscape; the choice is arbitrary and
        // has no effect, since both print edges are then equal anyway.
        var isLandscape = sourceWidthPx >= sourceHeightPx;

        var (printWidthIn, printHeightIn) = size.IsSquare
            ? (size.ShortInches, size.ShortInches)
            : isLandscape
                ? (size.LongInches, size.ShortInches)
                : (size.ShortInches, size.LongInches);

        var targetAspect = printWidthIn / printHeightIn;
        var sourceAspect = (double)sourceWidthPx / sourceHeightPx;

        // Centre-crop to the print's aspect ratio: trim the longer dimension,
        // keep the other whole. Never scale the two axes independently — that
        // stretches faces, which is worse than losing edge content.
        int cropWidth, cropHeight;
        if (sourceAspect > targetAspect)
        {
            cropHeight = sourceHeightPx;
            cropWidth = (int)Math.Round(sourceHeightPx * targetAspect);
        }
        else
        {
            cropWidth = sourceWidthPx;
            cropHeight = (int)Math.Round(sourceWidthPx / targetAspect);
        }

        // Rounding above can overshoot by a pixel on extreme aspect ratios.
        cropWidth = Math.Clamp(cropWidth, 1, sourceWidthPx);
        cropHeight = Math.Clamp(cropHeight, 1, sourceHeightPx);

        var crop = new CropBox(
            X: (sourceWidthPx - cropWidth) / 2,
            Y: (sourceHeightPx - cropHeight) / 2,
            Width: cropWidth,
            Height: cropHeight);

        var sourceArea = (double)sourceWidthPx * sourceHeightPx;
        var croppedAway = 1.0 - (cropWidth * (double)cropHeight / sourceArea);

        return new PrintPlan(
            Size: size,
            TargetWidthPx: (int)Math.Round(printWidthIn * dpi),
            TargetHeightPx: (int)Math.Round(printHeightIn * dpi),
            Crop: crop,
            EffectiveDpi: cropWidth / printWidthIn,
            IsLandscape: isLandscape)
        {
            CroppedAwayFraction = Math.Clamp(croppedAway, 0.0, 1.0)
        };
    }
}
