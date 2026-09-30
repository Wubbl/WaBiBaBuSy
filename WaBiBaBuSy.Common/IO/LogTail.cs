using System.Globalization;

namespace WaBiBaBuSy.Common.IO;

/// <summary>
/// Reads the tail of a log file that another writer (the app's own rolling file logger) keeps open.
/// </summary>
public static class LogTail
{
    /// <summary>
    /// The last <paramref name="maxLines"/> lines of <paramref name="path"/>. Opens with
    /// <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/>, so a
    /// <see cref="StreamWriter"/> holding the file for appending does not block the read.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadLastLinesAsync(string path, int maxLines, CancellationToken ct = default)
    {
        var tail = new Queue<string>(Math.Max(1, maxLines));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024, useAsync: true);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (maxLines <= 0) continue;
            if (tail.Count == maxLines) tail.Dequeue();
            tail.Enqueue(line);
        }
        return tail.ToArray();
    }

    /// <summary>
    /// Lines whose leading <c>[HH:mm:ss.fff</c> local timestamp lies in [<paramref name="fromLocal"/>,
    /// <paramref name="toLocal"/>] (local time of day). Unstamped lines (stack traces) follow the
    /// preceding stamped line. A window that crosses midnight returns every line.
    /// </summary>
    public static IReadOnlyList<string> FilterWindow(IReadOnlyList<string> lines, TimeSpan fromLocal, TimeSpan toLocal)
    {
        if (toLocal < fromLocal) return lines;
        var result = new List<string>();
        bool inWindow = false;
        foreach (var line in lines)
        {
            if (TryParseTime(line, out var time)) inWindow = time >= fromLocal && time <= toLocal;
            if (inWindow) result.Add(line);
        }
        return result;
    }

    // FileLoggerProvider format: "[12:34:56.789 INF] [Category] message"
    private static bool TryParseTime(string line, out TimeSpan time)
    {
        time = default;
        return line.Length >= 13 && line[0] == '['
            && TimeSpan.TryParseExact(line.AsSpan(1, 12), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out time);
    }
}
