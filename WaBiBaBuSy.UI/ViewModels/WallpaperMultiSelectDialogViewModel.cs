using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// Represents a selectable wallpaper item in the multi-select gallery dialog
/// </summary>
public partial class WallpaperMultiSelectItem : ViewModelBase
{
    [ObservableProperty]
    private string _wallpaperId = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private WallpaperType _type;

    [ObservableProperty]
    private string _resolution = string.Empty;

    [ObservableProperty]
    private long _fileSizeBytes;

    [ObservableProperty]
    private bool _isSelected = false;

    /// <summary>
    /// Human-readable file size
    /// </summary>
    public string FileSize
    {
        get
        {
            if (FileSizeBytes < 1024)
                return $"{FileSizeBytes} B";
            if (FileSizeBytes < 1024 * 1024)
                return $"{FileSizeBytes / 1024.0:F1} KB";
            if (FileSizeBytes < 1024 * 1024 * 1024)
                return $"{FileSizeBytes / (1024.0 * 1024.0):F1} MB";
            return $"{FileSizeBytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        }
    }

    /// <summary>
    /// Type display name (Video, Image, GIF)
    /// </summary>
    public string TypeName => Type switch
    {
        WallpaperType.Video => "Video",
        WallpaperType.Image => "Image",
        WallpaperType.Gif => "GIF",
        _ => "Unknown"
    };
}

/// <summary>
/// Filter mode for gallery items
/// </summary>
public enum WallpaperFilterMode
{
    All,
    Animations,      // Videos and GIFs
    Backgrounds,     // Images
    Videos,
    Images,
    Gifs
}

/// <summary>
/// ViewModel for wallpaper multi-select dialog
/// </summary>
public partial class WallpaperMultiSelectDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<WallpaperMultiSelectItem> _allWallpapers = new();

    [ObservableProperty]
    private ObservableCollection<WallpaperMultiSelectItem> _filteredWallpapers = new();

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private int _selectedFilterMode = 0; // All

    [ObservableProperty]
    private int _selectedSortMode = 0; // Name

    [ObservableProperty]
    private bool _selectAll = false;

    [ObservableProperty]
    private string _selectionSummary = "0 selected";

    public bool DialogResult { get; private set; }
    private Action? _closeAction;

    /// <summary>
    /// Get the list of selected wallpapers
    /// </summary>
    public List<WallpaperMultiSelectItem> GetSelectedWallpapers()
    {
        return AllWallpapers
            .Where(w => w.IsSelected)
            .ToList();
    }

    public void SetCloseAction(Action closeAction)
    {
        _closeAction = closeAction;
    }

    /// <summary>
    /// Load wallpapers from the gallery
    /// </summary>
    public void LoadWallpapers(IEnumerable<WallpaperItemViewModel> wallpapers)
    {
        AllWallpapers.Clear();

        foreach (var wallpaper in wallpapers)
        {
            var item = new WallpaperMultiSelectItem
            {
                WallpaperId = wallpaper.WallpaperId,
                Name = wallpaper.Name,
                FilePath = wallpaper.FilePath,
                Type = wallpaper.Type,
                Resolution = wallpaper.Resolution,
                FileSizeBytes = wallpaper.FileSizeBytes,
                IsSelected = false
            };

            AllWallpapers.Add(item);
        }

        ApplyFilter();
    }

    /// <summary>
    /// Apply filter and search to wallpapers
    /// </summary>
    private void ApplyFilter()
    {
        var filtered = AllWallpapers.AsEnumerable();

        // Apply type filter
        var filterMode = (WallpaperFilterMode)SelectedFilterMode;
        filtered = filterMode switch
        {
            WallpaperFilterMode.Animations => filtered.Where(w => w.Type == WallpaperType.Video || w.Type == WallpaperType.Gif),
            WallpaperFilterMode.Backgrounds => filtered.Where(w => w.Type == WallpaperType.Image),
            WallpaperFilterMode.Videos => filtered.Where(w => w.Type == WallpaperType.Video),
            WallpaperFilterMode.Images => filtered.Where(w => w.Type == WallpaperType.Image),
            WallpaperFilterMode.Gifs => filtered.Where(w => w.Type == WallpaperType.Gif),
            _ => filtered
        };

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var query = SearchQuery.ToLower();
            filtered = filtered.Where(w =>
                w.Name.ToLower().Contains(query) ||
                w.FilePath.ToLower().Contains(query));
        }

        // Apply sort
        filtered = SelectedSortMode switch
        {
            0 => filtered.OrderBy(w => w.Name),        // Name ascending
            1 => filtered.OrderByDescending(w => w.Name), // Name descending
            2 => filtered.OrderByDescending(w => w.FileSizeBytes), // Size descending
            3 => filtered.OrderBy(w => w.Type),        // Type ascending
            _ => filtered
        };

        FilteredWallpapers.Clear();
        foreach (var item in filtered)
        {
            FilteredWallpapers.Add(item);
        }

        UpdateSelectionSummary();
    }

    /// <summary>
    /// Update the selection summary text
    /// </summary>
    private void UpdateSelectionSummary()
    {
        var selectedCount = AllWallpapers.Count(w => w.IsSelected);
        var totalSize = AllWallpapers
            .Where(w => w.IsSelected)
            .Sum(w => w.FileSizeBytes);

        var sizeStr = FormatBytes(totalSize);
        SelectionSummary = selectedCount == 0
            ? "0 selected"
            : $"{selectedCount} selected ({sizeStr})";
    }

    /// <summary>
    /// Format bytes to human-readable size
    /// </summary>
    private string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
    }

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnSelectedFilterModeChanged(int value)
    {
        ApplyFilter();
    }

    partial void OnSelectedSortModeChanged(int value)
    {
        ApplyFilter();
    }

    partial void OnSelectAllChanged(bool value)
    {
        foreach (var wallpaper in AllWallpapers)
        {
            wallpaper.IsSelected = value;
        }
        UpdateSelectionSummary();
    }

    /// <summary>
    /// Handle when individual wallpaper selection changes
    /// </summary>
    public void OnWallpaperSelectionChanged()
    {
        UpdateSelectionSummary();

        // Update SelectAll checkbox state
        var allSelected = AllWallpapers.Count > 0 && AllWallpapers.All(w => w.IsSelected);
        SelectAll = allSelected;
    }

    /// <summary>
    /// Constructor - subscribe to wallpaper selection changes
    /// </summary>
    public WallpaperMultiSelectDialogViewModel()
    {
        AllWallpapers.CollectionChanged += (s, e) =>
        {
            // Subscribe to IsSelected property changes on each new item
            if (e.NewItems != null)
            {
                foreach (WallpaperMultiSelectItem item in e.NewItems)
                {
                    item.PropertyChanged += (s2, e2) =>
                    {
                        if (e2.PropertyName == nameof(WallpaperMultiSelectItem.IsSelected))
                        {
                            OnWallpaperSelectionChanged();
                        }
                    };
                }
            }
        };
    }

    [RelayCommand]
    private void Ok()
    {
        var selectedCount = AllWallpapers.Count(w => w.IsSelected);
        if (selectedCount == 0)
        {
            // TODO: Show validation error
            return;
        }

        DialogResult = true;
        _closeAction?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        _closeAction?.Invoke();
    }
}
