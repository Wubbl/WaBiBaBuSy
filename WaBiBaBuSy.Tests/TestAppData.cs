using System.Runtime.CompilerServices;
using WaBiBaBuSy.Models.Configuration;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Tests never touch the real settings: before any test runs, the settings folder (client id, topology, ...)
/// points at a temp folder. Tests that start a real server or client would otherwise rewrite the developer's
/// %APPDATA%\WaBiBaBuSy — often the same machine that runs the LAN party.
/// </summary>
internal static class TestAppData
{
#pragma warning disable CA2255 // a module initializer in a test assembly is the point here
    [ModuleInitializer]
    internal static void RedirectSettings() =>
        AppDataPaths.Root = Path.Combine(Path.GetTempPath(), $"wbbs-appdata-{Environment.ProcessId}");
#pragma warning restore CA2255
}

public class TestAppDataTests
{
    [Fact]
    public void SettingsAndTopology_LiveInATempFolderDuringTests()
    {
        Assert.StartsWith(Path.GetTempPath(), AppDataPaths.Root);
        Assert.StartsWith(AppDataPaths.Root, TopologyStore.DefaultPath);

        ConfigurationManager.UpdateClientId("test-client-id");
        Assert.True(File.Exists(Path.Combine(AppDataPaths.Root, "client-config.json")));
    }
}
