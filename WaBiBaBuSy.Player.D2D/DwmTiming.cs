using System.Runtime.InteropServices;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.Player.D2D;

/// <summary>
/// <c>DwmGetCompositionTimingInfo</c> for the test-mode present-time fallback (<see cref="DwmPresentEstimator"/>).
/// One cheap, non-blocking call; only made when a probe is resolved, never per frame.
/// </summary>
internal static class DwmTiming
{
    /// <summary>
    /// Win32 <c>DWM_TIMING_INFO</c>. dwmapi.h declares it under <c>#include &lt;pshpack1.h&gt;</c>, hence Pack = 1
    /// (292 bytes). UNSIGNED_RATIO is two UINT32s; QPC_TIME and DWM_FRAME_COUNT are ULONGLONG.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DWM_TIMING_INFO
    {
        public uint cbSize;
        public uint rateRefreshNumerator;
        public uint rateRefreshDenominator;
        public ulong qpcRefreshPeriod;
        public uint rateComposeNumerator;
        public uint rateComposeDenominator;
        public ulong qpcVBlank;
        public ulong cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose;
        public ulong cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame;
        public ulong cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed;
        public ulong qpcFrameDisplayed;
        public ulong cRefreshFrameDisplayed;
        public ulong cFrameComplete;
        public ulong qpcFrameComplete;
        public ulong cFramePending;
        public ulong qpcFramePending;
        public ulong cFramesDisplayed;
        public ulong cFramesComplete;
        public ulong cFramesPending;
        public ulong cFramesAvailable;
        public ulong cFramesDropped;
        public ulong cFramesMissed;
        public ulong cRefreshNextDisplayed;
        public ulong cRefreshNextPresented;
        public ulong cRefreshesDisplayed;
        public ulong cRefreshesPresented;
        public ulong cRefreshStarted;
        public ulong cPixelsReceived;
        public ulong cPixelsDrawn;
        public ulong cBuffersEmpty;
    }

    /// <summary>Since Windows 8.1 <paramref name="hwnd"/> must be NULL (else E_INVALIDARG): desktop-wide timing.</summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO timingInfo);

    private static readonly uint StructSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>();

    /// <summary>Query DWM's composition timing. Returns null on failure; <paramref name="hr"/> is the HRESULT (or exception HRESULT).</summary>
    public static DwmTimingSnapshot? TryQuery(out int hr)
    {
        try
        {
            var info = new DWM_TIMING_INFO { cbSize = StructSize };
            hr = DwmGetCompositionTimingInfo(IntPtr.Zero, ref info);
            if (hr < 0) return null;
            return new DwmTimingSnapshot
            {
                QpcVBlank = (long)info.qpcVBlank,
                QpcRefreshPeriod = (long)info.qpcRefreshPeriod,
                RefreshRateNumerator = info.rateRefreshNumerator,
                RefreshRateDenominator = info.rateRefreshDenominator,
                ComposeRateNumerator = info.rateComposeNumerator,
                ComposeRateDenominator = info.rateComposeDenominator,
                QpcCompose = (long)info.qpcCompose,
            };
        }
        catch (Exception ex)   // DllNotFound / EntryPointNotFound on exotic systems
        {
            hr = ex.HResult;
            return null;
        }
    }
}
