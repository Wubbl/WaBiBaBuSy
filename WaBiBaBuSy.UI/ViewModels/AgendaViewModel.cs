using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using WaBiBaBuSy.Models.Configuration;
using WaBiBaBuSy.Models.Show;
using WaBiBaBuSy.UI.Services;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>
/// The right panel's Agenda tab (design 2026-10-21-agenda-overlay-design.md §6): the timetable as typed lines, notes,
/// title, first day, corner. Every edit is published ~1 s after typing stops — no Apply button.
/// </summary>
public partial class AgendaViewModel : ObservableObject
{
    private readonly ServerPlaybackHost _server;
    private readonly DispatcherTimer _debounce;
    private bool _loading;

    public AgendaViewModel(ServerPlaybackHost server)
    {
        _server = server;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = PublishAsync(); };
        _server.PropertyChanged += OnServerPropertyChanged;
        Load(server.AgendaDocument);
    }

    public IReadOnlyList<AgendaCorner> Corners { get; } = Enum.GetValues<AgendaCorner>();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _itemsText = "";
    [ObservableProperty] private string _notesText = "";
    [ObservableProperty] private DateTimeOffset? _firstDay;
    [ObservableProperty] private AgendaCorner _corner;
    [ObservableProperty] private IReadOnlyList<string> _preview = Array.Empty<string>();
    [ObservableProperty] private string _statusText = "";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(Preview) or nameof(StatusText)) return;
        UpdatePreview();
        _debounce.Stop();
        _debounce.Start();
        StatusText = "…";
    }

    /// <summary>The tray toggled the agenda: show it here without publishing again.</summary>
    private void OnServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerPlaybackHost.CurrentAgenda) && _server.AgendaDocument.Enabled != Enabled && !_debounce.IsEnabled)
            Dispatcher.UIThread.Post(() => Load(_server.AgendaDocument));
    }

    private void Load(AgendaDocument doc)
    {
        _loading = true;
        try
        {
            Enabled = doc.Enabled;
            Title = doc.Title;
            ItemsText = doc.ItemsText;
            NotesText = doc.NotesText;
            FirstDay = new DateTimeOffset(doc.FirstDay.ToDateTime(TimeOnly.MinValue));
            Corner = doc.Corner;
        }
        finally { _loading = false; }
        UpdatePreview();
    }

    private AgendaDocument ToDocument() => new()
    {
        Enabled = Enabled,
        Title = Title ?? "",
        ItemsText = ItemsText ?? "",
        NotesText = NotesText ?? "",
        FirstDay = FirstDay is { } d ? DateOnly.FromDateTime(d.Date) : DateOnly.FromDateTime(DateTime.Today),
        Corner = Corner,
    };

    /// <summary>How the typed lines were understood: "Sat 18:00  Pizza", one per parsed item.</summary>
    private void UpdatePreview()
    {
        var doc = ToDocument();
        var agenda = doc.Build(TimeZoneInfo.Local, Array.Empty<string>());
        Preview = agenda.Items.Select(i => $"{AgendaMath.DayLabel(agenda, i)} {AgendaMath.TimeLabel(agenda, i)}  {i.Text}").ToList();
    }

    private async System.Threading.Tasks.Task PublishAsync()
    {
        var doc = ToDocument();
        await _server.ApplyAgendaAsync(doc);
        int count = _server.CurrentAgenda?.Items.Count ?? 0;
        StatusText = doc.Enabled ? $"On all screens · {count} item(s)" : "Off";
    }
}
