using System.Windows.Input;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace NexusMods.App.UI.WorkspaceSystem;

/// <summary>
/// The mouse's back and forward buttons.
/// </summary>
/// <remarks>
/// A press becomes <see cref="RequestedEvent"/>, raised where the pointer was, so the
/// history it steps is the one nearest that spot. The event bubbles: a page with a history
/// of its own (the in-app browser) gets the first go, then the panel the page is in, then
/// the window, which falls back to the selected tab like the top bar's buttons do. Whoever
/// acts on it marks it handled.
/// </remarks>
public static class HistoryNavigation
{
    public static readonly RoutedEvent<HistoryNavigationEventArgs> RequestedEvent =
        RoutedEvent.Register<HistoryNavigationEventArgs>("HistoryNavigationRequested", RoutingStrategies.Bubble, typeof(HistoryNavigation));

    /// <summary>
    /// Which way <paramref name="button"/> steps through history, or null if it isn't one
    /// of the history buttons.
    /// </summary>
    public static bool? IsForward(MouseButton button) => button switch
    {
        MouseButton.XButton1 => false,
        MouseButton.XButton2 => true,
        _ => null,
    };

    /// <summary>
    /// Steps a tab's history, if there is anywhere to go.
    /// </summary>
    public static void Step(IPanelTabViewModel tab, bool forward)
    {
        TryExecute(forward ? tab.GoForwardInHistoryCommand : tab.GoBackInHistoryCommand);
    }

    /// <summary>
    /// Runs <paramref name="command"/> if it can run, and says whether it did.
    /// </summary>
    /// <remarks>
    /// Executing a ReactiveCommand from code ignores whether it can execute, and stepping
    /// past either end of a tab's history would open a page that isn't there.
    /// </remarks>
    public static bool TryExecute(ICommand command)
    {
        if (!command.CanExecute(null)) return false;
        command.Execute(null);
        return true;
    }
}

public class HistoryNavigationEventArgs : RoutedEventArgs
{
    /// <summary>
    /// Whether to step forward rather than back.
    /// </summary>
    public bool IsForward { get; }

    public HistoryNavigationEventArgs(bool isForward) : base(HistoryNavigation.RequestedEvent)
    {
        IsForward = isForward;
    }
}
