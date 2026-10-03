using System.Globalization;

namespace WaBiBaBuSy.Models.Testing;

/// <summary>
/// The fields of Win32 <c>DWM_TIMING_INFO</c> (from <c>DwmGetCompositionTimingInfo(NULL, …)</c>) the present-time
/// estimate needs. QPC values are raw <c>QueryPerformanceCounter</c> ticks; rates are numerator / denominator in Hz.
/// </summary>
public struct DwmTimingSnapshot
{
    /// <summary><c>qpcVBlank</c>: QPC of a vertical blank of DWM's display clock (may be the last or the next one).</summary>
    public long QpcVBlank { get; set; }
    /// <summary><c>qpcRefreshPeriod</c>: refresh period in QPC ticks (some drivers report nonsense; see <see cref="DwmPresentEstimator"/>).</summary>
    public long QpcRefreshPeriod { get; set; }
    /// <summary><c>rateRefresh.uiNumerator</c>.</summary>
    public uint RefreshRateNumerator { get; set; }
    /// <summary><c>rateRefresh.uiDenominator</c>.</summary>
    public uint RefreshRateDenominator { get; set; }
    /// <summary><c>rateCompose.uiNumerator</c>.</summary>
    public uint ComposeRateNumerator { get; set; }
    /// <summary><c>rateCompose.uiDenominator</c>.</summary>
    public uint ComposeRateDenominator { get; set; }
    /// <summary><c>qpcCompose</c>: QPC of a DWM composition pass; 0 = unknown (compositions assumed at vblank).</summary>
    public long QpcCompose { get; set; }
}

/// <summary>Result of <see cref="DwmPresentEstimator.Estimate"/>.</summary>
/// <param name="ScanoutQpc">Estimated QPC at which the frame reached the screen; null when no estimate is possible.</param>
/// <param name="Source"><see cref="PresentSources.Dwm"/>, <see cref="PresentSources.Nominal"/>, or null without an estimate.</param>
/// <param name="Note">Why this source was used (for logs and the report); never null.</param>
public readonly record struct DwmPresentEstimate(long? ScanoutQpc, string? Source, string Note);

/// <summary>
/// Present-time fallback for when DXGI frame statistics are unusable (failed, or S_OK with all zeros).
/// <para>
/// Model: the wallpaper swap chain is composed by DWM (layered, parented to WorkerW — never independent flip) with
/// <c>MaximumFrameLatency = 1</c>. A frame whose <c>Present</c> returned at QPC <c>t</c> is picked up by the first DWM
/// composition strictly after <c>t</c> (compositions recur every compose period from <c>qpcCompose</c>), and DWM's
/// composed frame becomes visible at the first vblank at least <see cref="MinComposeLeadMs"/> after that composition
/// started (vblanks recur every refresh period from <c>qpcVBlank</c>). Both references are used only for their phase,
/// so it does not matter whether DWM reports the last or the next vblank / compose.
/// This is the same instant DXGI's <c>SyncQPCTime</c> reports for a composed swap chain (the vblank at which the
/// frame was shown), i.e. typically 1–2 refresh periods after Present.
/// </para>
/// <para>
/// Limits: since Windows 8.1 DWM timing is only available for the whole desktop (hwnd must be NULL), i.e. one display
/// clock. When the player's monitor runs at a different rate (mixed-refresh setups), that clock says nothing about
/// this monitor's vblank phase, so the estimate falls back to <see cref="PresentSources.Nominal"/>: Present + 1.5 of
/// the monitor's own refresh periods — the mean of the 1–2 period composed-present latency, error within ±½ period.
/// The same applies when DWM timing is unavailable or stale. A second monitor at the same nominal rate but with an
/// unsynchronized vblank cannot be detected; its estimate can be up to one refresh period off.
/// </para>
/// </summary>
public static class DwmPresentEstimator
{
    /// <summary>A DWM reference vblank / compose further than this from the present is not extrapolated.</summary>
    public const double MaxReferenceAgeMs = 2000;
    /// <summary>DWM refresh rate and monitor rate differing by more than this fraction count as different clocks.</summary>
    public const double RefreshMismatchTolerance = 0.03;
    /// <summary>
    /// A composition that starts closer than this before a vblank cannot make it (capped at a quarter period):
    /// its result is shown at the following vblank. Also keeps a compose reported "at" the vblank from rounding
    /// onto that vblank.
    /// </summary>
    public const double MinComposeLeadMs = 1.0;

