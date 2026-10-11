using System.Collections.ObjectModel;
using NexusMods.App.UI.WorkspaceSystem;

namespace NexusMods.App.UI.Pages.Dlc;

public interface IDlcPageViewModel : IPageViewModelInterface
{
    /// <summary>
    /// The game's own optional content that is installed, one row per item.
    /// </summary>
    ReadOnlyObservableCollection<IDlcItemViewModel> Items { get; }

    /// <summary>
    /// False when the game ships no switchable content, or none of it is installed.
    /// </summary>
    bool HasItems { get; }
}

public interface IDlcItemViewModel
{
    /// <summary>
    /// The name of the content, e.g. "Far Harbor".
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The plugin that loads it, shown as a subtitle so the user can match it to their load order.
    /// </summary>
    string PluginName { get; }

    /// <summary>
    /// Whether the content is currently switched on for this loadout.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Switches the content on or off. The switch itself only reflects what the data model says,
    /// so a failed write leaves the row showing the truth rather than the click.
    /// </summary>
    R3.ReactiveCommand<R3.Unit> CommandToggle { get; }
}
