# UI Redesign Plan 3 — Settings Sidebar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the one-scroll `SettingsWindow` into a sidebar dialog (Server · Client · Wallpaper · Logging) with rarely changed fields behind Advanced, and stop Save from wiping settings the dialog does not show.

**Architecture:** `ConfigurationManager` gains read-modify-write `Update*Configuration(Action<T>)` methods (re-read file → apply edit → save), so fields the dialog never shows (`ClientId`, `ServiceType`, `UpdateManagement`, `UpdateSettings`, `MaxLogFileSizeMB`, `MaxLogFiles`) survive a Save. `SettingsViewModel` gains `SelectedSectionIndex` and `SaveError`; `SettingsWindow.axaml` becomes a two-column grid (sidebar `ListBox` + one visible section panel). A static `SettingsWindow.ShowSingle()` makes sure only one window is open, whether it was opened from the tray or the `⋯` menu.

**Tech Stack:** .NET 9, Avalonia 12.1.2, CommunityToolkit.Mvvm, xUnit.

**Spec:** `.docs/plans/2026-09-24-ui-redesign-design.md` §6 ("Settings dialog") and §8 step 5.

## Global Constraints

- "Settings dialog: left sidebar (Server · Client · Wallpaper · Logging) instead of one scroll; rarely changed fields (service name, heartbeat, max cache size, max clients) behind Advanced."
- "No setting is added or removed." Every field bound today stays bound, with the same binding name.
- Config file formats (`server-config.json`, `client-config.json`, `logging-config.json`) do not change.
- Tray menu unchanged (spec §6).
- C# 12, file-scoped namespaces, nullable on, XML docs on new public APIs, MVVM (no logic in code-behind beyond window plumbing).
- Tests project references only `WaBiBaBuSy.Models` (UI is `net9.0-windows` WinExe) — testable logic goes in Models.
- After code changes run `./tools/graphify-update.ps1` (not bare `graphify update .`).

## Review Focus

1. **A client connects while Settings is open, then the user saves** → the server-assigned `ClientId` written by `UpdateClientId` must survive (Save re-reads the file instead of using a snapshot). Test: Task 1 `Update_PreservesFieldWrittenAfterLoad`.
2. **Save on a machine whose config has update settings / custom `ServiceType`** → those values must be unchanged afterwards. Test: Task 1 `Update_PreservesFieldsTheEditDoesNotTouch` (server, client, logging).
3. **Settings opened twice (tray + `⋯` menu, or double-click)** → one window, brought to front. Covered by Task 2 manual check 4.
4. **Save fails (file locked / read-only)** → the window stays open and shows a one-line error instead of silently doing nothing. Covered by Task 2 `SaveError` + manual check 6.
5. **Cancel after edits, then reopen** → shows the persisted values, not the discarded edits (a new VM loads from disk each open). Covered by Task 2 manual check 5.

---

## File Structure

| File | Change |
|---|---|
| `WaBiBaBuSy.Models/Configuration/ConfigurationManager.cs` | Add `UpdateServerConfiguration`, `UpdateClientConfiguration`, `UpdateLoggingConfiguration` + internal path-based core `UpdateJsonFile<T>`; `UpdateClientId` reuses it. |
| `WaBiBaBuSy.Models/WaBiBaBuSy.Models.csproj` | `InternalsVisibleTo WaBiBaBuSy.Tests`. |
| `WaBiBaBuSy.Tests/ConfigurationUpdateTests.cs` | New — preservation tests. |
| `WaBiBaBuSy.UI/ViewModels/SettingsViewModel.cs` | Save uses `Update*`; `SelectedSectionIndex`, `SaveError`; Browse commands use a folder picker. |
| `WaBiBaBuSy.UI/Views/SettingsWindow.axaml(.cs)` | Sidebar layout, Advanced expanders, `ShowSingle`, storage provider hookup. |
| `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs`, `TrayViewModel.cs` | Call `SettingsWindow.ShowSingle()`. |
| Docs | Spec status, `UI_ARCHITECTURE.md`, `RECENT_UPDATES.md`, `CLAUDE.md` line. |

---

### Task 1: Read-modify-write config updates (fixes Save wiping hidden fields)

