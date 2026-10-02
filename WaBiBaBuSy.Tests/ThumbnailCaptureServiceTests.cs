using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Which remote player window a client uploads as its thumbnail (<see cref="ThumbnailCaptureService.ShouldAdopt"/>):
/// the primary monitor always, another monitor only while no live window is assigned — also after a Stop cleared it.
/// </summary>
public class ThumbnailCaptureServiceTests
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    private static ThumbnailCaptureService NewService() => new(NullLogger<ThumbnailCaptureService>.Instance);

    [Fact]
    public void Fresh_AdoptsAnyMonitor()
    {
        var capture = NewService();
        Assert.True(capture.ShouldAdopt(1));
        Assert.True(capture.ShouldAdopt(0));
    }

    [Fact]
    public void LiveWindowAssigned_OnlyThePrimaryMonitorTakesOver()
    {
        var capture = NewService();
        capture.SetWallpaperHwnd(GetDesktopWindow(), "scene");

        Assert.False(capture.ShouldAdopt(1));
        Assert.True(capture.ShouldAdopt(0));
    }

    [Fact]
    public void AfterClear_ASceneWithoutMonitorZero_AdoptsItsWindowAgain()
    {
        var capture = NewService();
        capture.SetWallpaperHwnd(GetDesktopWindow(), "scene");
        capture.ClearWallpaperHwnd();

        Assert.True(capture.ShouldAdopt(1));
    }

    [Fact]
    public void AssignedWindowGone_AnotherMonitorAdopts()
    {
        var capture = NewService();
        capture.SetWallpaperHwnd(new IntPtr(0x7FFF_0001), "disposed player");   // not a window

        Assert.True(capture.ShouldAdopt(1));
    }
}
