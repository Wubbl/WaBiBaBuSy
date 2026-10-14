using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaBiBaBuSy.Models.Testing;
using WaBiBaBuSy.Models.Wallpaper;
using WaBiBaBuSy.Models.Wallpaper.Suggest;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>One suggestion card in the strip.</summary>
public partial class SuggestionCardViewModel : ObservableObject
{
    public SuggestionCardViewModel(Suggestion suggestion) => Suggestion = suggestion;

    /// <summary>The built suggestion.</summary>
    public Suggestion Suggestion { get; }
    /// <summary>Card title.</summary>
    public string Name => Suggestion.RecipeName;
    /// <summary>Why it was suggested.</summary>
    public string Why => Suggestion.Why;

    /// <summary>True for the card that is the draft right now.</summary>
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// The Suggest strip (spec 2026-10-14 §3): saves the draft when it opens, loads a clicked card as the draft,
/// Shuffle replaces the cards, Keep closes, Revert restores the saved draft. Nothing here sends to machines.
/// </summary>
public partial class SuggestionsViewModel : ObservableObject
{
    private readonly Func<CrossScreenConfig> _captureDraft;
    private readonly Action<CrossScreenConfig> _loadDraft;
    private readonly Func<Task<SelectionProfile?>> _profileSelection;
    private CrossScreenConfig? _savedDraft;
    private SelectionProfile? _selection;
    private int _seed;

    public SuggestionsViewModel(Func<CrossScreenConfig> captureDraft, Action<CrossScreenConfig> loadDraft,
        Func<Task<SelectionProfile?>> profileSelection)
    {
        _captureDraft = captureDraft;
        _loadDraft = loadDraft;
        _profileSelection = profileSelection;
    }

    /// <summary>The cards on show.</summary>
    public ObservableCollection<SuggestionCardViewModel> Cards { get; } = new();

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNote))] private string? _note;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(KeepCommand))] private bool _canKeep;

    /// <summary>True when the info line has text.</summary>
    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>Profile the picked media and show the best four cards.</summary>
    [RelayCommand]
    private async Task OpenAsync()
    {
        var selection = await _profileSelection();
        if (selection == null) return;
        _selection = selection;
        if (!IsOpen) _savedDraft = _captureDraft(); // pressed again while open: ✕ still returns to the pre-Suggest scene
        CanKeep = false;
        _seed = Environment.TickCount; // UI-only: the built scenes carry their own seeds, so they stay deterministic
        Title = "✨ Suggestions for: " + selection.Describe();
        Show(SceneSuggester.Suggest(selection, _savedDraft!, _seed));
        if (Cards.Count == 0) Note = "No suggestions for this selection";
        IsOpen = true;
    }

    /// <summary>Four more cards, preferring recipes not on show; the draft is untouched.</summary>
    [RelayCommand]
    private void Shuffle()
    {
        if (_selection == null || _savedDraft == null) return;
        _seed = PatternLayout.Hash3(_seed, 1, 0);
        var shown = Cards.Select(c => c.Name).ToList();
        Show(SceneSuggester.Shuffle(_selection, _savedDraft, _seed, shown));
    }

    /// <summary>Make the card the draft (the room view previews it).</summary>
    [RelayCommand]
    private void Apply(SuggestionCardViewModel? card)
    {
        if (card == null) return;
        foreach (var c in Cards) c.IsSelected = c == card;
        _loadDraft(SceneFile.Clone(card.Suggestion.Scene));
        CanKeep = true;
    }

    /// <summary>Close the strip, keeping the clicked card as the draft.</summary>
    [RelayCommand(CanExecute = nameof(CanKeep))]
    private void Keep() => Close();

    /// <summary>Close the strip and restore the draft from before it opened.</summary>
    [RelayCommand]
    private void Revert()
    {
        if (_savedDraft != null) _loadDraft(_savedDraft);
        Close();
    }

    /// <summary>Close without touching the draft (the user picked other media).</summary>
    public void Discard()
    {
        if (IsOpen) Close();
    }

    private void Show(SuggestionSet set)
    {
        Cards.Clear();
        foreach (var s in set.Cards) Cards.Add(new SuggestionCardViewModel(s));
        Note = set.Note;
    }

    private void Close()
    {
        IsOpen = false;
        CanKeep = false;
        Cards.Clear();
        _savedDraft = null;
        _selection = null;
    }
}