**Files:**
- Modify: `WaBiBaBuSy.Models/Configuration/ConfigurationManager.cs`
- Modify: `WaBiBaBuSy.Models/WaBiBaBuSy.Models.csproj`
- Create: `WaBiBaBuSy.Tests/ConfigurationUpdateTests.cs`
- Modify: `WaBiBaBuSy.UI/ViewModels/SettingsViewModel.cs` (`Save` only)

**Interfaces:**
- Produces:
  - `public static void ConfigurationManager.UpdateServerConfiguration(Action<ServerConfiguration> edit)`
  - `public static void ConfigurationManager.UpdateClientConfiguration(Action<ClientConfiguration> edit)`
  - `public static LoggingConfiguration ConfigurationManager.UpdateLoggingConfiguration(Action<LoggingConfiguration> edit)` (returns the saved config so the caller can `AppLogger.ApplyConfig` it)
  - `internal static T ConfigurationManager.UpdateJsonFile<T>(string path, Action<T> edit) where T : class, new()` — loads `path` (missing/corrupt → `new T()`), applies `edit`, writes indented camelCase JSON, returns the instance. Throws on write failure (like the existing `Save*`).

- [ ] **Step 1: Add InternalsVisibleTo**

In `WaBiBaBuSy.Models/WaBiBaBuSy.Models.csproj`, inside the project root, add:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="WaBiBaBuSy.Tests" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing tests**

Create `WaBiBaBuSy.Tests/ConfigurationUpdateTests.cs`:

```csharp
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
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~ConfigurationUpdateTests`
Expected: build FAIL — `'ConfigurationManager' does not contain a definition for 'UpdateJsonFile'`.

- [ ] **Step 4: Implement**

