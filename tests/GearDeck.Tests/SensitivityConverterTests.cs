using System.Globalization;
using GearDeck.Core.Services;
using Xunit;

public class SensitivityConverterTests
{
    [Fact]
    public void Cs2ToValorant_And_Apex_MatchesExpectedYawRatios()
    {
        var toValorant = SensitivityConverter.Convert(new SensitivityConversionRequest(
            BaseGameId: "cs2",
            BaseSensitivity: 1.0,
            BaseFov: 90,
            TargetGameId: "valorant",
            TargetFov: 103));

        Assert.InRange(toValorant.TargetSensitivitySelected, 0.314 - 0.002, 0.314 + 0.002);
        Assert.InRange(toValorant.BaseCm360, 51.94, 51.96);

        var toApex = SensitivityConverter.Convert(new SensitivityConversionRequest(
            BaseGameId: "cs2",
            BaseSensitivity: 1.0,
            BaseFov: 90,
            TargetGameId: "apex",
            TargetFov: 110));

        Assert.Equal(1.00, toApex.TargetSensitivitySelected, 2);
    }

    [Fact]
    public void DpiDoubling_800To1600_HalvesLinear_AndSubtracts15InPubg()
    {
        var cs2 = SensitivityConverter.Convert(new SensitivityConversionRequest(
            BaseGameId: "cs2",
            BaseSensitivity: 1.0,
            BaseFov: 90,
            TargetGameId: "cs2",
            TargetFov: 90,
            BaseDpi: 800,
            TargetDpi: 1600));

        Assert.Equal(0.5, cs2.TargetSensitivitySelected, 6);
        Assert.Equal(cs2.BaseCm360, cs2.TargetCm360, 6);

        var pubg = SensitivityConverter.Convert(new SensitivityConversionRequest(
            BaseGameId: "pubg",
            BaseSensitivity: 50,
            BaseFov: 90,
            TargetGameId: "pubg",
            TargetFov: 90,
            BaseDpi: 800,
            TargetDpi: 1600));

        Assert.InRange(pubg.TargetSensitivitySelected, 34.90, 35.00);
        Assert.Equal("35", pubg.FormattedSelected);
    }

    [Fact]
    public void Mdh100ScaleRatio_UsesActualHFovLinearRatio()
    {
        var result = SensitivityConverter.Convert(new SensitivityConversionRequest(
            BaseGameId: "cs2",
            BaseSensitivity: 1.0,
            BaseFov: 90,
            TargetGameId: "the_finals",
            TargetFov: 90,
            Method: SensitivityConversionMethod.MonitorDistanceH100));

        Assert.Equal(result.TargetActualHFov / result.BaseActualHFov, result.Mdh100ScaleRatio, 12);
        Assert.InRange(result.Mdh100ScaleRatio, 1.14, 1.15);
        Assert.Equal(result.TargetSensitivityCm360 * result.Mdh100ScaleRatio, result.TargetSensitivityMdh100, 8);

        var sameFov = SensitivityConverter.Convert(new SensitivityConversionRequest(
            BaseGameId: "cs2",
            BaseSensitivity: 1.0,
            BaseFov: 90,
            TargetGameId: "cs2",
            TargetFov: 90));

        Assert.Equal(1.0, sameFov.Mdh100ScaleRatio, 12);
        Assert.Equal(sameFov.TargetSensitivityCm360, sameFov.TargetSensitivityMdh100, 12);
    }

    [Fact]
    public void FovScaling_Mdv0_And_Mdh100_RespondToFovAndAspectRatio()
    {
        SensitivityConversionResult ConvertAt(double targetFov) => SensitivityConverter.Convert(
            new SensitivityConversionRequest(
                BaseGameId: "cs2",
                BaseSensitivity: 1.0,
                BaseFov: 90,
                TargetGameId: "the_finals",
                TargetFov: targetFov,
                Method: SensitivityConversionMethod.FocalLengthMdv0));

        var at90 = ConvertAt(90);
        Assert.InRange(at90.TargetActualVFov, 89.99, 90.01);
        Assert.InRange(at90.TargetActualHFov, 121.2, 121.4);
        Assert.True(at90.TargetSensitivityFovScaled > at90.TargetSensitivityCm360);
        Assert.True(at90.TargetSensitivityMdh100 > at90.TargetSensitivityCm360);

        var at100 = ConvertAt(100);
        Assert.True(at100.TargetSensitivityFovScaled > at90.TargetSensitivityFovScaled);
        Assert.True(at100.TargetSensitivityMdh100 > at90.TargetSensitivityMdh100);
    }

