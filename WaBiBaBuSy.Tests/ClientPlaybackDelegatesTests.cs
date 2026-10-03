using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Core.Services.Networking;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Wallpaper;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Client mode: the D2D delegates are registered once (app lifetime) and must reach every playback
/// service a connect creates — tray connect, main-window connect and reconnects alike.
/// </summary>
public class ClientPlaybackDelegatesTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "wbbs-delegates-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); } catch { /* may not exist */ }
    }

    private WallpaperPlaybackService NewPlayback() => new(
        NullLogger<WallpaperPlaybackService>.Instance,
        new WallpaperSyncClient(NullLogger<WallpaperSyncClient>.Instance, new ClientConfiguration { CacheDirectory = _cacheDir }),
        cacheDirectory: _cacheDir);

    private static Task Apply(string path, int monitor, string bg, int fit) => Task.CompletedTask;
    private static Task CrossScreen(CrossScreenApplyRequest request) => Task.CompletedTask;
    private static Task Stop() => Task.CompletedTask;

    [Fact]
    public void ApplyTo_SetsAllThreeDelegates()
    {
        var delegates = new ClientPlaybackDelegates { D2DApply = Apply, CrossScreenApply = CrossScreen, CrossScreenStop = Stop };
        var playback = NewPlayback();

        delegates.ApplyTo(playback);

        Assert.Equal(Apply, playback.D2DApplyDelegate);
        Assert.Equal(CrossScreen, playback.D2DCrossScreenApplyDelegate);
        Assert.Equal(Stop, playback.D2DCrossScreenStopDelegate);
    }

    [Fact]
    public void RegisteredBeforeConnect_ReachesEveryLaterPlaybackService()
    {
        var delegates = new ClientPlaybackDelegates { D2DApply = Apply, CrossScreenApply = CrossScreen, CrossScreenStop = Stop };

        // Two connects (e.g. connect, disconnect, connect again) create two playback services.
        var first = NewPlayback();
        delegates.ApplyTo(first);
        var second = NewPlayback();
        delegates.ApplyTo(second);

        Assert.Equal(CrossScreen, second.D2DCrossScreenApplyDelegate);
        Assert.Equal(Stop, second.D2DCrossScreenStopDelegate);
    }

    [Fact]
    public void Service_KeepsDelegatesSetWhileDisconnected()
    {
        var service = new WaBiBaBuSyService(NullLogger<WaBiBaBuSyService>.Instance, new ServerConfiguration(), new ClientConfiguration { CacheDirectory = _cacheDir });

        // Not connected: there is no playback service yet, the delegates must still be kept.
        service.SetD2DApplyDelegate(Apply);
        service.SetD2DCrossScreenApplyDelegate(CrossScreen);
        service.SetD2DCrossScreenStopDelegate(Stop);

        Assert.Null(service.PlaybackService);
        Assert.Equal(Apply, service.PlaybackDelegates.D2DApply);
        Assert.Equal(CrossScreen, service.PlaybackDelegates.CrossScreenApply);
        Assert.Equal(Stop, service.PlaybackDelegates.CrossScreenStop);
    }
}