In `ConfigurationManager.cs`, add after `UpdateClientId` (and replace `UpdateClientId`'s body so it reuses the core):

```csharp
    /// <summary>
    /// Persist the server-assigned client identity. Re-reads the file first so a concurrent
    /// Settings save is not clobbered - only <see cref="ClientConfiguration.ClientId"/> changes.
    /// </summary>
    public static void UpdateClientId(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;
        try
        {
            if (LoadClientConfiguration().ClientId == clientId) return;
            UpdateClientConfiguration(c => c.ClientId = clientId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error persisting client id: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-read the server config, apply <paramref name="edit"/> and save. Fields the edit does
    /// not touch keep their on-disk values.
    /// </summary>
    public static void UpdateServerConfiguration(Action<ServerConfiguration> edit) =>
        UpdateJsonFile(ServerConfigPath, edit);

    /// <summary>
    /// Re-read the client config, apply <paramref name="edit"/> and save. Keeps fields the
    /// dialog never shows, such as <see cref="ClientConfiguration.ClientId"/>.
    /// </summary>
    public static void UpdateClientConfiguration(Action<ClientConfiguration> edit) =>
        UpdateJsonFile(ClientConfigPath, edit);

    /// <summary>
    /// Re-read the logging config, apply <paramref name="edit"/>, save, and return the saved config.
    /// </summary>
    public static LoggingConfiguration UpdateLoggingConfiguration(Action<LoggingConfiguration> edit) =>
        UpdateJsonFile(LoggingConfigPath, edit);

    /// <summary>
    /// Load <paramref name="path"/> (missing or unreadable → defaults), apply <paramref name="edit"/>,
    /// write it back. Throws if the write fails.
    /// </summary>
    internal static T UpdateJsonFile<T>(string path, Action<T> edit) where T : class, new()
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        T config;
        try
        {
            config = File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? new T()
                : new T();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading {Path.GetFileName(path)}: {ex.Message}. Using defaults.");
            config = new T();
        }

        edit(config);
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        return config;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test WaBiBaBuSy.Tests --filter FullyQualifiedName~ConfigurationUpdateTests`
Expected: 6 passed.

- [ ] **Step 6: Switch `SettingsViewModel.Save` to the update methods**

Replace the body of the `try` in `Save()` with:

```csharp
            ConfigurationManager.UpdateServerConfiguration(s =>
            {
                s.Port = ServerPort;
                s.MaxClients = MaxClients;
                s.ContentDirectory = ContentDirectory;
                s.EnableAutoDiscovery = EnableAutoDiscovery;
                s.ServiceName = ServiceName;
            });

            ConfigurationManager.UpdateClientConfiguration(c =>
            {
                c.ServerAddress = ServerAddress;
                c.ServerPort = ClientServerPort;
                c.AutoConnect = AutoConnect;
                c.PreferAutoDiscovery = PreferAutoDiscovery;
                c.CacheDirectory = CacheDirectory;
                c.MaxCacheSizeMB = MaxCacheSizeMB;
                c.HeartbeatIntervalSeconds = HeartbeatIntervalSeconds;
                c.PauseOnFullscreen = PauseOnFullscreen;
            });

            var loggingConfig = ConfigurationManager.UpdateLoggingConfiguration(l =>
            {
                l.Level           = LogLevelDebug ? "Debug" : LogLevelWarning ? "Warning" : LogLevelError ? "Error" : "Information";
                l.LogUI           = LogUI;
                l.LogD2DPlayer    = LogD2DPlayer;
                l.LogComposition  = LogComposition;
                l.LogRenderers    = LogRenderers;
                l.LogNetworking   = LogNetworking;
                l.LogAnimation    = LogAnimation;
                l.LogFileTransfer = LogFileTransfer;
                l.LogToFile       = LogToFile;
                l.LogPerformanceMetrics = LogPerformanceMetrics;
                l.LogFrameByFrame = LogFrameByFrame;
                l.LogDirectory    = LogDirectory;
            });

            // Apply immediately – no restart needed
            AppLogger.ApplyConfig(loggingConfig);

            Console.WriteLine("Settings saved successfully");
            SettingsSaved?.Invoke(this, EventArgs.Empty);
```

- [ ] **Step 7: Build + full test run**

Run: `dotnet build WaBiBaBuSy.sln` then `dotnet test WaBiBaBuSy.Tests`
Expected: 0 errors; all tests green (previous count + 6).

- [ ] **Step 8: Commit**

```bash
git add WaBiBaBuSy.Models WaBiBaBuSy.Tests/ConfigurationUpdateTests.cs WaBiBaBuSy.UI/ViewModels/SettingsViewModel.cs
git commit -m "fix: Settings save keeps ClientId and other fields the dialog does not show"
```

---

### Task 2: Sidebar Settings window

**Files:**
- Modify: `WaBiBaBuSy.UI/ViewModels/SettingsViewModel.cs`
- Modify: `WaBiBaBuSy.UI/Views/SettingsWindow.axaml`, `SettingsWindow.axaml.cs`
- Modify: `WaBiBaBuSy.UI/ViewModels/MainWindowViewModel.cs:927`, `WaBiBaBuSy.UI/ViewModels/TrayViewModel.cs:214-219`

**Interfaces:**
- Consumes: Task 1's `Save()` (unchanged signature).
- Produces:
  - `SettingsViewModel.SelectedSectionIndex` (`int`, 0 Server · 1 Client · 2 Wallpaper · 3 Logging) plus `bool IsServerSection / IsClientSection / IsWallpaperSection / IsLoggingSection`.
  - `SettingsViewModel.SaveError` (`string?`; set on failed save, cleared on the next attempt).
  - `SettingsViewModel.SetStorageProvider(IStorageProvider)`.
  - `public static void SettingsWindow.ShowSingle()`.

- [ ] **Step 1: ViewModel — sections, save error, folder picker**

The UI project has no implicit usings. In `SettingsViewModel.cs` add `using Avalonia.Platform.Storage;`, `using System.IO;` and `using System.Threading.Tasks;`, then add:

```csharp
    // ── Sidebar ──────────────────────────────────────────────────────────────

    /// <summary>Sidebar section: 0 Server, 1 Client, 2 Wallpaper, 3 Logging.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerSection), nameof(IsClientSection),
                              nameof(IsWallpaperSection), nameof(IsLoggingSection))]
    private int _selectedSectionIndex;

    public bool IsServerSection => SelectedSectionIndex == 0;
    public bool IsClientSection => SelectedSectionIndex == 1;
    public bool IsWallpaperSection => SelectedSectionIndex == 2;
    public bool IsLoggingSection => SelectedSectionIndex == 3;

    /// <summary>One-line error shown in the footer when saving failed; null when fine.</summary>
    [ObservableProperty]
    private string? _saveError;

    private IStorageProvider? _storageProvider;

    /// <summary>Called by the window so the Browse buttons can open a folder picker.</summary>
    public void SetStorageProvider(IStorageProvider storageProvider) => _storageProvider = storageProvider;
```

In `Save()`: first line inside the method `SaveError = null;`; in the `catch` add `SaveError = $"Could not save settings: {ex.Message}";` (keep the Console line). The window must stay open on failure — `SettingsSaved` is only raised in the `try`, which is already the case.

Replace the two Browse commands:

```csharp
    [RelayCommand]
    private async Task BrowseContentDirectory()
    {
        var path = await PickFolderAsync("Content directory", ContentDirectory);
        if (path != null) ContentDirectory = path;
    }

    [RelayCommand]
    private async Task BrowseCacheDirectory()
    {
        var path = await PickFolderAsync("Cache directory", CacheDirectory);
        if (path != null) CacheDirectory = path;
    }

    private async Task<string?> PickFolderAsync(string title, string current)
    {
        if (_storageProvider == null) return null;
        var start = Directory.Exists(current)
            ? await _storageProvider.TryGetFolderFromPathAsync(current)
            : null;
        var folders = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
```


- [ ] **Step 2: Window code-behind — single instance + storage provider**

Replace `SettingsWindow.axaml.cs`:

```csharp
using Avalonia.Controls;
using WaBiBaBuSy.UI.ViewModels;

namespace WaBiBaBuSy.UI.Views;

public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    public SettingsWindow()
    {
        InitializeComponent();

        var viewModel = new SettingsViewModel();
        DataContext = viewModel;
        viewModel.SetStorageProvider(StorageProvider);

        viewModel.SettingsSaved += (s, e) => Close();
        viewModel.SettingsCancelled += (s, e) => Close();
    }

    /// <summary>
    /// Show the Settings window, or bring the already open one to the front. Tray and the
    /// main window's ⋯ menu both open it, so a second click must not load a second copy.
    /// </summary>
    public static void ShowSingle()
    {
        if (_open != null)
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }

        _open = new SettingsWindow();
        _open.Closed += (s, e) => _open = null;
        _open.Show();
    }
}
```

Change `WindowStartupLocation="CenterOwner"` to `CenterScreen` in the axaml — `Show()` has no owner, so CenterOwner falls back to the top-left.

- [ ] **Step 3: Call sites**

`MainWindowViewModel.cs:927`:

```csharp
    private void OpenSettings() => Views.SettingsWindow.ShowSingle();
```

`TrayViewModel.cs` `Settings()` command body:

```csharp
        SettingsWindow.ShowSingle();
```

- [ ] **Step 4: Sidebar layout**

Replace `SettingsWindow.axaml` with the layout below. All existing bindings are kept (same names); only placement changes. Advanced (collapsed): Server → Max clients, Service name; Client → Heartbeat interval, Max cache size; Logging → Performance metrics, Frame-by-frame (already labelled "Advanced" today).

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:WaBiBaBuSy.UI.ViewModels"
        xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
        mc:Ignorable="d" d:DesignWidth="680" d:DesignHeight="520"
        x:Class="WaBiBaBuSy.UI.Views.SettingsWindow"
        x:DataType="vm:SettingsViewModel"
        Title="WaBiBaBuSy Settings"
        Width="680" Height="520"
        MinWidth="560" MinHeight="400"
        WindowStartupLocation="CenterScreen">

    <Design.DataContext>
        <vm:SettingsViewModel/>
    </Design.DataContext>

    <Window.Styles>
        <Style Selector="ListBox.sidebar > ListBoxItem">
            <Setter Property="Padding" Value="14,8"/>
            <Setter Property="FontSize" Value="13"/>
        </Style>
        <Style Selector="TextBlock.label">
            <Setter Property="Foreground" Value="#CCCCCC"/>
            <Setter Property="VerticalAlignment" Value="Center"/>
            <Setter Property="Margin" Value="0,0,12,10"/>
        </Style>
        <Style Selector="TextBlock.section">
            <Setter Property="FontSize" Value="16"/>
            <Setter Property="FontWeight" Value="Bold"/>
            <Setter Property="Foreground" Value="White"/>
            <Setter Property="Margin" Value="0,0,0,12"/>
        </Style>
        <Style Selector="Expander">
            <Setter Property="HorizontalAlignment" Value="Stretch"/>
            <Setter Property="Margin" Value="0,6,0,0"/>
        </Style>
    </Window.Styles>

    <Grid RowDefinitions="*,Auto" ColumnDefinitions="150,*">
        <!-- Sidebar -->
        <ListBox Grid.Row="0" Grid.Column="0" Classes="sidebar" Background="#1E1E1E"
                 SelectedIndex="{Binding SelectedSectionIndex}">
            <ListBoxItem Content="Server"/>
            <ListBoxItem Content="Client"/>
            <ListBoxItem Content="Wallpaper"/>
            <ListBoxItem Content="Logging"/>
        </ListBox>

        <ScrollViewer Grid.Row="0" Grid.Column="1" Padding="20">
            <Panel>
                <!-- Server -->
                <StackPanel IsVisible="{Binding IsServerSection}">
                    <TextBlock Classes="section" Text="Server"/>
                    <Grid ColumnDefinitions="Auto,*" RowDefinitions="Auto,Auto,Auto">
                        <TextBlock Grid.Row="0" Classes="label" Text="Port:"/>
                        <NumericUpDown Grid.Row="0" Grid.Column="1" Value="{Binding ServerPort}" Minimum="1" Maximum="65535" Margin="0,0,0,10"/>

                        <TextBlock Grid.Row="1" Classes="label" Text="Content Directory:"/>
                        <Grid Grid.Row="1" Grid.Column="1" ColumnDefinitions="*,Auto" Margin="0,0,0,10">
                            <TextBox Text="{Binding ContentDirectory}" Margin="0,0,5,0"/>
                            <Button Grid.Column="1" Content="Browse" Command="{Binding BrowseContentDirectoryCommand}"/>
                        </Grid>

                        <CheckBox Grid.Row="2" Grid.ColumnSpan="2" Content="Enable Auto-Discovery (mDNS)"
                                  IsChecked="{Binding EnableAutoDiscovery}" Foreground="White"/>
                    </Grid>
                    <Expander Header="Advanced">
                        <Grid ColumnDefinitions="Auto,*" RowDefinitions="Auto,Auto">
                            <TextBlock Grid.Row="0" Classes="label" Text="Max Clients:"/>
                            <NumericUpDown Grid.Row="0" Grid.Column="1" Value="{Binding MaxClients}" Minimum="1" Maximum="100" Margin="0,0,0,10"/>
                            <TextBlock Grid.Row="1" Classes="label" Text="Service Name:"/>
                            <TextBox Grid.Row="1" Grid.Column="1" Text="{Binding ServiceName}"/>
                        </Grid>
                    </Expander>
                </StackPanel>

                <!-- Client -->
                <StackPanel IsVisible="{Binding IsClientSection}">
                    <TextBlock Classes="section" Text="Client"/>
                    <Grid ColumnDefinitions="Auto,*" RowDefinitions="Auto,Auto,Auto,Auto,Auto">
                        <TextBlock Grid.Row="0" Classes="label" Text="Server Address:"/>
                        <TextBox Grid.Row="0" Grid.Column="1" Text="{Binding ServerAddress}"
                                 PlaceholderText="192.168.1.100 or hostname" Margin="0,0,0,10"/>

                        <TextBlock Grid.Row="1" Classes="label" Text="Server Port:"/>
                        <NumericUpDown Grid.Row="1" Grid.Column="1" Value="{Binding ClientServerPort}" Minimum="1" Maximum="65535" Margin="0,0,0,10"/>

                        <TextBlock Grid.Row="2" Classes="label" Text="Cache Directory:"/>
                        <Grid Grid.Row="2" Grid.Column="1" ColumnDefinitions="*,Auto" Margin="0,0,0,10">
                            <TextBox Text="{Binding CacheDirectory}" Margin="0,0,5,0"/>
                            <Button Grid.Column="1" Content="Browse" Command="{Binding BrowseCacheDirectoryCommand}"/>
                        </Grid>

                        <CheckBox Grid.Row="3" Grid.ColumnSpan="2" Content="Auto-connect on startup"
                                  IsChecked="{Binding AutoConnect}" Margin="0,0,0,5" Foreground="White"/>
                        <CheckBox Grid.Row="4" Grid.ColumnSpan="2" Content="Prefer auto-discovery over manual address"
                                  IsChecked="{Binding PreferAutoDiscovery}" Foreground="White"/>
                    </Grid>
                    <Expander Header="Advanced">
                        <Grid ColumnDefinitions="Auto,*" RowDefinitions="Auto,Auto">
                            <TextBlock Grid.Row="0" Classes="label" Text="Heartbeat Interval (s):"/>
                            <NumericUpDown Grid.Row="0" Grid.Column="1" Value="{Binding HeartbeatIntervalSeconds}" Minimum="1" Maximum="60" Margin="0,0,0,10"/>
                            <TextBlock Grid.Row="1" Classes="label" Text="Max Cache Size (MB):"/>
                            <NumericUpDown Grid.Row="1" Grid.Column="1" Value="{Binding MaxCacheSizeMB}" Minimum="100" Maximum="102400" Increment="512"/>
                        </Grid>
                    </Expander>
                </StackPanel>

                <!-- Wallpaper -->
                <StackPanel IsVisible="{Binding IsWallpaperSection}">
                    <TextBlock Classes="section" Text="Wallpaper"/>
                    <CheckBox Content="Pause wallpaper when a fullscreen app is detected (games, video players)"
                              IsChecked="{Binding PauseOnFullscreen}" Foreground="White"/>
                </StackPanel>

                <!-- Logging -->
                <StackPanel IsVisible="{Binding IsLoggingSection}" Spacing="6">
                    <TextBlock Classes="section" Text="Logging"/>

                    <TextBlock Text="Global Level:" Foreground="#CCCCCC"/>
                    <WrapPanel>
                        <RadioButton Content="Information" GroupName="LogLevel" IsChecked="{Binding LogLevelInfo}" Foreground="White" Margin="0,0,15,0"/>
                        <RadioButton Content="Debug" GroupName="LogLevel" IsChecked="{Binding LogLevelDebug}" Foreground="White" Margin="0,0,15,0"/>
                        <RadioButton Content="Warning" GroupName="LogLevel" IsChecked="{Binding LogLevelWarning}" Foreground="White" Margin="0,0,15,0"/>
                        <RadioButton Content="Error" GroupName="LogLevel" IsChecked="{Binding LogLevelError}" Foreground="White"/>
                    </WrapPanel>

                    <CheckBox Content="Log to file  (rolling daily log in Logs directory)"
                              IsChecked="{Binding LogToFile}" Foreground="White" Margin="0,8,0,0"/>
                    <StackPanel IsVisible="{Binding LogToFile}">
                        <TextBlock Text="Log Directory:" Foreground="#CCCCCC" Margin="0,0,0,3"/>
                        <TextBox Text="{Binding LogDirectory}"/>
                    </StackPanel>

                    <TextBlock Text="Component Filters:" Foreground="#CCCCCC" Margin="0,8,0,0"/>
                    <CheckBox Content="UI Layer  (ViewModels, TrayViewModel)" IsChecked="{Binding LogUI}" Foreground="White"/>
                    <CheckBox Content="D2D Player  (WaBiBaBuSy.Player.D2D)" IsChecked="{Binding LogD2DPlayer}" Foreground="White"/>
                    <CheckBox Content="Composition System  (CompositionRenderer, AnimationLayer)" IsChecked="{Binding LogComposition}" Foreground="White"/>
                    <CheckBox Content="Renderers  (Video / GIF / Image renderers)" IsChecked="{Binding LogRenderers}" Foreground="White"/>
                    <CheckBox Content="Networking  (gRPC, WallpaperSyncService, mDNS)" IsChecked="{Binding LogNetworking}" Foreground="White"/>
                    <CheckBox Content="Animation System  (Distributor, Timing, Orchestrator)" IsChecked="{Binding LogAnimation}" Foreground="White"/>
                    <CheckBox Content="File Transfer  (UpdateDownloader, UpdateVerifier)" IsChecked="{Binding LogFileTransfer}" Foreground="White"/>

                    <Expander Header="Advanced">
                        <StackPanel Spacing="6">
                            <CheckBox Content="Performance metrics  (CPU, RAM, FPS – Debug level)"
                                      IsChecked="{Binding LogPerformanceMetrics}" Foreground="White"/>
                            <CheckBox Content="Frame-by-frame logging  (very verbose!)"
                                      IsChecked="{Binding LogFrameByFrame}" Foreground="White"/>
                        </StackPanel>
                    </Expander>
                </StackPanel>
            </Panel>
        </ScrollViewer>

        <!-- Footer -->
        <Grid Grid.Row="1" Grid.ColumnSpan="2" ColumnDefinitions="*,Auto,Auto" Margin="20,12,20,16">
            <TextBlock Grid.Column="0" Text="{Binding SaveError}" IsVisible="{Binding SaveError, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"
                       Foreground="#F0A030" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"
                       ToolTip.Tip="{Binding SaveError}"/>
            <Button Grid.Column="1" Content="Save" Command="{Binding SaveCommand}" Width="100" Margin="0,0,10,0" IsDefault="True"/>
            <Button Grid.Column="2" Content="Cancel" Command="{Binding CancelCommand}" Width="100" IsCancel="True"/>
        </Grid>
    </Grid>
</Window>
```

Notes for the implementer:
- `PlaceholderText` replaces the obsolete `Watermark` (AVLN5001 on Avalonia 12). If the build shows it is not available, keep `Watermark`.
- `SelectedSectionIndex` defaults to 0, so Server is selected on open — verify in manual check 1.

- [ ] **Step 5: Build + tests**

Run: `dotnet build WaBiBaBuSy.sln` then `dotnet test WaBiBaBuSy.Tests`
Expected: 0 errors, no new warnings in `SettingsWindow.axaml`; all tests green.

- [ ] **Step 6: Manual check (launch `dotnet run --project WaBiBaBuSy.UI`)**

1. `⋯` → Settings opens centered with the sidebar; Server is selected; each sidebar item shows only its section.
2. Advanced expanders are collapsed; Server shows Max Clients + Service Name inside, Client shows Heartbeat + Max Cache Size inside.
3. Browse (Server content dir, Client cache dir) opens a folder picker and fills the text box; cancelling the picker leaves the value.
4. With Settings open, open it again from the tray and from `⋯` → no second window; the existing one comes to the front.
5. Change Server Address, press Cancel, reopen → old value. Change it, Save, reopen → new value.
6. Save failure: set `%APPDATA%\WaBiBaBuSy\client-config.json` read-only, change a field, Save → amber error line in the footer, window stays open. Remove read-only afterwards.
7. Note `clientId` in `client-config.json` before, Save, check after → unchanged.
8. Logging: Log Directory box only visible when "Log to file" is checked; Enter = Save, Esc = Cancel.

Screenshot the four sections for the review.

- [ ] **Step 7: Commit**

```bash
git add WaBiBaBuSy.UI
git commit -m "feat: Settings dialog with sidebar sections and Advanced expanders"
```

---

### Task 3: Docs, graph

**Files:**
- Modify: `.docs/plans/2026-09-24-ui-redesign-design.md` (status line)
- Modify: `.docs/UI_ARCHITECTURE.md` (Settings section)
- Modify: `.docs/RECENT_UPDATES.md`
- Modify: `CLAUDE.md` (Documentation Map line for the UI redesign)

- [ ] **Step 1: Update docs**

- Spec status line → `**Status:** Plans 1–3 (rollout steps 1–5) implemented 2026-09-27; GUI verification pending`.
- `CLAUDE.md` Documentation Map entry → "room-first main window; Plans 1–3 implemented (toolbar + RoomView, docked Scene editor + Playlist tab, Settings sidebar)".
- `UI_ARCHITECTURE.md`: describe the Settings window as sidebar (Server · Client · Wallpaper · Logging), Advanced expanders, single instance via `SettingsWindow.ShowSingle()`, Save through `ConfigurationManager.Update*Configuration` (read-modify-write, keeps `ClientId` and update settings). Replace any text describing the old single-scroll layout (`grep -n "Settings" .docs/UI_ARCHITECTURE.md`).
- `RECENT_UPDATES.md`: new top entry dated 2026-09-27 — Settings sidebar; fix: Settings Save no longer resets `ClientId`, `ServiceType`, update settings and log-file limits; Browse buttons open a folder picker; one Settings window at a time.

- [ ] **Step 2: Refresh graph**

Run: `pwsh ./tools/graphify-update.ps1`

- [ ] **Step 3: Commit**

```bash
git add .docs CLAUDE.md graphify-out/GRAPH_REPORT.md
git commit -m "docs: record the Settings sidebar (UI redesign Plan 3)"
```
