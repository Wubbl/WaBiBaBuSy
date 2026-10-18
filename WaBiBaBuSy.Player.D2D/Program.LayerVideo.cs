// WaBiBaBuSy.Player.D2D/Program.LayerVideo.cs
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.WallpaperEngine.Services;

namespace WaBiBaBuSy.Player.D2D;

/// <summary>
/// Video on a layer above the bottom one (roadmap 2.3 follow-up §4.1). Layer 0's video keeps its own render branch;
/// an upper layer gets one of these: its own LibVLC decoding into a double buffer, uploaded into a D2D bitmap that
/// the normal layer draw takes as <c>videoBitmapOverride</c>, and a timer that keeps it on the layer's shared clock
/// (same seek / rate rules as layer 0). At most <see cref="SceneChecks.MaxVideoLayers"/> videos per scene.
/// </summary>
partial class Program
{
    private sealed class LayerVideo : IDisposable
    {
        private readonly SceneLayer _layer;
        private readonly LibVLC _vlc;
        private readonly LibVLCSharp.Shared.MediaPlayer _player;
        private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;      // held: LibVLC calls them for the player's lifetime
        private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;
        private readonly System.Threading.Timer _syncTimer;
        private readonly int _bufferSize;
        private IntPtr _bufferA, _bufferB;
        private volatile IntPtr _writeBuffer, _readBuffer;
        private volatile bool _frameReady;
        private float _rate = 1f;
        private bool _disposed;

        public int Width { get; }
        public int Height { get; }
        public long DurationMs { get; }
        public ID2D1Bitmap Bitmap { get; }

        public LayerVideo(string filePath, SceneLayer layer, ID2D1DeviceContext context)
        {
            _layer = layer;
            LibVLCSharp.Shared.Core.Initialize(LibVLCPreloader.GetLibDirectory());
            _vlc = new LibVLC(enableDebugLogs: false, "--no-video-title-show", "--no-audio", "--file-caching=300", "--avcodec-hw=any");
            _player = new LibVLCSharp.Shared.MediaPlayer(_vlc);

            using (var media = new Media(_vlc, filePath, FromType.FromPath))
            {
                media.Parse(MediaParseOptions.ParseLocal).Wait();
                DurationMs = media.Duration;
                var track = media.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
                Width = track.TrackType == TrackType.Video && track.Data.Video.Width > 0 ? (int)track.Data.Video.Width : 1920;
                Height = track.TrackType == TrackType.Video && track.Data.Video.Height > 0 ? (int)track.Data.Video.Height : 1080;
            }

            _bufferSize = Width * Height * 4;
            _bufferA = Marshal.AllocHGlobal(_bufferSize);
            _bufferB = Marshal.AllocHGlobal(_bufferSize);
            unsafe
            {
                new Span<byte>((void*)_bufferA, _bufferSize).Clear();
                new Span<byte>((void*)_bufferB, _bufferSize).Clear();
            }
            _writeBuffer = _bufferA;
            _readBuffer = _bufferB;

            _lockCb = (opaque, planes) => { Marshal.WriteIntPtr(planes, _writeBuffer); return IntPtr.Zero; };
            _displayCb = (opaque, picture) =>
            {
                var oldRead = Interlocked.Exchange(ref _readBuffer, _writeBuffer);
                _writeBuffer = oldRead;
                _frameReady = true;
            };
            _player.SetVideoFormat("RV32", (uint)Width, (uint)Height, (uint)(Width * 4));
            _player.SetVideoCallbacks(_lockCb, null, _displayCb);

            Bitmap = context.CreateBitmap(new SizeI(Width, Height), IntPtr.Zero, 0,
                new BitmapProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96f, 96f));

            using var playMedia = new Media(_vlc, filePath, FromType.FromPath);
            if (layer.Animation.Loop) playMedia.AddOption(":input-repeat=65535");
            _player.Media = playMedia;
            _player.Play();   // decode ahead; Sync pauses / seeks once the shared start is known
            _syncTimer = new System.Threading.Timer(_ => Sync(), null, 500, 500);
            _logger?.LogInformation("[LAYER-VIDEO] {Path}: {W}x{H}, {Duration} ms", filePath, Width, Height, DurationMs);
        }

        /// <summary>Render thread: the newest decoded frame into <see cref="Bitmap"/> (no-op when nothing new).</summary>
        public ID2D1Bitmap CurrentFrame()
        {
            if (_frameReady && !_disposed)
            {
                _frameReady = false;
                var read = _readBuffer;
                if (read != IntPtr.Zero) Bitmap.CopyFromMemory(read, (uint)(Width * 4));
            }
            return Bitmap;
        }

        /// <summary>Timer thread: hold the video on this layer's clock (paused on frame 0 before its start).</summary>
        private void Sync()
        {
            try
            {
                lock (_compositionLock)
                {
                    if (_disposed || !_running || !_isPlaying || DurationMs <= 0) return;
                    long raw = (long)(NowUtc - _renderLoopStart).TotalMilliseconds;
                    long? elapsed = SyncTiming.LayerElapsed(raw, _nodeOrderForLayers, _layer);
                    if (elapsed is not long e || e < 0)
                    {
                        if (_player.IsPlaying) _player.SetPause(true);
                        if (_player.Time > 0) _player.Time = 0;
                        return;
                    }
                    bool loop = _layer.Animation.Loop;
                    if (!loop && e >= DurationMs) return;   // held on the last frame
                    if (!_player.IsPlaying) _player.Play();
                    long now = _player.Time;
                    if (now < 0) return;

                    long target = SyncTiming.VideoTargetMs(e, DurationMs, loop);
                    long drift = SyncTiming.VideoDriftMs(now, target, DurationMs, loop);
                    float rate = 1f;
                    if (Math.Abs(drift) > SyncTiming.VideoSeekThresholdMs) _player.Time = target;
                    else rate = SyncTiming.VideoCorrectionRate(drift);
                    if (rate != _rate) { _player.SetRate(rate); _rate = rate; }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[LAYER-VIDEO] sync step failed");
            }
        }

        public void Dispose()
        {
            lock (_compositionLock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            _syncTimer.Dispose();
            try { _player.Stop(); } catch { /* already stopped */ }
            _player.Dispose();
            _vlc.Dispose();
            Bitmap.Dispose();
            if (_bufferA != IntPtr.Zero) { Marshal.FreeHGlobal(_bufferA); _bufferA = IntPtr.Zero; }
            if (_bufferB != IntPtr.Zero) { Marshal.FreeHGlobal(_bufferB); _bufferB = IntPtr.Zero; }
        }
    }

    /// <summary>This node's traversal index for the layers' clocks (the seat-map order; 0 without one).</summary>
    private static int _nodeOrderForLayers => _sequentialLayout?.Order ?? 0;
}