    /// <summary>
    /// Estimate when the frame presented at <paramref name="presentQpc"/> reached the screen.
    /// </summary>
    /// <param name="presentQpc">QPC right after <c>Present</c> returned for the frame.</param>
    /// <param name="timing">DWM timing, or null when <c>DwmGetCompositionTimingInfo</c> failed.</param>
    /// <param name="qpcFrequency">QPC ticks per second.</param>
    /// <param name="monitorRefreshHz">The player's own monitor refresh rate; ≤ 0 = unknown (DWM's clock is trusted).</param>
    public static DwmPresentEstimate Estimate(long presentQpc, DwmTimingSnapshot? timing, long qpcFrequency, double monitorRefreshHz)
    {
        if (qpcFrequency <= 0) return new DwmPresentEstimate(null, null, "no QPC frequency");
        if (timing is not { } t) return Nominal(presentQpc, qpcFrequency, monitorRefreshHz, "DWM timing unavailable");

        double refreshPeriod = RefreshPeriodTicks(t, qpcFrequency);
        if (refreshPeriod <= 0) return Nominal(presentQpc, qpcFrequency, monitorRefreshHz, "DWM reported no refresh period");

        double maxAgeTicks = MaxReferenceAgeMs * qpcFrequency / 1000.0;
        double vblankOffset = t.QpcVBlank - presentQpc;   // small (bounded below), so double keeps full tick precision
        if (t.QpcVBlank == 0 || Math.Abs(vblankOffset) > maxAgeTicks)
            return Nominal(presentQpc, qpcFrequency, monitorRefreshHz, "DWM vblank time stale");

        double dwmHz = qpcFrequency / refreshPeriod;
        if (monitorRefreshHz > 0 && Math.Abs(dwmHz - monitorRefreshHz) > RefreshMismatchTolerance * monitorRefreshHz)
            return Nominal(presentQpc, qpcFrequency, monitorRefreshHz,
                string.Create(CultureInfo.InvariantCulture, $"DWM clock {dwmHz:F1} Hz is another display than this {monitorRefreshHz:0.##} Hz monitor"));

        // Composition phase and period. No (or a stale) compose time: compositions at vblank.
        double composePeriod = t.ComposeRateNumerator > 0 && t.ComposeRateDenominator > 0
            ? (double)qpcFrequency * t.ComposeRateDenominator / t.ComposeRateNumerator
            : refreshPeriod;
        if (composePeriod < qpcFrequency / 1000.0) composePeriod = refreshPeriod;
        double composeOffset = t.QpcCompose != 0 && Math.Abs((double)(t.QpcCompose - presentQpc)) <= maxAgeTicks
            ? t.QpcCompose - presentQpc
            : vblankOffset;

        // First composition strictly after the present.
        double compose = composeOffset + (Math.Floor(-composeOffset / composePeriod) + 1) * composePeriod;
        // First vblank at least the compose lead after that composition.
        double lead = Math.Min(MinComposeLeadMs * qpcFrequency / 1000.0, refreshPeriod / 4);
        double earliest = compose + lead;
        double scanout = vblankOffset + Math.Ceiling((earliest - vblankOffset) / refreshPeriod) * refreshPeriod;

        return new DwmPresentEstimate(presentQpc + (long)Math.Round(scanout), PresentSources.Dwm,
            string.Create(CultureInfo.InvariantCulture, $"DWM composition timing ({dwmHz:F1} Hz)"));
    }

    /// <summary>
    /// <c>qpcRefreshPeriod</c>, unless it is implausible (below 1 ms, or below half the period from
    /// <c>rateRefresh</c> — both seen from real drivers, per Chromium's VSync provider); then the rate's period.
    /// 0 when neither is usable.
    /// </summary>
    private static double RefreshPeriodTicks(DwmTimingSnapshot t, long qpcFrequency)
    {
        double ratePeriod = t.RefreshRateNumerator > 0 && t.RefreshRateDenominator > 0
            ? (double)qpcFrequency * t.RefreshRateDenominator / t.RefreshRateNumerator
            : 0;
        double period = t.QpcRefreshPeriod;
        if (period < qpcFrequency / 1000.0 || (ratePeriod > 0 && period < ratePeriod / 2)) period = ratePeriod;
        return period;
    }

    /// <summary>Present + 1.5 of the monitor's refresh periods (mean composed-present latency, ±½ period).</summary>
    private static DwmPresentEstimate Nominal(long presentQpc, long qpcFrequency, double monitorRefreshHz, string why)
    {
        if (monitorRefreshHz <= 0)
            return new DwmPresentEstimate(null, null, why + "; monitor refresh rate unknown");
        double latency = 1.5 * qpcFrequency / monitorRefreshHz;
        return new DwmPresentEstimate(presentQpc + (long)Math.Round(latency), PresentSources.Nominal,
            string.Create(CultureInfo.InvariantCulture, $"{why}; Present + 1.5 refresh periods at {monitorRefreshHz:0.##} Hz"));
    }
}
