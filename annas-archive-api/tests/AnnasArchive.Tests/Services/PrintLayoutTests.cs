using AnnasArchive.API.Services.PhotoPrint;
using FluentAssertions;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// The crop-and-fit rules from DOCS/features/google-photos-cvs-print-automation-spec.md §6.
/// The load-bearing one is orientation matching: a print sheet has no inherent
/// orientation, so a landscape photo must target 6x4 rather than being centre-cropped
/// down to a portrait sliver. Everything else here guards against silently shipping a
/// stretched or soft print to the store, which is only discovered at the counter.
/// </summary>
public sealed class PrintLayoutTests
{
    private static PrintSize Size(string code) => PrintSize.FromCode(code);

    // ─── Orientation matching ────────────────────────────────────────────

    [Fact]
    public void LandscapeSource_OnFourBySix_TargetsSixWideNotFourWide()
    {
        var plan = PrintLayout.ComputePlan(4000, 3000, Size("4x6"));

        plan.IsLandscape.Should().BeTrue();
        plan.TargetWidthPx.Should().Be(1800, "6in × 300dpi — the long edge is horizontal");
        plan.TargetHeightPx.Should().Be(1200);
    }

    [Fact]
    public void PortraitSource_OnFourBySix_TargetsFourWide()
    {
        var plan = PrintLayout.ComputePlan(3000, 4000, Size("4x6"));

        plan.IsLandscape.Should().BeFalse();
        plan.TargetWidthPx.Should().Be(1200);
        plan.TargetHeightPx.Should().Be(1800);
    }

    [Fact]
    public void LandscapeSource_KeepsMostOfTheFrame_RatherThanCroppingToPortrait()
    {
        // The regression this whole feature turns on. A 3:2 landscape photo on a
        // 3:2 print should lose nothing at all; matching it to a portrait 4x6
        // would discard well over half the image.
        var plan = PrintLayout.ComputePlan(3000, 2000, Size("4x6"));

        plan.CroppedAwayFraction.Should().BeApproximately(0.0, 0.001);
        plan.Crop.Should().Be(new CropBox(0, 0, 3000, 2000));
    }

    // ─── Cropping ────────────────────────────────────────────────────────

    [Fact]
    public void WiderThanTarget_TrimsWidthAndKeepsFullHeight()
    {
        // 16:9 source onto a 3:2 print — the sides go, the full height stays.
        var plan = PrintLayout.ComputePlan(1920, 1080, Size("4x6"));

        plan.Crop.Height.Should().Be(1080);
        plan.Crop.Width.Should().Be(1620, "1080 × 3/2");
        plan.Crop.Y.Should().Be(0);
    }

    [Fact]
    public void TallerThanTarget_TrimsHeightAndKeepsFullWidth()
    {
        // A 1:2 panorama onto a portrait 4x6 (2:3) — the source is narrower than
        // the print, so top and bottom go and the full width stays.
        var plan = PrintLayout.ComputePlan(2000, 4000, Size("4x6"));

        plan.Crop.Width.Should().Be(2000);
        plan.Crop.Height.Should().Be(3000, "2000 ÷ (4/6)");
        plan.Crop.X.Should().Be(0);
        plan.Crop.Y.Should().Be(500, "(4000 − 3000) / 2");
    }

    [Fact]
    public void Crop_IsCentred()
    {
        var plan = PrintLayout.ComputePlan(4000, 1000, Size("4x6"));

        plan.Crop.Width.Should().Be(1500);
        plan.Crop.X.Should().Be(1250, "(4000 − 1500) / 2 — equal margins each side");
        plan.Crop.Y.Should().Be(0);
    }

    [Fact]
    public void Crop_NeverExceedsSourceBounds()
    {
        // Extreme ratios are where rounding can push the crop off the edge.
        foreach (var size in PrintSize.Catalog)
        {
            foreach (var (w, h) in new[] { (10000, 3), (3, 10000), (1, 1), (7, 13) })
            {
                var plan = PrintLayout.ComputePlan(w, h, size);

                plan.Crop.X.Should().BeGreaterThanOrEqualTo(0);
                plan.Crop.Y.Should().BeGreaterThanOrEqualTo(0);
                (plan.Crop.X + plan.Crop.Width).Should().BeLessThanOrEqualTo(w,
                    $"{size.Code} at {w}×{h} must stay inside the source");
                (plan.Crop.Y + plan.Crop.Height).Should().BeLessThanOrEqualTo(h,
                    $"{size.Code} at {w}×{h} must stay inside the source");
                plan.Crop.Width.Should().BeGreaterThan(0);
                plan.Crop.Height.Should().BeGreaterThan(0);
            }
        }
    }