    [Fact]
    public void ResolutionAndStretch_ComputeActualFov_And_PixelsPerCount()
    {
        var stretched = SensitivityConverter.ComputeActualFov(
            FovBasis.Horizontal4By3HorPlus, 90, 1280, 960, stretchedTo16By9: true);
        Assert.Equal(1.3333, stretched.StretchFactor, 3);

        var cs2At16By9 = SensitivityConverter.ComputeActualFov(
            FovBasis.Horizontal4By3HorPlus, 90, 1920, 1080, stretchedTo16By9: false);
        Assert.Equal(106.26, cs2At16By9.ActualHFov, 2);

        SensitivityConversionResult ConvertAt(int width, int height) => SensitivityConverter.Convert(
            new SensitivityConversionRequest(
                BaseGameId: "cs2",
                BaseSensitivity: 1.0,
                BaseFov: 90,
                TargetGameId: "cs2",
                TargetFov: 90,
                TargetWidth: width,
                TargetHeight: height));

        var at4K = ConvertAt(3840, 2160);
        var at1080p = ConvertAt(1920, 1080);
        Assert.Equal(2.0, at4K.PixelsPerCountAtTargetRes / at1080p.PixelsPerCountAtTargetRes, 6);
    }

    [Fact]
    public void PubgExponential_UsesActualHFov_AndInvertsConsistently()
    {
        var pubg = SensitivityConverter.FindById("pubg");
        Assert.Equal(SensitivityScaleKind.PubgExponential, pubg.ScaleKind);
        Assert.True(pubg.PhysicalYawAffectedByFov);

        var deg90 = SensitivityConverter.DegreesPerCountFromSens(pubg, 50.0, 90.0);
        var deg103 = SensitivityConverter.DegreesPerCountFromSens(pubg, 50.0, 103.0);
        Assert.Equal(0.00005555 * 10.0 * 90.0, deg90, 10);
        Assert.Equal(103.0 / 90.0, deg103 / deg90, 10);
        Assert.Equal(50.0, SensitivityConverter.SensFromDegreesPerCount(pubg, deg90, 90.0), 8);
        Assert.Equal(50.0, SensitivityConverter.SensFromDegreesPerCount(pubg, deg103, 103.0), 8);

        var toWider = SensitivityConverter.Convert(new SensitivityConversionRequest(
            BaseGameId: "pubg",
            BaseSensitivity: 50.0,
            BaseFov: 90,
            TargetGameId: "pubg",
            TargetFov: 103));

        Assert.InRange(toWider.TargetSensitivitySelected, 46.0, 48.0);
        Assert.Equal(
            50.0 + 50.0 * Math.Log10(toWider.BaseActualHFov / toWider.TargetActualHFov),
            toWider.TargetSensitivitySelected,
            8);
        Assert.Contains(toWider.DynamicWarnings,
            w => w.Contains("实际横向 FOV") && w.Contains("HFOV"));
    }

    [Fact]
    public void BattlefieldAffine_ForwardInverse_AndConfigOffsets()
    {
        var bfv = SensitivityConverter.FindById("battlefield_v");
        var bf6 = SensitivityConverter.FindById("battlefield_6");
        Assert.Equal(SensitivityScaleKind.Affine, bfv.ScaleKind);
        Assert.Equal(SensitivityScaleKind.Affine, bf6.ScaleKind);
        Assert.Same(bfv, SensitivityConverter.FindById("battlefield"));
        Assert.Same(bf6, SensitivityConverter.FindById("BATTLEFIELD_6"));

        foreach (var game in new[] { bfv, bf6 })
        {
            var deg = SensitivityConverter.DegreesPerCountFromSens(game, 10.0);
            Assert.Equal(10.0 * game.YawDegreesPerCount + game.YawOffsetDegreesPerCount, deg, 10);
            Assert.Equal(10.0, SensitivityConverter.SensFromDegreesPerCount(game, deg), 10);
        }

        var bfvResult = SensitivityConverter.Convert(new SensitivityConversionRequest(
            BaseGameId: "battlefield_v",
            BaseSensitivity: 10,
            BaseFov: 55,
            TargetGameId: "battlefield_v",
            TargetFov: 55));
        Assert.Equal(10.0, bfvResult.TargetSensitivitySelected, 10);
        Assert.Equal("GstInput.MouseSensitivity 0.020000", bfvResult.ConfigSnippet);

        var bf6Result = SensitivityConverter.Convert(new SensitivityConversionRequest(
            BaseGameId: "battlefield_6",
            BaseSensitivity: 10,
            BaseFov: 90,
            TargetGameId: "battlefield_6",
            TargetFov: 90));
        Assert.Equal(10.0, bf6Result.TargetSensitivitySelected, 10);
        Assert.Equal("GstInput.MouseSensitivity 0.008500", bf6Result.ConfigSnippet);

        var cs2ToBfv = SensitivityConverter.Convert(new SensitivityConversionRequest(
            BaseGameId: "cs2",
            BaseSensitivity: 1.0,
            BaseFov: 90,
            TargetGameId: "battlefield_v",
            TargetFov: 55));
        Assert.Equal(
            (0.022 - bfv.YawOffsetDegreesPerCount) / bfv.YawDegreesPerCount,
            cs2ToBfv.TargetSensitivitySelected,
            8);
        var expectedExport = 0.005 + cs2ToBfv.TargetSensitivitySelected * 0.0015;
        Assert.Equal(
            "GstInput.MouseSensitivity " + expectedExport.ToString("F6", CultureInfo.InvariantCulture),
            cs2ToBfv.ConfigSnippet);
    }

