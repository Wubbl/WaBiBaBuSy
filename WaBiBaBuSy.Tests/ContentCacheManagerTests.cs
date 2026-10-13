using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Models.Content;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Review 2026-10-13 E3: eviction keeps the index and everything used recently.</summary>
public class ContentCacheManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wbbs-cache-{Guid.NewGuid():N}");

    public ContentCacheManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string File1Mb(string name, TimeSpan age)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[1024 * 1024]);
        var t = DateTime.UtcNow - age;
        File.SetLastWriteTimeUtc(path, t);
        File.SetLastAccessTimeUtc(path, t);
        return path;
    }

    [Fact]
    public void OverLimit_EvictsOldFilesOnly_NeverTheIndexOrRecentOnes()
    {
        var index = File1Mb(ContentCacheIndex.FileName, TimeSpan.FromDays(30));
        var old1 = File1Mb(Path.Combine("aaaa", "old1.gif"), TimeSpan.FromDays(3));
        var old2 = File1Mb(Path.Combine("bbbb", "old2.gif"), TimeSpan.FromDays(2));
        var showAsset = File1Mb(Path.Combine("cccc", "show.gif"), TimeSpan.FromMinutes(1));
        var justDownloaded = File1Mb(Path.Combine("dddd", "new.mp4"), TimeSpan.Zero);

        new ContentCacheManager(NullLogger<ContentCacheManager>.Instance, _dir, maxCacheSizeMB: 3).EnsureSpace(0);

        Assert.True(File.Exists(index));
        Assert.False(File.Exists(old1));          // oldest first
        Assert.False(File.Exists(old2));
        Assert.True(File.Exists(showAsset));      // in use: stays even though the cache is still over the limit
        Assert.True(File.Exists(justDownloaded));
    }
}
