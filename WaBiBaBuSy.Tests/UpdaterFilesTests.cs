using WaBiBaBuSy.Common.Update;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// The updater is framework-dependent: an update package that ships only WaBiBaBuSy.Updater.*
/// crashes on the client with FileNotFoundException (System.CommandLine) before logging anything.
/// </summary>
public class UpdaterFilesTests : IDisposable
{
    private const string DepsJson = """
        {
          "targets": {
            ".NETCoreApp,Version=v9.0": {
              "WaBiBaBuSy.Updater/1.0.0": {
                "dependencies": { "System.CommandLine": "2.0.12" },
                "runtime": { "WaBiBaBuSy.Updater.dll": {} }
              },
              "System.CommandLine/2.0.12": {
                "runtime": { "lib/net8.0/System.CommandLine.dll": { "assemblyVersion": "2.0.12.0" } },
                "resources": {
                  "lib/net8.0/de/System.CommandLine.resources.dll": { "locale": "de" },
                  "lib/net8.0/fr/System.CommandLine.resources.dll": { "locale": "fr" }
                }
              }
            }
          }
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wbbs-updater-{Guid.NewGuid():N}");
    private readonly string _install;
    private readonly string _package;

    public UpdaterFilesTests()
    {
        _install = Path.Combine(_root, "install");
        _package = Path.Combine(_root, "package");
        Directory.CreateDirectory(Path.Combine(_install, "de"));
        Directory.CreateDirectory(_package);

        Touch(_install, UpdaterFiles.ExeName);
        Touch(_install, "WaBiBaBuSy.Updater.dll");
        Touch(_install, "WaBiBaBuSy.Updater.runtimeconfig.json");
        File.WriteAllText(Path.Combine(_install, UpdaterFiles.DepsJsonName), DepsJson);
        Touch(_install, "System.CommandLine.dll");
        Touch(_install, Path.Combine("de", "System.CommandLine.resources.dll"));
        Touch(_install, "Unrelated.dll");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void RequiredDependencies_ComeFromDepsJson_WithoutTheUpdaterItself()
    {
        Assert.Equal(["System.CommandLine.dll"], UpdaterFiles.GetRequiredDependencies(_install));
    }

    [Fact]
    public void RequiredDependencies_FallBackToSystemCommandLine_WhenDepsJsonMissing()
    {
        Assert.Equal(["System.CommandLine.dll"], UpdaterFiles.GetRequiredDependencies(_package));
    }

    [Fact]
    public void CopyUpdater_ShipsDependencyAssemblies_AndAvailableResources()
    {
        var updaterDir = Path.Combine(_package, "updater");

        var missing = UpdaterFiles.CopyUpdater(_install, updaterDir);

        Assert.Empty(missing);
        Assert.True(File.Exists(Path.Combine(updaterDir, UpdaterFiles.ExeName)));
        Assert.True(File.Exists(Path.Combine(updaterDir, UpdaterFiles.DepsJsonName)));
        Assert.True(File.Exists(Path.Combine(updaterDir, "System.CommandLine.dll")));
        Assert.True(File.Exists(Path.Combine(updaterDir, "de", "System.CommandLine.resources.dll")));
        Assert.False(File.Exists(Path.Combine(updaterDir, "Unrelated.dll")));
        // fr resources are listed but not installed — optional, so not reported as missing
        Assert.False(Directory.Exists(Path.Combine(updaterDir, "fr")));
    }

    [Fact]
    public void CopyUpdater_ReportsMissingRequiredAssembly()
    {
        File.Delete(Path.Combine(_install, "System.CommandLine.dll"));

        var missing = UpdaterFiles.CopyUpdater(_install, Path.Combine(_package, "updater"));

        Assert.Equal(["System.CommandLine.dll"], missing);
    }

    [Fact]
    public void RepairDependencies_FixesLegacyPackage_FromBinariesFolder()
    {
        // Legacy package: updater/ has only WaBiBaBuSy.Updater.*, binaries/ has the full app
        var updaterDir = Path.Combine(_package, "updater");
        var binariesDir = Path.Combine(_package, "binaries");
        Directory.CreateDirectory(updaterDir);
        Directory.CreateDirectory(binariesDir);
        foreach (var file in Directory.GetFiles(_install, "WaBiBaBuSy.Updater.*"))
            File.Copy(file, Path.Combine(updaterDir, Path.GetFileName(file)));
        Touch(binariesDir, "System.CommandLine.dll");

        Assert.Empty(UpdaterFiles.RepairDependencies(updaterDir, binariesDir));
        Assert.True(File.Exists(Path.Combine(updaterDir, "System.CommandLine.dll")));
    }

    [Fact]
    public void RepairDependencies_ReportsMissing_WhenBinariesLackIt()
    {
        var updaterDir = Path.Combine(_package, "updater");
        Directory.CreateDirectory(updaterDir);
        File.Copy(Path.Combine(_install, UpdaterFiles.DepsJsonName), Path.Combine(updaterDir, UpdaterFiles.DepsJsonName));

        Assert.Equal(["System.CommandLine.dll"],
            UpdaterFiles.RepairDependencies(updaterDir, Path.Combine(_package, "binaries")));
    }

    private static void Touch(string dir, string relative) =>
        File.WriteAllText(Path.Combine(dir, relative), relative);
}