    [Fact]
    public void AffineBelowMinimum_ClampsToZero_AndWarns()
    {
        var result = SensitivityConverter.Convert(new SensitivityConversionRequest(
            BaseGameId: "cs2",
            BaseSensitivity: 0.01,
            BaseFov: 90,
            TargetGameId: "battlefield_v",
            TargetFov: 55));

        Assert.Equal(0.0, result.TargetSensitivityCm360, 12);
        Assert.Equal(0.0, result.TargetSensitivitySelected, 12);
        Assert.Contains(result.DynamicWarnings,
            w => w.Contains("最小灵敏度下限") && w.Contains("钳位"));

        var fromZero = SensitivityConverter.Convert(new SensitivityConversionRequest(
            BaseGameId: "battlefield_v",
            BaseSensitivity: 0.0,
            BaseFov: 55,
            TargetGameId: "cs2",
            TargetFov: 90));

        Assert.True(double.IsFinite(fromZero.BaseCm360) && fromZero.BaseCm360 > 0);
        Assert.Equal(fromZero.BaseGame.YawOffsetDegreesPerCount / 0.022, fromZero.TargetSensitivityCm360, 8);
    }

    [Fact]
    public void DeadlockYaw_Is0044_AndHalvesCs2Sensitivity()
    {
        var deadlock = SensitivityConverter.FindById("deadlock");
        Assert.Equal(0.044, deadlock.YawDegreesPerCount, 12);

        var result = SensitivityConverter.Convert(new SensitivityConversionRequest(
            BaseGameId: "cs2",
            BaseSensitivity: 1.0,
            BaseFov: 90,
            TargetGameId: "deadlock",
            TargetFov: 90));

        Assert.Equal(0.5, result.TargetSensitivitySelected, 10);
    }

    [Fact]
    public void DeltaForceYaw_Is003_AndDocumentsSourceDisagreement()
    {
        var delta = SensitivityConverter.FindById("delta_force");
        Assert.Equal(0.03, delta.YawDegreesPerCount, 12);
        Assert.NotNull(delta.FormulaSourceNote);
        Assert.Contains("KovaaK", delta.FormulaSourceNote!);
        Assert.Contains("0.03", delta.FormulaSourceNote!);
        Assert.Contains("Aiming.Pro", delta.FormulaSourceNote!);
        Assert.Contains("0.01", delta.FormulaSourceNote!);
        Assert.Contains("360", delta.FormulaSourceNote!);

        var result = SensitivityConverter.Convert(new SensitivityConversionRequest(
            BaseGameId: "cs2",
            BaseSensitivity: 1.0,
            BaseFov: 90,
            TargetGameId: "delta_force",
            TargetFov: 100));
        Assert.Contains(result.DynamicWarnings,
            w => w.Contains("KovaaK") && w.Contains("Aiming.Pro") && w.Contains("360"));
    }

