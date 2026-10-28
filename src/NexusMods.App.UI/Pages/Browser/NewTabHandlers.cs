using Microsoft.Extensions.Logging;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Handlers;

namespace NexusMods.App.UI.Pages.Browser;

/// <summary>
/// Opens <paramref name="url"/> in an app tab of its own, behind the one the user is
/// reading. Safe to call from any thread.
/// </summary>
/// <param name="url">The link to open.</param>
/// <param name="userGesture">Whether the user asked for it, rather than the page on its own.</param>
internal delegate void OpenLinkInNewTab(string url, bool userGesture);

/// <summary>
/// Turns the windows a page opens -- a <c>target="_blank"</c> link, <c>window.open</c> --
/// into app tabs.
/// </summary>
/// <remarks>
/// Every popup is cancelled. The browser is drawn off-screen into a single tab, so a popup
/// Chromium creates has nowhere to be shown, and it reports its loads and title to the tab
/// that opened it as if they were that tab's own.
/// </remarks>
internal sealed class NewTabLifeSpanHandler : LifeSpanHandler
{
    private readonly ILogger _logger;
    private readonly OpenLinkInNewTab _openInNewTab;

    public NewTabLifeSpanHandler(ILogger logger, OpenLinkInNewTab openInNewTab)
    {
        _logger = logger;
        _openInNewTab = openInNewTab;
    }

    protected override bool OnBeforePopup(
        CefBrowser browser,
        CefFrame frame,
        string targetUrl,
        string targetFrameName,
        CefWindowOpenDisposition targetDisposition,
        bool userGesture,
        CefPopupFeatures popupFeatures,
        CefWindowInfo windowInfo,
        ref CefClient client,
        CefBrowserSettings settings,
        ref CefDictionaryValue extraInfo,
        ref bool noJavascriptAccess)
    {
        // Called on a Chromium thread, where an escaping exception aborts the process.
        try
        {
            _openInNewTab(targetUrl, userGesture);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Unable to open popup `{Url}` in a new tab", targetUrl);
        }

        return true;
    }
}

/// <summary>
/// Adds "Open link in new tab" to the menu a right-click on a link brings up.
/// </summary>
internal sealed class NewTabContextMenuHandler : ContextMenuHandler
{
    private const int OpenLinkInNewTabCommand = (int)CefMenuId.UserFirst;

    private readonly ILogger _logger;
    private readonly OpenLinkInNewTab _openInNewTab;

    public NewTabContextMenuHandler(ILogger logger, OpenLinkInNewTab openInNewTab)
    {
        _logger = logger;
        _openInNewTab = openInNewTab;
    }

    // Both called on a Chromium thread, where an escaping exception aborts the process.
    protected override void OnBeforeContextMenu(CefBrowser browser, CefFrame frame, CefContextMenuParams state, CefMenuModel model)
    {
        try
        {
            if (string.IsNullOrEmpty(state.LinkUrl)) return;

            var hasDefaultItems = model.Count > 0;
            model.InsertItemAt(0, OpenLinkInNewTabCommand, "Open link in new tab");
            if (hasDefaultItems) model.InsertSeparatorAt(1);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Unable to add the new tab entry to the in-app browser menu");
        }
    }

    protected override bool OnContextMenuCommand(CefBrowser browser, CefFrame frame, CefContextMenuParams state, int commandId, CefEventFlags eventFlags)
    {
        if (commandId != OpenLinkInNewTabCommand) return false;

        try
        {
            _openInNewTab(state.LinkUrl, userGesture: true);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Unable to open `{Url}` in a new tab", state.LinkUrl);
        }

        return true;
    }
}