    [Fact]
    public void CropAspect_MatchesTargetAspect()
    {
        foreach (var size in PrintSize.Catalog)
        {
            var plan = PrintLayout.ComputePlan(4032, 3024, size);

            var cropAspect = (double)plan.Crop.Width / plan.Crop.Height;
            var targetAspect = (double)plan.TargetWidthPx / plan.TargetHeightPx;

            cropAspect.Should().BeApproximately(targetAspect, 0.01,
                $"{size.Code} must not stretch — crop and target aspect have to agree");
        }
    }

    // ─── Square sizes ────────────────────────────────────────────────────

    [Fact]
    public void SquareSize_ProducesSquareOutput_FromEitherOrientation()
    {
        foreach (var (w, h) in new[] { (4000, 3000), (3000, 4000) })
        {
            var plan = PrintLayout.ComputePlan(w, h, Size("4x4"));

            plan.TargetWidthPx.Should().Be(1200);
            plan.TargetHeightPx.Should().Be(1200);
            plan.Crop.Width.Should().Be(plan.Crop.Height);
        }
    }

    // ─── Effective DPI / quality floor ───────────────────────────────────

    [Fact]
    public void EffectiveDpi_ReportsRealResolution_NotRenderDpi()
    {
        // 1800px across a 6in print is exactly 300dpi.
        var plan = PrintLayout.ComputePlan(1800, 1200, Size("4x6"));

        plan.EffectiveDpi.Should().BeApproximately(300.0, 0.5);
        plan.IsBelowQualityFloor.Should().BeFalse();
    }

    [Fact]
    public void SmallSourceOnLargePrint_FlagsQualityFloor_WithoutFailing()
    {
        // A 1024px-wide photo blown up to 16x20: upscaling to 300dpi adds no
        // detail, and EffectiveDpi is what exposes that.
        var plan = PrintLayout.ComputePlan(1024, 768, Size("16x20"));

        plan.EffectiveDpi.Should().BeLessThan(PrintPlan.QualityFloorDpi);
        plan.IsBelowQualityFloor.Should().BeTrue();
        plan.TargetWidthPx.Should().Be(6000, "the plan still renders — it warns, it does not reject");
    }

    [Fact]
    public void ModernPhonePhoto_ClearsTheFloor_OnEverydaySizes()
    {
        foreach (var code in new[] { "4x6", "5x7", "8x10", "wallet" })
        {
            var plan = PrintLayout.ComputePlan(4032, 3024, Size(code));

            plan.IsBelowQualityFloor.Should().BeFalse(
                $"a 12MP phone photo should print cleanly at {code}");
        }
    }

    // ─── Catalog / codes ─────────────────────────────────────────────────

    [Fact]
    public void FromCode_IsCaseInsensitive_AndRejectsUnknownCodes()
    {
        PrintSize.FromCode("4X6").Code.Should().Be("4x6");
        PrintSize.TryFromCode("nonsense", out _).Should().BeFalse();
        PrintSize.TryFromCode(null, out _).Should().BeFalse();
        PrintSize.TryFromCode("  ", out _).Should().BeFalse();

        var act = () => PrintSize.FromCode("11x17");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Catalog_CodesAreUnique_AndShortEdgeNeverExceedsLong()
    {
        PrintSize.Catalog.Select(s => s.Code).Should().OnlyHaveUniqueItems();

        foreach (var size in PrintSize.Catalog)
        {
            size.ShortInches.Should().BeLessThanOrEqualTo(size.LongInches,
                $"{size.Code} has its edges the wrong way round, which would invert every orientation decision");
        }
    }

    [Fact]
    public void SquareDetection_MatchesTheCatalog()
    {
        Size("4x4").IsSquare.Should().BeTrue();
        Size("8x8").IsSquare.Should().BeTrue();
        Size("4x6").IsSquare.Should().BeFalse();
    }

    // ─── Guards ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    public void NonPositiveSourceDimensions_Throw(int width, int height)
    {
        var act = () => PrintLayout.ComputePlan(width, height, Size("4x6"));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CustomDpi_ScalesTargetPixels()
    {
        var plan = PrintLayout.ComputePlan(4000, 3000, Size("4x6"), dpi: 150);

        plan.TargetWidthPx.Should().Be(900);
        plan.TargetHeightPx.Should().Be(600);
    }
}
