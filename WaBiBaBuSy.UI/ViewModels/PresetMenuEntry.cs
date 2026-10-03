using System.Windows.Input;

namespace WaBiBaBuSy.UI.ViewModels;

/// <summary>A preset in a menu: its name and what clicking it does (load, add to playlist, …).</summary>
public sealed record PresetMenuEntry(string Name, ICommand Command);
