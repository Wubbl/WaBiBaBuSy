using WaBiBaBuSy.Common.IO;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// "View logs" hung because the client read its own log with File.ReadAllLinesAsync while
/// FileLoggerProvider held the file open for writing. LogTail must read through that lock.
/// </summary>
public class LogTailTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wbbs-logtail-{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* best effort */ }
    }

    [Fact]
    public void RootCause_ReadAllLines_FailsWhileLoggerHoldsTheFile()
    {
        using var writer = new StreamWriter(_path, append: true) { AutoFlush = true };   // FileLoggerProvider's open mode
        writer.WriteLine("[12:00:00.000 INF] [Test] hello");
        Assert.ThrowsAny<IOException>(() => File.ReadAllLines(_path));
    }

    [Fact]
    public async Task ReadLastLines_WhileLoggerHoldsTheFile_ReturnsTheTail()
    {
        using var writer = new StreamWriter(_path, append: true) { AutoFlush = true };
        for (int i = 0; i < 10; i++) writer.WriteLine($"[12:00:0{i}.000 INF] [Test] line {i}");

        var lines = await LogTail.ReadLastLinesAsync(_path, 3);

        Assert.Equal(new[]
        {
            "[12:00:07.000 INF] [Test] line 7",
            "[12:00:08.000 INF] [Test] line 8",
            "[12:00:09.000 INF] [Test] line 9",
        }, lines);
    }

    [Fact]
    public async Task ReadLastLines_FewerLinesThanMax_ReturnsAll()
    {
        await File.WriteAllLinesAsync(_path, new[] { "a", "b" });
        Assert.Equal(new[] { "a", "b" }, await LogTail.ReadLastLinesAsync(_path, 500));
    }

    [Fact]
    public void FilterWindow_KeepsStampedLinesInWindow_AndTheirContinuationLines()
    {
        var lines = new[]
        {
            "[11:59:59.999 INF] [A] before",
            "[12:00:00.000 ERR] [A] inside",
            "   at Some.Stack.Trace()",
            "[12:00:05.000 INF] [A] inside too",
            "[12:00:05.001 INF] [A] after",
        };

        var result = LogTail.FilterWindow(lines, new TimeSpan(12, 0, 0), new TimeSpan(0, 12, 0, 5, 0));

        Assert.Equal(new[] { lines[1], lines[2], lines[3] }, result);
    }

    [Fact]
    public void FilterWindow_WindowCrossingMidnight_ReturnsEverything()
    {
        var lines = new[] { "[23:59:59.000 INF] x", "[00:00:01.000 INF] y" };
        Assert.Equal(lines, LogTail.FilterWindow(lines, new TimeSpan(23, 59, 0), new TimeSpan(0, 1, 0)));
    }
}
