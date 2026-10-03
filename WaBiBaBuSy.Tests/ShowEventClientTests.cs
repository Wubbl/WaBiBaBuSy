using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Core.Services.Networking;
using WaBiBaBuSy.Core.Services.Show;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>EVENT on the client: forwarded at once (players hold it until T0), the image fetched into the cache first.</summary>
public class ShowEventClientTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), $"wbbs-evt-{Guid.NewGuid():N}");
    private readonly WallpaperSyncClient _client;
    private readonly WallpaperPlaybackService _playback;
    private readonly List<ShowEvent> _received = new();

    public ShowEventClientTests()
    {
        _client = new WallpaperSyncClient(NullLogger<WallpaperSyncClient>.Instance, new ClientConfiguration());
        _playback = new WallpaperPlaybackService(NullLogger<WallpaperPlaybackService>.Instance, _client, cacheDirectory: _cacheDir)
        {
            ShowEventDelegate = e => { lock (_received) _received.Add(e); return Task.CompletedTask; },
        };
    }

    public void Dispose()
    {
        _playback.Dispose();
        try { Directory.Delete(_cacheDir, recursive: true); } catch { /* best effort */ }
    }

    private static ShowEvent Evt(ShowEventType type = ShowEventType.Celebration) =>
        new() { EventId = "e1", Type = type, DurationMs = 5000, ImagePath = @"C:\server\logo.png" };

    [Fact]
    public async Task FutureT0_ForwardedImmediately_WithT0FromTheTimestamp()
    {
        var cmd = ShowEventCodec.ToCommand(Evt(), 1);
        cmd.TimestampUtc = Start.ToUnixTimeMilliseconds() + 60_000;   // a minute ahead: must not wait for it
        await _playback.HandleShowEventAsync(cmd).WaitAsync(TimeSpan.FromSeconds(5));
        var e = Assert.Single(_received);
        Assert.Equal(cmd.TimestampUtc, e.T0Utc);
    }

    [Fact]
    public async Task CachedAsset_RewritesTheImagePath()
    {
        Directory.CreateDirectory(_cacheDir);
        var local = Path.Combine(_cacheDir, "logo.png");
        File.WriteAllBytes(local, new byte[] { 1 });
        _playback.RegisterContent("logo.png-0123456789abcdef", local);
        var asset = new ContentAssetRef { ContentId = "logo.png-0123456789abcdef", Role = AssetRole.Event, OriginalPath = @"C:\server\logo.png" };

        await _playback.HandleShowEventAsync(ShowEventCodec.ToCommand(Evt(ShowEventType.Traversal), 1, asset));

        Assert.Equal(local, Assert.Single(_received).ImagePath);
    }

    [Fact]
    public async Task AssetNotDownloadable_ForwardsWithoutImage()
    {
        var asset = new ContentAssetRef { ContentId = "missing.png-ffffffffffffffff", Role = AssetRole.Event, OriginalPath = @"C:\server\missing.png" };
        await _playback.HandleShowEventAsync(ShowEventCodec.ToCommand(Evt(ShowEventType.Traversal), 1, asset));
        Assert.Null(Assert.Single(_received).ImagePath);
    }

    [Fact]
    public async Task NoAsset_DropsTheServerPath()
    {
        await _playback.HandleShowEventAsync(ShowEventCodec.ToCommand(Evt(ShowEventType.Traversal), 1));
        Assert.Null(Assert.Single(_received).ImagePath);     // a server path means nothing on this machine
    }

    [Fact]
    public async Task MalformedEvent_IsIgnored()
    {
        await _playback.HandleShowEventAsync(new SyncCommand { Type = CommandType.Event, EventJson = "{" });
        Assert.Empty(_received);
    }

    [Fact]
    public async Task ThrowingDelegate_DoesNotEscape()
    {
        _playback.ShowEventDelegate = _ => throw new InvalidOperationException("player gone");
        Assert.Null(await Record.ExceptionAsync(() => _playback.HandleShowEventAsync(ShowEventCodec.ToCommand(Evt(), 1))));
    }
}
