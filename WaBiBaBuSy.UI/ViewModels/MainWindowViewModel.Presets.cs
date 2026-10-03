using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Core.Services.Animation;
using WaBiBaBuSy.Models.Testing;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// Scene presets in the docked editor (Presets ▾, Save as preset…, Export…, Import…) and the Playlist tab's
/// "＋ Add ▾ → From preset". The store is the tray's, so the tray's Quick start menu follows every change.
/// </summary>
public partial class MainWindowViewModel
{
    private static readonly FilePickerFileType SceneJson = new("Scene / preset (JSON)") { Patterns = new[] { "*.json" } };

    /// <summary>Editor "Presets ▾": one entry per preset, loads it into the editor.</summary>
    public ObservableCollection<PresetMenuEntry> ScenePresets { get; } = new();

    [ObservableProperty] private bool _hasPresets;

    /// <summary>Name typed in "Save as preset…" (pre-filled with the last loaded preset).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SavePresetButtonText))]
    private string _presetNameDraft = string.Empty;

    /// <summary>Result of the last preset action, shown under the footer buttons.</summary>
    [ObservableProperty] private string _presetStatus = string.Empty;

    /// <summary>"Replace preset" when the typed name exists — the overwrite warning.</summary>
    public string SavePresetButtonText =>
        PresetNameDraft.Trim().Length > 0 && _presets.Exists(PresetNameDraft.Trim()) ? "Replace preset" : "Save preset";

    /// <summary>Called once from the constructor.</summary>
    private void InitPresets()
    {
        _presets.Changed += OnPresetsChanged;
        RefreshPresetMenus();
    }

    private void OnPresetsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_detached) RefreshPresetMenus();
    });

    private void RefreshPresetMenus()
    {
        var names = _presets.List();
        ScenePresets.Clear();
        foreach (var name in names)
            ScenePresets.Add(new PresetMenuEntry(name, new AsyncRelayCommand(() => LoadPresetAsync(name))));
        HasPresets = names.Count > 0;
        PlaylistEditor.SetPresetEntries(names.Select(name =>
            new PresetMenuEntry(name, new AsyncRelayCommand(() => AddPresetToPlaylistAsync(name)))));
        OnPropertyChanged(nameof(SavePresetButtonText));
    }

    /// <summary>Load a preset as the draft. The room selection (targets) is left as it is.</summary>
    private async Task LoadPresetAsync(string name)
    {
        try
        {
            SceneEditor.LoadFromConfig(await _presets.LoadAsync(name));
            PresetNameDraft = name;
            PresetStatus = $"Loaded preset “{name}”";
        }
        catch (Exception ex) when (ex is ScenarioException or IOException or UnauthorizedAccessException)
        {
            PresetStatus = $"Could not load “{name}”: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveAsPreset()
    {
        var name = PresetNameDraft.Trim();
        if (name.Length == 0)
        {
            PresetStatus = "Type a preset name first";
            return;
        }
        try
        {
            var saved = await _presets.SaveAsync(name, SceneEditor.BuildConfig());
            PresetNameDraft = saved;
            PresetStatus = $"Saved preset “{saved}”";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PresetStatus = $"Could not save “{name}”: {ex.Message}";
        }
    }

    /// <summary>Export the draft as a JSON file plus an assets\ folder next to it (share it with the club).</summary>
    [RelayCommand]
    private async Task ExportScene()
    {
        if (_storageProvider == null) return;
        var file = await _storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export scene",
            SuggestedFileName = (PresetNameDraft.Trim().Length > 0 ? PresetStore.Sanitize(PresetNameDraft) : "scene") + ".json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { SceneJson },
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        try
        {
            await PresetStore.ExportAsync(SceneEditor.BuildConfig(), path);
            PresetStatus = $"Exported {Path.GetFileName(path)} (+ assets\\ next to it)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PresetStatus = $"Export failed: {ex.Message}";
        }
    }

    /// <summary>Import a scene / exported preset (assets copied into the preset folder) and load it.</summary>
    [RelayCommand]
    private async Task ImportPreset()
    {
        if (_storageProvider == null) return;
        var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import scene or preset",
            AllowMultiple = false,
            FileTypeFilter = new[] { SceneJson },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        try
        {
            var name = await _presets.ImportAsync(path);
            await LoadPresetAsync(name);
            PresetStatus = $"Imported preset “{name}”";
        }
        catch (Exception ex) when (ex is ScenarioException or IOException or UnauthorizedAccessException)
        {
            PresetStatus = $"Import failed: {ex.Message}";
        }
    }

    /// <summary>Playlist "From preset": a copy of the preset as a new item, playing on all seats.</summary>
    private async Task AddPresetToPlaylistAsync(string name)
    {
        try
        {
            var config = await _presets.LoadAsync(name);
            config.SelectedMonitorIds = new();
            PlaylistEditor.AddItem(config, name);
        }
        catch (Exception ex) when (ex is ScenarioException or IOException or UnauthorizedAccessException)
        {
            PresetStatus = $"Could not add “{name}”: {ex.Message}";
        }
    }
}
