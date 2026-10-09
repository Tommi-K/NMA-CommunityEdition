using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using NexusMods.UI.Sdk;
using NexusMods.UI.Sdk.Dialog;

namespace NexusMods.App.UI.Notifications;

public class WindowNotificationService : IWindowNotificationService
{
    private WindowNotificationManager? _notificationManager;

    /// <summary>
    /// The popup the notifications are shown in, or null when they are in the window itself
    /// because no popup could be set up.
    /// </summary>
    /// <remarks>
    /// A notification has to be in a window of its own to be seen over the in-app browser at
    /// all. Chromium renders into a native child window of the app's window, and a native
    /// window covers everything the app draws in that window, whichever layer it is drawn in
    /// -- so a notification over an open browser tab was simply invisible. A popup is a
    /// separate native window, which does come out on top.
    /// </remarks>
    private Popup? _popup;

    /// <summary>
    /// How many notifications are on screen. The popup is open while there is at least one and
    /// closed again afterwards, so that its window isn't sitting over the app taking clicks
    /// that were meant for whatever is underneath.
    /// </summary>
    private int _onScreen;

    /// <summary>
    /// Lazy initialization, as main window may not available at creation time
    /// Needs to be called on UI thread
    /// </summary>
    private WindowNotificationManager? GetNotificationManager()
    {
        if (_notificationManager != null)
            return _notificationManager;

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: not null } desktopLifetime)
        {
            // Unable to access the main window, so we cannot show notifications
            return null;
        }

        var mainWindow = desktopLifetime.MainWindow;

        // The layer is only there once the window's template has been applied. Until then
        // there is nothing to hang a popup on, so fall back to notifications inside the
        // window: covered by a browser tab, but better than none at all.
        var overlayLayer = OverlayLayer.GetOverlayLayer(mainWindow);
        if (overlayLayer is null)
        {
            // Must be on UI thread to create the WindowNotificationManager
            _notificationManager = new WindowNotificationManager(mainWindow)
            {
                Position = NotificationPosition.BottomCenter,
                MaxItems = 4,
            };

            return _notificationManager;
        }

        _notificationManager = new WindowNotificationManager
        {
            Position = NotificationPosition.BottomCenter,
            MaxItems = 4,
        };

        // Anchored to the bottom of the window and growing upwards, which is where the
        // notifications were before, and kept in place by the popup following its target.
        _popup = new Popup
        {
            PlacementTarget = mainWindow,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.Bottom,
            PlacementGravity = PopupGravity.Top,
            VerticalOffset = -16,
            IsLightDismissEnabled = false,
            Topmost = true,
            Child = _notificationManager,
        };

        // A popup needs a place in the window's tree to be positioned against it; it draws
        // nothing where it is added, since its contents go to its own window.
        overlayLayer.Children.Add(_popup);

        return _notificationManager;
    }

    /// <Inheritdoc />
    public void ShowToast(
        string message,
        ToastNotificationVariant type = ToastNotificationVariant.Neutral,
        TimeSpan? expiration = null,
        DialogButtonDefinition[]? buttonDefinitions = null,
        Action<ButtonDefinitionId>? buttonHandler = null)
    {
        DispatcherHelper.EnsureOnUIThread(() =>
            {
                var manager = GetNotificationManager();
                if (manager == null) return;
                
                // TODO: Use ToastNotificationVariant
                // TODO: Use buttons and handler
        
                var notification = new Notification(
                    null,
                    message,
                    NotificationType.Information,
                    expiration ?? TimeSpan.FromSeconds(5),
                    onClose: OnNotificationClosed);

                // Open before showing: the notification is measured and laid out in the
                // popup's window, which has to be there first.
                _onScreen++;
                if (_popup is not null) _popup.IsOpen = true;

                // Must be on UI thread to show the notification
                manager.Show(notification);
                return;
            }
        );
    }

    /// <summary>
    /// Closes the popup once the last notification has gone from it.
    /// </summary>
    /// <remarks>
    /// Called on the UI thread, by the notification seeing out its time on screen or being
    /// dismissed.
    /// </remarks>
    private void OnNotificationClosed()
    {
        _onScreen = Math.Max(0, _onScreen - 1);
        if (_onScreen > 0) return;

        if (_popup is not null) _popup.IsOpen = false;
    }
}
