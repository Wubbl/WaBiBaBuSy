using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Common;
using WaBiBaBuSy.Common.Version;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.Core.Interfaces;
using WaBiBaBuSy.Core.Services;
using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.Core.Services.Testing;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.UI.Services;
using WaBiBaBuSy.UI.Services.Testing;
using WaBiBaBuSy.UI.Views;
using WaBiBaBuSy.WallpaperEngine.Composition;
using WaBiBaBuSy.WallpaperEngine.Native;
using WaBiBaBuSy.WallpaperEngine.Renderers;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// ViewModel for the system tray icon
/// </summary>
public partial class TrayViewModel : ObservableObject
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly WaBiBaBuSyService _service;
    private readonly GlobalHotkeyService _hotkeys = new();
    private readonly DesktopWindowManager _desktopManager;
    private readonly TestRunCoordinator _testCoordinator;
    // What the menu, the CLI and the control API start runs through, so the control panel shows each run.
    private readonly ObservedTestRunControl _testControl;
    private MainWindow? _mainWindow;
    // Read from Kestrel threads (control API): must not touch Avalonia objects such as _mainWindow.DataContext.
    private volatile MainWindowViewModel? _mainViewModel;
    // Client mode: renders what the server sends, for every connect path, with or without the main window
    private readonly RemotePlaybackHost _remotePlayback;
    // Server / standalone mode: this machine's players, the running scene and the show, with or without the main window
    private readonly ServerPlaybackHost _serverPlayback;
    // Scene presets (editor, playlist, Quick start); seeded with the starter presets on first run
    private readonly PresetStore _presets = new();
    private NativeMenuItem? _quickStartItem;
    private NativeMenuItem? _agendaItem;
    // Why the last Quick start did not play; shown at the top of the submenu until a start succeeds
    private string? _quickStartProblem;

    [ObservableProperty]
    private bool _isClientConnected;

    /// <summary>The app's preset store, shared with every control panel.</summary>
    public PresetStore Presets => _presets;

    public string VersionText => $"v{VersionInfo.AppVersion} (build {VersionInfo.BuildNumber})";

    public TrayViewModel(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;

        // Load configuration from file
        var serverConfig = ConfigurationManager.LoadServerConfiguration();
        var clientConfig = ConfigurationManager.LoadClientConfiguration();

        Console.WriteLine($"Loaded server config from: {ConfigurationManager.GetServerConfigPath()}");
        Console.WriteLine($"Loaded client config from: {ConfigurationManager.GetClientConfigPath()}");

        var logger = AppLogger.CreateLogger<WaBiBaBuSyService>();

        // Create desktop window manager (will be used for wallpaper restoration on exit)
        _desktopManager = new DesktopWindowManager(AppLogger.CreateLogger<DesktopWindowManager>());

        // Create renderer factory for wallpaper playback
        var rendererFactory = CreateRendererFactory(_desktopManager);

        _service = new WaBiBaBuSyService(logger, serverConfig, clientConfig, rendererFactory);

        // Players left behind by a crashed or killed prior session would keep rendering stale content.
        int orphans = PlayerProcesses.Kill(priorSessionOnly: true);
        if (orphans > 0)
            Console.WriteLine($"[Startup] Killed {orphans} orphan player process(es) from a prior session");

        // Registered before any connect so the first command after registering already renders synchronized.
        _remotePlayback = new RemotePlaybackHost(_service, _desktopManager);
        _remotePlayback.Register();
        _serverPlayback = new ServerPlaybackHost(_service, _desktopManager, _remotePlayback);

        // Ctrl+Alt+1..5 fire show events — registered only while this machine runs the server.
        _hotkeys.Pressed += slot => Dispatcher.UIThread.Post(() => _ = OnEventHotkeyAsync(slot));
        _service.ServerStatusChanged += (_, e) => Dispatcher.UIThread.Post(() => UpdateHotkeys(e.IsRunning));
        // The Events strip's Hotkeys toggle (Ctrl+Alt+digit is AltGr+digit on German keyboards) takes effect at once.
        _serverPlayback.EventSettingsChanged += () => Dispatcher.UIThread.Post(() => UpdateHotkeys(_service.IsServerRunning));
        UpdateHotkeys(_service.IsServerRunning);

        // Subscribe to service events
        _service.ClientConnectionStatusChanged += OnClientConnectionStatusChanged;

        // Subscribe to application exit event to restore desktop
        _desktop.Exit += OnApplicationExit;

        // A run starts only with the control panel open (its room is the node list); once started it keeps
        // its host when the window closes.
        _testCoordinator = new TestRunCoordinator(
            () => _mainViewModel != null ? new TestHostAdapter(_serverPlayback) : null,
            () => _service.ServerSyncService is { } sync ? new ServerTestChannel(sync) : null,
            AppLogger.Factory);
        // StartAsync may come from a Kestrel thread (control API): hop to the UI thread to show the run.
        _testControl = new ObservedTestRunControl(_testCoordinator,
            run => Dispatcher.UIThread.Post(() => _ = _mainViewModel?.TrackTestRunAsync(run)));
        _service.TestRunControl = _testControl;

        _presets.Changed += (_, _) => Dispatcher.UIThread.Post(RebuildQuickStartMenu);
        _ = SeedPresetsAsync();

        // The test-mode menu reads the setting once; refresh it when Settings saves.
        SettingsViewModel.AnySettingsSaved += (_, _) => _mainViewModel?.RefreshTestModeEnabled();
    }

    /// <summary>
    /// Creates a renderer factory that instantiates appropriate renderers based on file type and monitor index
    /// </summary>
    private Func<string, int, IWallpaperRenderer?> CreateRendererFactory(DesktopWindowManager desktopManager)
    {
        return (filePath, monitorIndex) =>
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            // Note: monitorIndex will be used when WallpaperConfig is passed to InitializeAsync
            return extension switch
            {
                // GIFs now use LibVLC (VideoWallpaperRenderer) for instant loading
                ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" or ".flv" or ".gif" =>
                    new VideoWallpaperRenderer(
                        AppLogger.CreateLogger<VideoWallpaperRenderer>(),
                        desktopManager),

                ".jpg" or ".jpeg" or ".png" or ".bmp" =>
                    new ImageWallpaperRendererLibVLC(
                        AppLogger.CreateLogger<ImageWallpaperRendererLibVLC>(),
                        desktopManager),

                _ => null
            };
        };
    }

    [RelayCommand]
    private void ShowWindow()
    {
        if (_mainWindow == null || !_mainWindow.IsVisible)
        {
            var viewModel = new MainWindowViewModel(_service, _serverPlayback, _presets) { TestCoordinator = _testControl };
            // A run started while the window was closed (control API) still shows its Cancel button.
            if (_testControl.ActiveRun is { } activeRun) _ = viewModel.TrackTestRunAsync(activeRun);
            var window = new MainWindow { DataContext = viewModel };
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_mainViewModel, viewModel)) _mainViewModel = null;
            };
            _mainWindow = window;
            _mainViewModel = viewModel;
            _mainWindow.Show();
            // Bring it to the front so the first click (e.g. on the splitter) is not spent activating the window
            _mainWindow.Activate();
        }
        else
        {
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        var logPath = PathHelper.GetLogsPath();
        Process.Start(new ProcessStartInfo
        {
            FileName = logPath,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void Connect()
    {
        // Render delegates are registered once (RemotePlaybackHost), so this connects like the main window does.
        // When mDNS finds nothing (switches / VLANs often block it), fall back to the server saved in Settings,
        // not localhost (review 2026-10-13 E15).
        var saved = ConfigurationManager.LoadClientConfiguration();
        var address = string.IsNullOrWhiteSpace(saved.ServerAddress) ? "localhost" : saved.ServerAddress;
        var port = saved.ServerPort > 0 ? saved.ServerPort : 50051;
        _ = _service.DiscoverAndConnectAsync(address, port);
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        try
        {
            await _service.DisconnectFromServerAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error disconnecting: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Settings()
    {
        SettingsWindow.ShowSingle();
    }

    [RelayCommand]
    private Task Exit() => ExitAsync(0);

    private void UpdateHotkeys(bool serverRunning)
    {
        if (serverRunning && _serverPlayback.EventSettings.HotkeysEnabled)
        {
            _hotkeys.Start();
            if (_hotkeys.FailedSlots.Count > 0)
                _serverPlayback.EventStatusText = $"Hotkey(s) taken by another app: {string.Join(", ", _hotkeys.FailedSlots.Select(ShowEventHotkeys.Gesture))}";
        }
        else
        {
            _hotkeys.Stop();
        }
    }

    private async Task OnEventHotkeyAsync(int slot)
    {
        if (slot == ShowEventHotkeys.BiBaBoSlot)
        {
            try { await _serverPlayback.ToggleBiBaBoAsync(null); }   // everyone (the hotkey has no target picker)
            catch (Exception ex)
            {
                AppLogger.CreateLogger<TrayViewModel>().LogError(ex, "BiBaBo hotkey failed");
                _serverPlayback.EventStatusText = $"BiBaBo failed: {ex.Message}";
            }
            return;
        }
        if (ShowEventHotkeys.TypeForSlot(slot) is not { } type) return;
        try
        {
            await _serverPlayback.FireEventAsync(type, _serverPlayback.EventSettings.LastTargetSeatId);
        }
        catch (Exception ex)
        {
            AppLogger.CreateLogger<TrayViewModel>().LogError(ex, "Show event hotkey {Type} failed", type);
            _serverPlayback.EventStatusText = $"{type} failed: {ex.Message}";
        }
    }

    private bool _exiting;

    private async Task ExitAsync(int exitCode)
    {
        if (_exiting) return;   // a second Exit click would run the whole shutdown twice
        _exiting = true;
        _hotkeys.Dispose();

        // An active test run: let it write the partial report and switch test mode off on every node
        // before the players and the server go away.
        if (_testCoordinator.Cancel() && !await _testCoordinator.WaitForIdleAsync(TimeSpan.FromSeconds(10)))
            Console.WriteLine("[Test] The test run did not finish within 10 s of cancelling; exiting anyway");

        // Server / standalone mode: stop the show and this machine's players
        try { await _serverPlayback.ShutdownAsync(); }
        catch (Exception ex) { Console.WriteLine($"[Exit] Stopping local players failed: {ex.Message}"); }

        // Client mode: stop the server-driven players
        try { await _remotePlayback.StopAllAsync(); }
        catch (Exception ex) { Console.WriteLine($"[Exit] Stopping remote players failed: {ex.Message}"); }

        // Cleanup: Stop server/client if running. Guarded like the steps above: a throw here used to skip
        // Shutdown, so Exit did nothing and --exit never exited (review 2026-10-13, part 6).
        try { await _service.StopServerAsync(); }
        catch (Exception ex) { Console.WriteLine($"[Exit] Stopping the server failed: {ex.Message}"); }
        try { await _service.DisconnectFromServerAsync(); }
        catch (Exception ex) { Console.WriteLine($"[Exit] Disconnecting failed: {ex.Message}"); }

        // Close main window if open
        try { _mainWindow?.Close(); }
        catch (Exception ex) { Console.WriteLine($"[Exit] Closing the window failed: {ex.Message}"); }

        // Shutdown the application
        _desktop.Shutdown(exitCode);
    }

    /// <summary>
    /// --test-run: open the control panel, start the server, wait for the scenario's remote nodes
    /// (120 s), run, print the verdict; with --exit quit with <see cref="TestRunReport.ExitCode"/> (0 pass, 1 failed or a node unaccounted for, 2 aborted).
    /// </summary>
    public async Task RunTestFromCommandLineAsync(string scenarioPath, bool exitWhenDone)
    {
        int exitCode = 2;
        try
        {
            var scenario = ScenarioLoader.Load(scenarioPath);
            ShowWindow();
            if (!_service.IsServerRunning) await _service.StartServerAsync();
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (true)
            {
                var nodes = await _serverPlayback.TestNodesAsync();
                int remotes = nodes.Count(n => !n.IsLocal && n.Connected);
                if (remotes >= scenario.Requires.MinRemoteNodes && nodes.Any(n => n.IsLocal)) break;
                if (DateTime.UtcNow > deadline)
                    throw new InvalidOperationException($"only {remotes} of {scenario.Requires.MinRemoteNodes} remote node(s) connected after 120 s");
                await Task.Delay(1000);
            }

            var report = await _testControl.StartAsync(scenarioPath);
            (exitCode, var reason) = report.ExitCode();
            CliConsole.WriteLine($"Test run {report.Verdict.ToString().ToLowerInvariant()}{(reason != null ? $" (exit {exitCode}: {reason})" : "")}: {Path.Combine(report.ResultsDirectory, "report.html")}");
        }
        catch (Exception ex)
        {
            CliConsole.WriteLine($"Test run could not run: {ex.Message}", error: true);
        }
        if (exitWhenDone) await ExitAsync(exitCode);
    }

    private void OnClientConnectionStatusChanged(object? sender, Core.Services.Networking.ConnectionStatusChangedEventArgs e)
    {
        IsClientConnected = e.IsConnected;
    }

    // ── Presets / Quick start ─────────────────────────────────────────────

    /// <summary>First run: the starter presets (shipped showcase scenes) into an empty preset folder.</summary>
    private async Task SeedPresetsAsync()
    {
        var log = AppLogger.CreateLogger<TrayViewModel>();
        try
        {
            var starterDir = Path.Combine(AppContext.BaseDirectory, "TestScenarios", "showcase");
            int seeded = await PresetSeeder.SeedIfEmptyAsync(_presets, starterDir, log);
            if (seeded > 0) log.LogInformation("Seeded {Count} starter preset(s) into {Dir}", seeded, _presets.Directory);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Seeding the starter presets failed");
        }
    }

    /// <summary>Insert "Quick start ▸" below "Open Control Panel" in the tray menu (NativeMenu has no ItemsSource).</summary>
    public void AttachTrayMenu(NativeMenu? menu)
    {
        if (menu == null) return;
        _quickStartItem = new NativeMenuItem("Quick start") { Menu = new NativeMenu() };
        menu.Items.Insert(Math.Min(1, menu.Items.Count), _quickStartItem);
        _agendaItem = new NativeMenuItem("Agenda on all screens") { ToggleType = MenuItemToggleType.CheckBox };
        _agendaItem.Click += (_, _) => _ = _serverPlayback.SetAgendaEnabledAsync(!_serverPlayback.AgendaDocument.Enabled);
        menu.Items.Insert(Math.Min(2, menu.Items.Count), _agendaItem);
        _serverPlayback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerPlaybackHost.CurrentAgenda)) Dispatcher.UIThread.Post(RebuildQuickStartMenu);
        };
        RebuildQuickStartMenu();
    }

    /// <summary>One entry per preset; hidden while connected to another server as a client.</summary>
    private void RebuildQuickStartMenu()
    {
        if (_quickStartItem?.Menu is not { } submenu) return;
        submenu.Items.Clear();
        if (_quickStartProblem != null)
        {
            submenu.Items.Add(new NativeMenuItem($"⚠ {_quickStartProblem}") { IsEnabled = false });
            submenu.Items.Add(new NativeMenuItemSeparator());
        }
        var names = _presets.List();
        foreach (var name in names)
        {
            var item = new NativeMenuItem(name);
            item.Click += (_, _) => _ = QuickStartAsync(name);
            submenu.Items.Add(item);
        }
        _quickStartItem.IsEnabled = names.Count > 0;
        _quickStartItem.IsVisible = !IsClientConnected;
        if (_agendaItem != null)
        {
            _agendaItem.IsChecked = _serverPlayback.AgendaDocument.Enabled;
            _agendaItem.IsVisible = !IsClientConnected;   // a client shows the server's agenda
        }
    }

    partial void OnIsClientConnectedChanged(bool value)
    {
        Dispatcher.UIThread.Post(RebuildQuickStartMenu);
        _ = _serverPlayback.SetClientModeAsync(value);   // a client shows its server's agenda, not this machine's own
    }

    /// <summary>
    /// Play a preset on every seat through the app-lifetime host (no window needed). A failure is logged and shown
    /// at the top of the Quick start submenu (the tray has no other place to say it); a start clears it.
    /// </summary>
    private async Task QuickStartAsync(string name)
    {
        var log = AppLogger.CreateLogger<TrayViewModel>();
        if (_service.IsClientConnected) return;
        string? problem;
        try
        {
            var config = await _presets.LoadAsync(name);
            problem = await _serverPlayback.QuickStartAsync(config)
                ? null
                : "did not start (another start was in flight, or no monitor / client to play on)";
        }
        catch (Exception ex)
        {
            // Everything: an exception escaping this fire-and-forget click would go unobserved.
            log.LogWarning(ex, "Quick start '{Preset}' failed", name);
            problem = ex.Message;
        }
        if (problem != null) log.LogWarning("Quick start '{Preset}': {Problem}", name, problem);
        _quickStartProblem = problem == null ? null : $"{name}: {Shorten(problem)}";
        RebuildQuickStartMenu();
    }

    /// <summary>Menu-sized text: one line, at most 90 characters.</summary>
    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= 90 ? line : line[..87] + "…";
    }

    /// <summary>
    /// Called when the application is exiting. Restores the Windows desktop wallpaper.
    /// </summary>
    private void OnApplicationExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        Console.WriteLine("Application exiting, restoring desktop wallpaper...");

        try
        {
            // Restore the desktop to its original state
            _desktopManager.RestoreDesktop();

            // Dispose the service to clean up resources
            _service.Dispose();

            Console.WriteLine("Desktop wallpaper restored successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error restoring desktop on exit: {ex.Message}");
        }
    }
}
