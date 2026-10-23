using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WaBiBaBuSy.Core.Services.Logging;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Models.Wallpaper.Suggest;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>Playlist tab wiring (UI redesign Plan 2): the tab edits playlists, the Scene editor edits their items.</summary>
public partial class MainWindowViewModel
{
    /// <summary>The Playlist tab's view model. Not named "Playlist": that would shadow the model type in this class.</summary>
    public PlaylistViewModel PlaylistEditor { get; } = new();

    /// <summary>Called once at the end of the constructor, after <see cref="InitSceneEditor"/>.</summary>
    private void InitPlaylist()
    {
        PlaylistEditor.StartShow = StartPlaylist;
        PlaylistEditor.StopShow = StopPlaylistAsync;
        PlaylistEditor.CaptureDraft = () => SceneEditor.BuildConfig();
        PlaylistEditor.LoadIntoEditor = LoadDraft;
        PlaylistEditor.BuildFromFolder = AddPlaylistFromFolderAsync;
        PlaylistEditor.ThumbnailProvider = PlaylistThumbnailAsync;
        PlaylistEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistViewModel.HasLoadedItem))
                UpdateLoadedPlaylistItemCommand.NotifyCanExecuteChanged();
            else if (e.PropertyName == nameof(PlaylistViewModel.IsShowRunning))
                OnPropertyChanged(nameof(IsAnythingPlaying));
        };
        if (_server.ShowResumePoint is { } show)
        {
            // A show that kept running while the window was closed: the tab shows its playlist, running, on
            // its current row. A copy, so editing rows never touches the running show.
            var running = JsonSerializer.Deserialize<WaBiBaBuSy.Models.Wallpaper.Playlist>(JsonSerializer.Serialize(show.Playlist))!;
            PlaylistEditor.RefreshSavedPlaylists(select: running.Name);
            PlaylistEditor.LoadFrom(running);
            OnShowChanged();
        }
        else
        {
            _ = PlaylistEditor.InitializeAsync();
        }
    }

    private const int PlaylistThumbnailWidth = 192;

    /// <summary>Playlist row thumbnail: the gallery's, else a video frame (cached generator), else the file decoded small.</summary>
    private async Task<Bitmap?> PlaylistThumbnailAsync(string path)
    {
        var gallery = Wallpapers.FirstOrDefault(w => string.Equals(w.FilePath, path, StringComparison.OrdinalIgnoreCase))?.Thumbnail;
        if (gallery != null) return gallery;
        if (!File.Exists(path)) return null;
        if (SceneChecks.IsVideo(path))
            return await _thumbnailGenerator.GenerateThumbnail(path) is { } jpg ? new Bitmap(jpg) : null;
        return await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, PlaylistThumbnailWidth);
        });
    }

    /// <summary>True while a show or a manual scene is running (shows the toolbar ■).</summary>
    public bool IsAnythingPlaying => IsCrossScreenRunning || PlaylistEditor.IsShowRunning;

    /// <summary>Toolbar ⏭: advance the running show to its next item.</summary>
    [RelayCommand]
    private void SkipShowItem() => _server.SkipShowItem();

    /// <summary>Editor footer: append the draft to the playlist as a new item.</summary>
    [RelayCommand]
    private void SaveSceneAsNewItem() => PlaylistEditor.AddItem(SceneEditor.BuildConfig());

    /// <summary>Editor footer: write the draft into the loaded playlist item.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateLoadedPlaylistItem))]
    private void UpdateLoadedPlaylistItem() => PlaylistEditor.UpdateLoadedItem(SceneEditor.BuildConfig());

    private bool CanUpdateLoadedPlaylistItem() => PlaylistEditor.HasLoadedItem;

    [RelayCommand]
    private void OpenPlaylistTab()
    {
        IsRightPanelOpen = true;
        RightPanelTabIndex = 1;
    }

    private const int MaxFolderFiles = 200;

    /// <summary>
    /// Auto from folder (spec 2026-10-14 folder-playlist §2): pick a folder, add its media to the gallery, profile it,
    /// build a party playlist and append its items to the open playlist. Never throws; the status line says what happened.
    /// </summary>
    private async Task AddPlaylistFromFolderAsync()
    {
        try
        {
            if (_storageProvider == null) return;
            var folders = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Folder with pictures, GIFs or videos",
                AllowMultiple = false,
            });
            var folder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(folder)) return;
            string folderName = Path.GetFileName(folder.TrimEnd('\\', '/'));

            var all = Directory.EnumerateFiles(folder).Where(SceneChecks.IsMediaFile)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
            if (all.Count == 0)
            {
                PlaylistEditor.FolderStatus = $"No pictures or videos in {folderName}";
                return;
            }
            var files = all.Take(MaxFolderFiles).ToList();
            // Gallery adds run on the UI thread (header decode, thumbnail, gallery save each): say so, and let the
            // window repaint between batches instead of freezing silently on a big folder.
            for (int i = 0; i < files.Count; i++)
            {
                if (i % 10 == 0)
                {
                    PlaylistEditor.FolderStatus = $"Adding to gallery {i} / {files.Count}…";
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                }
                AddWallpaperFromPath(files[i]);
            }

            var screen = PrimaryScreen();
            var progress = new Progress<int>(n => PlaylistEditor.FolderStatus = $"Analysing {n} / {files.Count}…");
            PlaylistEditor.FolderStatus = $"Analysing 0 / {files.Count}…";
            var profiles = await ProfileFilesAsync(files, screen.Width, screen.Height, progress);

            var built = PlaylistBuilder.Build(folderName, profiles, screen.Width, screen.Height, screen.PixelsPerCm,
                SceneEditor.BuildConfig(), PlaylistEditor.DefaultDurationMs);
            int added = PlaylistEditor.AppendItems(built.Name, built.Items);
            double minutes = built.Items.Sum(i => i.GetEffectiveDurationMs(PlaylistEditor.DefaultDurationMs)) / 60_000.0;
            PlaylistEditor.FolderStatus = $"Added {added} items (≈ {Math.Round(minutes)} min) from {folderName}"
                + (all.Count > files.Count ? $" — first {files.Count} of {all.Count} files" : string.Empty);
        }
        catch (Exception ex)
        {
            AppLogger.CreateLogger<MainWindowViewModel>().LogWarning(ex, "Auto from folder failed");
            PlaylistEditor.FolderStatus = "Could not build from that folder: " + ex.Message;
        }
    }
}
