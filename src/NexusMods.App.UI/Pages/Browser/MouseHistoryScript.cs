namespace NexusMods.App.UI.Pages.Browser;

/// <summary>
/// The script that hands the mouse's back and forward buttons over to the app.
/// </summary>
/// <remarks>
/// Chromium runs in a window of its own, so a press over a page never reaches the app's
/// input handling. The script reports it over the console instead, the same channel
/// <see cref="AutoDownloadScript"/> uses, and the app then steps the browser's history or,
/// once that runs out, the tab's. Whatever Chromium itself does with these buttons is the
/// default action of the press and release, so the script cancels both, and a press is
/// never acted on twice.
/// </remarks>
internal static class MouseHistoryScript
{
    public const string BackMessage = "[nma-history] back";
    public const string ForwardMessage = "[nma-history] forward";

    public const string Source =
        """
        (function () {
            // Injected at both the start and the end of a load, so twice per document.
            if (window.__nmaMouseHistory) return;
            window.__nmaMouseHistory = true;

            // MouseEvent.button: 3 is back, 4 is forward.
            function isHistoryButton(e) {
                return e.button === 3 || e.button === 4;
            }

            // Capturing on the window runs ahead of the page's own listeners.
            window.addEventListener('mousedown', function (e) {
                if (isHistoryButton(e)) e.preventDefault();
            }, true);

            window.addEventListener('mouseup', function (e) {
                if (!isHistoryButton(e)) return;
                e.preventDefault();
                console.log(e.button === 3 ? '[nma-history] back' : '[nma-history] forward');
            }, true);
        })();
        """;
}
