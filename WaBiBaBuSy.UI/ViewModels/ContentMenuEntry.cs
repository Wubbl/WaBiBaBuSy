using System.Windows.Input;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>A row of the gallery's "Get content ▾" menu: a link (with a command) or a non-clickable title / tip.</summary>
public sealed record ContentMenuEntry(string Header, string? Tip, ICommand? Command)
{
    /// <summary>Links are clickable; titles and the tip line are not.</summary>
    public bool IsLink => Command != null;
}
