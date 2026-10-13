using System;
using System.IO;
using System.Threading.Tasks;

namespace WaBiBaBuSy.Models;

/// <summary>
/// Settings / state files written so a crash or power loss never leaves half a file: the text goes to
/// a unique <c>{path}.{guid}.tmp</c> first and then replaces <paramref name="path"/> in one rename (review 2026-10-13 E11).
/// A truncated JSON file used to load as "invalid → defaults", and the next save then overwrote the real
/// settings, topology order or cache index with those defaults.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";   // unique: two concurrent saves never share a temp file
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    public static async Task WriteAllTextAsync(string path, string text)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";   // unique: two concurrent saves never share a temp file
        await File.WriteAllTextAsync(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
