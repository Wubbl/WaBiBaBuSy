using System;
using System.Collections.Generic;
using System.Globalization;

namespace WaBiBaBuSy.Models.Wallpaper;

/// <summary>
/// Pure formatting of gallery media metadata (resolution, length, frames, frame rate)
/// for thumbnail captions and tooltips.
/// </summary>
public static class MediaDetails
{
    /// <summary>
    /// GIF frame delay as the D2D player plays it: 0 (or negative) = 10 ms, capped at 1000 ms.
    /// </summary>
    public static int NormalizeGifDelayMs(int delayMs) => delayMs <= 0 ? 10 : Math.Min(delayMs, 1000);

    /// <summary>
    /// Compact length: "2.4 s" under 10 s, "12 s" under a minute, then "m:ss" / "h:mm:ss".
    /// Empty for zero or negative lengths.
    /// </summary>
    public static string FormatDuration(long durationMs)
    {
        if (durationMs <= 0)
            return string.Empty;

        var inv = CultureInfo.InvariantCulture;
        if (durationMs < 10_000)
            return (durationMs / 1000.0).ToString("0.0", inv) + " s";
        if (durationMs < 60_000)
            return (durationMs / 1000).ToString(inv) + " s";

        var t = TimeSpan.FromMilliseconds(durationMs);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Frame rate with up to two decimals ("30 fps", "29.97 fps"); empty when unknown.</summary>
    public static string FormatFrameRate(double fps) =>
        fps > 0 ? fps.ToString("0.##", CultureInfo.InvariantCulture) + " fps" : string.Empty;

    /// <summary>"WxH" resolution for display; "Unknown" and blanks become empty.</summary>
    public static string CleanResolution(string? resolution) =>
        string.IsNullOrWhiteSpace(resolution) || resolution == "Unknown" ? string.Empty : resolution.Replace('x', '×');

    /// <summary>
    /// One-line caption for a thumbnail: resolution plus, for animated media, the length
    /// (video) or frame count and length (GIF).
    /// </summary>
    public static string CaptionLine(string type, string? resolution, long durationMs, int frameCount)
    {
        var parts = new List<string>(3);
        var res = CleanResolution(resolution);
        if (res.Length > 0) parts.Add(res);

        if (type == "Gif" && frameCount > 1)
            parts.Add($"{frameCount} fr");
        var len = type is "Gif" or "Video" ? FormatDuration(durationMs) : string.Empty;
        if (len.Length > 0) parts.Add(len);

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Multi-line tooltip: name, type · resolution · size, animation details, full path.
    /// </summary>
    public static string ToolTip(string name, string type, string? resolution, string fileSize,
        long durationMs, int frameCount, double frameRate, string filePath)
    {
        var lines = new List<string> { name };

        var head = new List<string> { type == "Gif" ? "GIF" : type };
        var res = CleanResolution(resolution);
        if (res.Length > 0) head.Add(res);
        if (!string.IsNullOrEmpty(fileSize)) head.Add(fileSize);
        lines.Add(string.Join(" · ", head));

        var detail = new List<string>();
        if (type == "Gif")
        {
            if (frameCount > 0) detail.Add(frameCount == 1 ? "1 frame (static)" : $"{frameCount} frames");
            if (frameCount > 1 && durationMs > 0)
            {
                detail.Add(FormatDuration(durationMs) + " loop");
                detail.Add("≈" + FormatFrameRate(frameCount * 1000.0 / durationMs));
            }
        }
        else if (type == "Video")
        {
            var len = FormatDuration(durationMs);
            if (len.Length > 0) detail.Add(len);
            var fps = FormatFrameRate(frameRate);
            if (fps.Length > 0) detail.Add(fps);
            if (frameCount > 0) detail.Add(frameCount.ToString("N0", CultureInfo.InvariantCulture) + " frames");
        }
        if (detail.Count > 0) lines.Add(string.Join(" · ", detail));

        if (!string.IsNullOrEmpty(filePath)) lines.Add(filePath);
        return string.Join(Environment.NewLine, lines);
    }
}
