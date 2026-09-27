using System.Text.Json;
using WaBiBaBuSy.Models.Configuration;
using Xunit;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// Settings Save must only change the fields the dialog shows. Everything else in the file
/// (ClientId, update settings, ServiceType, log-file limits) has to survive, including values
/// written by someone else after the dialog loaded.
/// </summary>
public class ConfigurationUpdateTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wbbs-config-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private void Write<T>(T config) => File.WriteAllText(_path, JsonSerializer.Serialize(config, Json));
    private T Read<T>() => JsonSerializer.Deserialize<T>(File.ReadAllText(_path), Json)!;

    [Fact]
    public void Update_PreservesFieldsTheEditDoesNotTouch_Client()
    {
        Write(new ClientConfiguration
        {
            ClientId = "id-123",
            ServerAddress = "old",
            UpdateSettings = new UpdateSettingsConfiguration { MaxBackupsToKeep = 9, AutoApplyUpdates = true }
        });

        ConfigurationManager.UpdateJsonFile<ClientConfiguration>(_path, c => c.ServerAddress = "10.0.0.5");

        var saved = Read<ClientConfiguration>();
        Assert.Equal("10.0.0.5", saved.ServerAddress);
        Assert.Equal("id-123", saved.ClientId);
        Assert.Equal(9, saved.UpdateSettings.MaxBackupsToKeep);
        Assert.True(saved.UpdateSettings.AutoApplyUpdates);
    }

    [Fact]
    public void Update_PreservesFieldsTheEditDoesNotTouch_Server()
    {
        Write(new ServerConfiguration
        {
            ServiceType = "_custom._tcp",
            UpdateManagement = new UpdateManagementConfiguration { MaxDeferralDays = 3 }
        });

        ConfigurationManager.UpdateJsonFile<ServerConfiguration>(_path, s => s.Port = 6000);

        var saved = Read<ServerConfiguration>();
        Assert.Equal(6000, saved.Port);
        Assert.Equal("_custom._tcp", saved.ServiceType);
        Assert.Equal(3, saved.UpdateManagement.MaxDeferralDays);
    }

    [Fact]
    public void Update_PreservesFieldsTheEditDoesNotTouch_Logging()
    {
        Write(new LoggingConfiguration { MaxLogFileSizeMB = 42, MaxLogFiles = 11 });

        var result = ConfigurationManager.UpdateJsonFile<LoggingConfiguration>(_path, l => l.Level = "Debug");

        var saved = Read<LoggingConfiguration>();
        Assert.Equal("Debug", saved.Level);
        Assert.Equal(42, saved.MaxLogFileSizeMB);
        Assert.Equal(11, saved.MaxLogFiles);
        Assert.Equal("Debug", result.Level);
    }

    [Fact]
    public void Update_PreservesFieldWrittenAfterLoad()
    {
        // Dialog opened with no ClientId; the client then connected and UpdateClientId wrote one.
        Write(new ClientConfiguration { ClientId = "" });
        var dialogSnapshot = Read<ClientConfiguration>();
        Write(new ClientConfiguration { ClientId = "assigned-later" });

        ConfigurationManager.UpdateJsonFile<ClientConfiguration>(_path,
            c => c.ServerAddress = dialogSnapshot.ServerAddress + "host");

        Assert.Equal("assigned-later", Read<ClientConfiguration>().ClientId);
    }

    [Fact]
    public void Update_MissingFile_StartsFromDefaults()
    {
        ConfigurationManager.UpdateJsonFile<ClientConfiguration>(_path, c => c.ServerAddress = "x");

        var saved = Read<ClientConfiguration>();
        Assert.Equal("x", saved.ServerAddress);
        Assert.Equal(new ClientConfiguration().MaxCacheSizeMB, saved.MaxCacheSizeMB);
    }

    [Fact]
    public void Update_CorruptFile_StartsFromDefaults()
    {
        File.WriteAllText(_path, "{ not json");

        ConfigurationManager.UpdateJsonFile<ServerConfiguration>(_path, s => s.Port = 7000);

        Assert.Equal(7000, Read<ServerConfiguration>().Port);
    }

    [Fact]
    public void Update_ConcurrentEdits_AllSurvive()
    {
        File.WriteAllText(_path, "{}");

        const int count = 50;
        Parallel.For(0, count, i =>
        {
            ConfigurationManager.UpdateJsonFile<Dictionary<string, int>>(_path, d => d[$"k{i}"] = i);
        });

        var saved = Read<Dictionary<string, int>>();
        Assert.Equal(count, saved.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(i, saved[$"k{i}"]);
        }
    }

    [Fact]
    public void Update_ReadIoError_Throws()
    {
        Write(new ClientConfiguration { ServerAddress = "original" });
        var before = File.ReadAllText(_path);

        using (new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() =>
                ConfigurationManager.UpdateJsonFile<ClientConfiguration>(_path, c => c.ServerAddress = "x"));
        }

        Assert.Equal(before, File.ReadAllText(_path));
    }
}
