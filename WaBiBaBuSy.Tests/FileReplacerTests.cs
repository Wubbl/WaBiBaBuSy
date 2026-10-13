using WaBiBaBuSy.Updater;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>Review 2026-10-13 E4: a failed update leaves exactly the old install behind.</summary>
public class FileReplacerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wbbs-replace-{Guid.NewGuid():N}");
    private string Install => Path.Combine(_root, "install");
    private string Package => Path.Combine(_root, "package");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task FailedReplace_RestoresEveryOverwrittenFile_AndRemovesAddedOnes()
    {
        Write(Path.Combine(Install, "WaBiBaBuSy.Core.dll"), "old core");
        Write(Path.Combine(Install, "System.Text.Json.dll"), "old dep");          // a dependency, not WaBiBaBuSy.*
        Write(Path.Combine(Install, "runtimes", "x.dll"), "old runtime");          // a subfolder
        Write(Path.Combine(Install, "zz-locked.dll"), "old locked");
        foreach (var name in new[] { "WaBiBaBuSy.Core.dll", "System.Text.Json.dll", Path.Combine("runtimes", "x.dll"), "new-file.dll", "zz-locked.dll" })
            Write(Path.Combine(Package, "binaries", name), "new " + name);

        bool ok;
        using (new FileStream(Path.Combine(Install, "zz-locked.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))   // readable for the backup, not writable
            ok = await new FileReplacer(Install, Path.Combine(_root, "backup")).ReplaceFilesAsync(Package);

        Assert.False(ok);
        Assert.Equal("old core", File.ReadAllText(Path.Combine(Install, "WaBiBaBuSy.Core.dll")));
        Assert.Equal("old dep", File.ReadAllText(Path.Combine(Install, "System.Text.Json.dll")));
        Assert.Equal("old runtime", File.ReadAllText(Path.Combine(Install, "runtimes", "x.dll")));
        Assert.Equal("old locked", File.ReadAllText(Path.Combine(Install, "zz-locked.dll")));
        Assert.False(File.Exists(Path.Combine(Install, "new-file.dll")));
    }
}
