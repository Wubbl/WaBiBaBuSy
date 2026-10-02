using WaBiBaBuSy.Core.Services.Networking;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// What a client sends back for FETCH_LOGS (<see cref="WallpaperSyncClient.ReadLogsForUploadAsync"/>):
/// the tail of today's file, a UTC window across the daily files it touches, or a bracketed message
/// for a missing or unreadable file — never an exception, so the server always gets an answer.
/// </summary>
public class ClientLogUploadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wbbs-logupload-{Guid.NewGuid():N}");

    public ClientLogUploadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string PathFor(DateTime day) => Path.Combine(_dir, $"wabibabusy-{day:yyyy-MM-dd}.log");

    private static long UtcMs(DateTime local) => new DateTimeOffset(local).ToUnixTimeMilliseconds();

    [Fact]
    public async Task NoWindow_ReturnsTheTailOfTodaysFile()
    {
        await File.WriteAllLinesAsync(PathFor(DateTime.Today), Enumerable.Range(0, 600).Select(i => $"line {i}"));

        var lines = (await WallpaperSyncClient.ReadLogsForUploadAsync(_dir)).Split(Environment.NewLine);

        Assert.Equal(500, lines.Length);
        Assert.Equal("line 100", lines[0]);
        Assert.Equal("line 599", lines[^1]);
    }

    [Fact]
    public async Task MissingFile_ReturnsAHintInsteadOfThrowing()
    {
        var text = await WallpaperSyncClient.ReadLogsForUploadAsync(_dir);
        Assert.StartsWith("[No log file at ", text);
        Assert.Contains(_dir, text);
    }

    [Fact]
    public async Task MissingFile_Windowed_ReturnsAHint()
    {
        var day = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Local);
        var text = await WallpaperSyncClient.ReadLogsForUploadAsync(_dir, UtcMs(day), UtcMs(day.AddMinutes(5)));
        Assert.StartsWith("[No log file at ", text);
    }

    [Fact]
    public async Task UnreadableFile_ReturnsTheErrorAsText()
    {
        var path = PathFor(DateTime.Today);
        await File.WriteAllTextAsync(path, "[12:00:00.000 INF] x");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var text = await WallpaperSyncClient.ReadLogsForUploadAsync(_dir);

        Assert.StartsWith($"[Could not read {path}: ", text);
    }

    [Fact]
    public async Task Window_WithinOneDay_KeepsOnlyTheLinesInside()
    {
        var day = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Local);
        await File.WriteAllLinesAsync(PathFor(day), new[]
        {
            "[11:59:00.000 INF] before",
            "[12:00:30.000 INF] inside",
            "[12:06:00.000 INF] after",
        });

        var text = await WallpaperSyncClient.ReadLogsForUploadAsync(_dir, UtcMs(day.AddHours(12)), UtcMs(day.AddHours(12).AddMinutes(5)));

        Assert.Equal("[12:00:30.000 INF] inside", text);
    }

    [Fact]
    public async Task Window_CrossingMidnight_ReadsBothDailyFiles_AndFiltersEach()
    {
        var first = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Local);
        var second = first.AddDays(1);
        await File.WriteAllLinesAsync(PathFor(first), new[]
        {
            "[23:58:00.000 INF] before",
            "[23:59:30.000 INF] late in day one",
        });
        await File.WriteAllLinesAsync(PathFor(second), new[]
        {
            "[00:00:30.000 INF] early in day two",
            "[00:02:00.000 INF] after",
        });

        var text = await WallpaperSyncClient.ReadLogsForUploadAsync(_dir,
            UtcMs(first.AddHours(23).AddMinutes(59)), UtcMs(second.AddMinutes(1)));

        Assert.Equal(new[] { "[23:59:30.000 INF] late in day one", "[00:00:30.000 INF] early in day two" },
            text.Split(Environment.NewLine));
    }
}
