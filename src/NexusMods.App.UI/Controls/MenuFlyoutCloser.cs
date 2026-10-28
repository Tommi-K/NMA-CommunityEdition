using Avalonia.Controls;

namespace NexusMods.App.UI.Controls;

/// <summary>
/// Closes a <see cref="MenuFlyout"/> when an item in it is clicked, which Avalonia leaves
/// undone for an item the flyout doesn't hold directly.
/// </summary>
/// <remarks>
/// Avalonia finds the menu to close by walking up from the clicked item through its logical
/// parents. The row menus of the Library and Mods pages wrap each item in a
/// <c>ComponentControl</c>, which ends that walk, so the menu stayed open until a click
/// landed somewhere else in the window. A web page in a tab is a native window whose clicks
/// Avalonia never sees, so after "View mod page" the menu hung over the page.
/// </remarks>
public static class MenuFlyoutCloser
{
    /// <summary>
    /// Hooks every <see cref="MenuFlyoutPresenter"/> in the app. Call once at startup.
    /// </summary>
    public static void Register()
    {
        // The item marks its click handled once it has run its command, so handled events
        // have to be let through.
        MenuItem.ClickEvent.AddClassHandler<MenuFlyoutPresenter>(static (presenter, e) =>
        {
            if (e.Source is not MenuItem { HasSubMenu: false, StaysOpenOnClick: false }) return;
            presenter.Close();
        }, handledEventsToo: true);
    }
}
