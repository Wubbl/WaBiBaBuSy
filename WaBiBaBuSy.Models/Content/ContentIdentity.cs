using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace WaBiBaBuSy.Models.Content;

/// <summary>
/// Content ids that identify bytes, not names: <c>{fileName}-{first 16 hex of SHA-256}</c>.
/// Two different files with the same name get different ids (no collision in the client cache),
/// and a re-exported file with the same name is fetched again instead of served stale.
/// Hashes are cached per (path, size, mtime) so repeated applies do not re-read large GIFs.
/// </summary>
public static class ContentIdentity
{
    private static readonly ConcurrentDictionary<string, (long Size, DateTime Mtime, string Id)> Cache = new();

    public const int HashPrefixLength = 16;

    /// <summary>Content id for a file; falls back to the bare file name when the file cannot be read.</summary>
    public static string ComputeId(string filePath)
    {
        var name = Path.GetFileName(filePath);
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists) return name;
            if (Cache.TryGetValue(filePath, out var cached) && cached.Size == fi.Length && cached.Mtime == fi.LastWriteTimeUtc)
                return cached.Id;

            using var stream = File.OpenRead(filePath);
            var hash = SHA256.HashData(stream);
            var id = Format(name, hash);
            Cache[filePath] = (fi.Length, fi.LastWriteTimeUtc, id);
            return id;
        }
        catch
        {
            return name;
        }
    }

    /// <summary>Pure formatter, exposed for tests and for callers that already have the hash.</summary>
    public static string Format(string fileName, ReadOnlySpan<byte> sha256)
    {
        var hex = Convert.ToHexString(sha256);
        return $"{fileName}-{hex[..Math.Min(HashPrefixLength, hex.Length)].ToLowerInvariant()}";
    }

    /// <summary>True when the id carries a hash suffix (i.e. was produced by <see cref="ComputeId"/>).</summary>
    public static bool HasHash(string contentId)
    {
        int dash = contentId.LastIndexOf('-');
        if (dash < 0 || contentId.Length - dash - 1 != HashPrefixLength) return false;
        foreach (var c in contentId.AsSpan(dash + 1))
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