    [Fact]
    public void HuntAndFortnite_HaveExpectedProfiles_AndRoundTrip()
    {
        var hunt = SensitivityConverter.FindById("hunt");
        Assert.Equal(0.0429718162181364, hunt.YawDegreesPerCount, 12);
        Assert.Equal(FovBasis.Vertical, hunt.FovBasis);
        Assert.Equal(55, hunt.DefaultFov);
        Assert.Equal(55, hunt.MinFov);
        Assert.Equal(90, hunt.MaxFov);
        Assert.Equal(2, hunt.RecommendedDecimals);
        Assert.Contains("Hunt: Showdown 1896", hunt.Name, StringComparison.OrdinalIgnoreCase);

        var fortnite = SensitivityConverter.FindById("fortnite");
        Assert.Equal(0.005555, fortnite.YawDegreesPerCount, 12);
        Assert.Equal(FovBasis.Horizontal16By9Locked, fortnite.FovBasis);
        Assert.Equal(80, fortnite.DefaultFov);
        Assert.Equal(80, fortnite.MinFov);
        Assert.Equal(80, fortnite.MaxFov);
        Assert.True(fortnite.FovLocked);
        Assert.Equal(1, fortnite.RecommendedDecimals);
        Assert.Contains("Fortnite", fortnite.Name, StringComparison.OrdinalIgnoreCase);

        foreach (var id in new[] { "hunt", "fortnite" })
        {
            var result = SensitivityConverter.Convert(new SensitivityConversionRequest(
                BaseGameId: id,
                BaseSensitivity: 7.0,
                BaseFov: 123,
                TargetGameId: id,
                TargetFov: 123));
            Assert.Equal(7.0, result.TargetSensitivitySelected, 8);
            Assert.Equal(result.BaseCm360, result.TargetCm360, 8);
        }

        var locked = SensitivityConverter.Convert(new SensitivityConversionRequest(
            BaseGameId: "fortnite",
            BaseSensitivity: 7.0,
            BaseFov: 103,
            TargetGameId: "fortnite",
            TargetFov: 103));
        Assert.Equal(80, locked.BaseActualHFov, 8);
        Assert.Equal(80, locked.TargetActualHFov, 8);
    }

    [Fact]
    public void AllGamesCatalog_Has19ValidDistinctProfiles_AndRoundTrips()
    {
        var profiles = SensitivityConverter.AllGames;
        Assert.Equal(19, profiles.Count);
        Assert.Equal(19, profiles.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var profile in profiles)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.Id));
            Assert.False(string.IsNullOrWhiteSpace(profile.Name));
            Assert.False(string.IsNullOrWhiteSpace(profile.ShortTag));
            Assert.False(string.IsNullOrWhiteSpace(profile.Category));
            Assert.False(string.IsNullOrWhiteSpace(profile.AccentHex));
            Assert.False(string.IsNullOrWhiteSpace(profile.SettingPath));
            Assert.False(string.IsNullOrWhiteSpace(profile.ConfigTip));
            Assert.False(string.IsNullOrWhiteSpace(profile.AdsAndPitfallTip));
            Assert.True(profile.YawDegreesPerCount > 0 && double.IsFinite(profile.YawDegreesPerCount));
            Assert.True(profile.YawOffsetDegreesPerCount >= 0 && double.IsFinite(profile.YawOffsetDegreesPerCount));
            Assert.True(profile.RecommendedDecimals >= 0);
            Assert.True(profile.MinFov <= profile.DefaultFov && profile.DefaultFov <= profile.MaxFov);
            Assert.True(profile.ConfigExportMultiplier > 0 && double.IsFinite(profile.ConfigExportMultiplier));
            Assert.True(profile.ConfigExportOffset >= 0 && double.IsFinite(profile.ConfigExportOffset));
        }

        foreach (var baseGame in profiles)
        {
            foreach (var targetGame in profiles)
            {
                var forward = SensitivityConverter.Convert(new SensitivityConversionRequest(
                    BaseGameId: baseGame.Id,
                    BaseSensitivity: 50.0,
                    BaseFov: baseGame.DefaultFov,
                    TargetGameId: targetGame.Id,
                    TargetFov: targetGame.DefaultFov));

                Assert.True(double.IsFinite(forward.TargetSensitivitySelected) && forward.TargetSensitivitySelected > 0);
                Assert.True(double.IsFinite(forward.BaseCm360) && forward.BaseCm360 > 0);
                Assert.True(double.IsFinite(forward.TargetCm360) && forward.TargetCm360 > 0);
                Assert.True(double.IsFinite(forward.FovScaleRatio) && forward.FovScaleRatio > 0);
                Assert.True(double.IsFinite(forward.Mdh100ScaleRatio) && forward.Mdh100ScaleRatio > 0);
                Assert.True(double.IsFinite(forward.PixelsPerCountAtTargetRes) && forward.PixelsPerCountAtTargetRes > 0);
                Assert.NotNull(forward.DynamicWarnings);

                var back = SensitivityConverter.Convert(new SensitivityConversionRequest(
                    BaseGameId: targetGame.Id,
                    BaseSensitivity: forward.TargetSensitivitySelected,
                    BaseFov: targetGame.DefaultFov,
                    TargetGameId: baseGame.Id,
                    TargetFov: baseGame.DefaultFov));

                Assert.Equal(50.0, back.TargetSensitivitySelected, 6);
                Assert.Equal(forward.BaseCm360, back.TargetCm360, 6);
            }
        }
    }
}
