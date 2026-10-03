using WaBiBaBuSy.Models.Testing;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class DwmPresentEstimatorTests
{
    // 6 MHz QPC → a 60 Hz refresh period is exactly 100 000 ticks.
    private const long Freq = 6_000_000;
    private const long Period60 = 100_000;

    /// <summary>60 Hz DWM clock: vblanks at …, 1 000 000, 1 100 000, …; compositions 5 000 ticks after each vblank.</summary>
    private static DwmTimingSnapshot Timing60(long vblank = 1_000_000, long compose = 1_005_000) => new()
    {
        QpcVBlank = vblank,
        QpcRefreshPeriod = Period60,
        RefreshRateNumerator = 60, RefreshRateDenominator = 1,
        ComposeRateNumerator = 60, ComposeRateDenominator = 1,
        QpcCompose = compose,
    };

    [Fact]
    public void PresentBeforeThisCyclesCompose_ShownAtTheNextVBlank()
    {
        // Present 2 000 ticks after the vblank, before the compose at +5 000 → composed at 1 005 000, shown at 1 100 000.
        var e = DwmPresentEstimator.Estimate(1_002_000, Timing60(), Freq, monitorRefreshHz: 60);
        Assert.Equal(1_100_000, e.ScanoutQpc);
        Assert.Equal(PresentSources.Dwm, e.Source);
    }

    [Fact]
    public void PresentAfterThisCyclesCompose_WaitsForTheNextCompose()
    {
        // Present at +20 000: missed the compose at 1 005 000 → composed at 1 105 000, shown at 1 200 000.
        var e = DwmPresentEstimator.Estimate(1_020_000, Timing60(), Freq, monitorRefreshHz: 60);
        Assert.Equal(1_200_000, e.ScanoutQpc);
    }

    [Fact]
    public void ReferenceTimesInTheFuture_GiveTheSamePhase()
    {
        // DWM may report the next (not the last) vblank / compose; only the phase matters.
        var e = DwmPresentEstimator.Estimate(1_020_000, Timing60(vblank: 1_500_000, compose: 1_505_000), Freq, 60);
        Assert.Equal(1_200_000, e.ScanoutQpc);
    }

    [Fact]
    public void PresentExactlyAtACompose_IsTooLateForIt()
    {
        var e = DwmPresentEstimator.Estimate(1_005_000, Timing60(), Freq, 60);
        Assert.Equal(1_200_000, e.ScanoutQpc);
    }

    [Fact]
    public void NoComposeTime_ComposeAssumedAtVBlank_ShownOneRefreshAfterIt()
    {
        // Compose at the vblank 1 100 000 (strictly after the present) → shown at the following vblank.
        var e = DwmPresentEstimator.Estimate(1_020_000, Timing60(compose: 0), Freq, 60);
        Assert.Equal(1_200_000, e.ScanoutQpc);
    }

    [Fact]
    public void HalfRateComposition_UsesTheComposePeriod()
    {
        var t = Timing60();
        t.ComposeRateNumerator = 30;   // compositions every 200 000 ticks: 1 005 000, 1 205 000, …
        var e = DwmPresentEstimator.Estimate(1_020_000, t, Freq, 60);
        Assert.Equal(1_300_000, e.ScanoutQpc);
    }

    [Fact]
    public void ImplausibleQpcRefreshPeriod_FallsBackToTheRefreshRate()
    {
        // Chromium saw qpcRefreshPeriod = 60 ticks from some drivers; the rate (60 Hz) is used instead.
        var t = Timing60();
        t.QpcRefreshPeriod = 60;
        var e = DwmPresentEstimator.Estimate(1_020_000, t, Freq, 60);
        Assert.Equal(1_200_000, e.ScanoutQpc);
        Assert.Equal(PresentSources.Dwm, e.Source);
    }

    [Fact]
    public void FractionalMonitorRate_MatchesTheDwmClock()
    {
        // The player reports 59 Hz for a 59.94 Hz panel; that is the same clock, not a mixed-rate setup.
        var t = Timing60();
        t.RefreshRateNumerator = 60000; t.RefreshRateDenominator = 1001;
        t.QpcRefreshPeriod = 100_100;
        var e = DwmPresentEstimator.Estimate(1_020_000, t, Freq, monitorRefreshHz: 59);
        Assert.Equal(PresentSources.Dwm, e.Source);
    }

    [Fact]
    public void MixedRefreshRates_UseTheMonitorsNominalLatency()
    {
        // DWM's clock is the 60 Hz display; this monitor runs at 75 Hz, whose vblank phase DWM does not report:
        // present + 1.5 periods of 75 Hz (= 120 000 ticks).
        var e = DwmPresentEstimator.Estimate(1_020_000, Timing60(), Freq, monitorRefreshHz: 75);
        Assert.Equal(1_020_000 + 120_000, e.ScanoutQpc);
        Assert.Equal(PresentSources.Nominal, e.Source);
        Assert.Contains("60.0 Hz", e.Note);
        Assert.Contains("75", e.Note);
    }

    [Fact]
    public void NoDwmTiming_UsesTheMonitorsNominalLatency()
    {
        var e = DwmPresentEstimator.Estimate(1_020_000, null, Freq, monitorRefreshHz: 60);
        Assert.Equal(1_020_000 + 150_000, e.ScanoutQpc);
        Assert.Equal(PresentSources.Nominal, e.Source);
    }

    [Fact]
    public void StaleDwmTiming_UsesTheMonitorsNominalLatency()
    {
        // The vblank reference is 5 s away from the present: extrapolating its phase is not trusted.
        var e = DwmPresentEstimator.Estimate(1_020_000 + 5 * Freq, Timing60(), Freq, monitorRefreshHz: 60);
        Assert.Equal(PresentSources.Nominal, e.Source);
        Assert.Contains("stale", e.Note);
    }

    [Fact]
    public void NoDwmTimingAndNoRefreshRate_NoEstimate()
    {
        var e = DwmPresentEstimator.Estimate(1_020_000, null, Freq, monitorRefreshHz: 0);
        Assert.Null(e.ScanoutQpc);
        Assert.Null(e.Source);
        Assert.False(string.IsNullOrEmpty(e.Note));
    }

    [Fact]
    public void UnusableDwmRates_UnknownMonitorRate_NoEstimate()
    {
        var t = Timing60();
        t.QpcRefreshPeriod = 0;
        t.RefreshRateNumerator = 0;
        var e = DwmPresentEstimator.Estimate(1_020_000, t, Freq, monitorRefreshHz: 0);
        Assert.Null(e.ScanoutQpc);
    }

    [Fact]
    public void UnknownMonitorRate_TrustsTheDwmClock()
    {
        var e = DwmPresentEstimator.Estimate(1_020_000, Timing60(), Freq, monitorRefreshHz: 0);
        Assert.Equal(1_200_000, e.ScanoutQpc);
        Assert.Equal(PresentSources.Dwm, e.Source);
    }

    [Theory]
    [InlineData(10_000_000L, 165.0, 3_000L)]     // 10 MHz QPC, 165 Hz, compose 0.3 ms after vblank
    [InlineData(10_000_000L, 75.0, 0L)]
    [InlineData(3_579_545L, 59.94, 1_234L)]      // ACPI PM timer frequency, NTSC rate
    public void Estimate_AlwaysAfterThePresent_AndWithinTwoComposesAndOneRefresh(long freq, double hz, long composeOffset)
    {
        long period = (long)Math.Round(freq / hz);
        var t = new DwmTimingSnapshot
        {
            QpcVBlank = 50_000_000,
            QpcRefreshPeriod = period,
            RefreshRateNumerator = (uint)Math.Round(hz * 1000), RefreshRateDenominator = 1000,
            ComposeRateNumerator = (uint)Math.Round(hz * 1000), ComposeRateDenominator = 1000,
            QpcCompose = 50_000_000 + composeOffset,
        };
        for (long present = 50_000_000 - 3 * period; present < 50_000_000 + 3 * period; present += period / 7 + 1)
        {
            var e = DwmPresentEstimator.Estimate(present, t, freq, hz);
            Assert.Equal(PresentSources.Dwm, e.Source);
            Assert.True(e.ScanoutQpc > present, $"present {present} → {e.ScanoutQpc}");
            Assert.True(e.ScanoutQpc <= present + 2 * period + 1, $"present {present} → {e.ScanoutQpc}");
            Assert.True(e.ScanoutQpc - present >= period - composeOffset - 1, "a composed frame is never shown sooner than one refresh after the compose that took it");
        }
    }
}
