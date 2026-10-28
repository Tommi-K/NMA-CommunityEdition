using Microsoft.Extensions.Logging;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Handlers;

namespace NexusMods.App.UI.Pages.Browser;

/// <summary>
/// Applies <see cref="AdBlocker"/> to everything the embedded browser loads, and sends a
/// link the user asked to open in a new tab to one.
/// </summary>
/// <remarks>
/// One per browser, because CefGlue owns the handler's lifetime and disposes it with the
/// browser it was attached to. The rules themselves live on the shared
/// <see cref="AdBlocker"/> singleton.
/// </remarks>
internal sealed class InAppBrowserRequestHandler : RequestHandler
{
    private readonly AdBlocker _adBlocker;
    private readonly ILogger _logger;
    private readonly OpenLinkInNewTab _openInNewTab;

    public InAppBrowserRequestHandler(AdBlocker adBlocker, ILogger logger, OpenLinkInNewTab openInNewTab)
    {
        _adBlocker = adBlocker;
        _logger = logger;
        _openInNewTab = openInNewTab;
    }

    /// <summary>
    /// A ctrl-, shift- or middle-click on an ordinary link. Left alone, Chromium would load
    /// the link in this tab instead.
    /// </summary>
    /// <remarks>
    /// A link with <c>target="_blank"</c> doesn't come through here but through
    /// <see cref="NewTabLifeSpanHandler"/>, since the page asks for that window itself.
    /// </remarks>
    protected override bool OnOpenUrlFromTab(
        CefBrowser browser,
        CefFrame frame,
        string targetUrl,
        CefWindowOpenDisposition targetDisposition,
        bool userGesture)
    {
        // Same as below: nothing may escape onto the Chromium thread. Returning false lets
        // the navigation go ahead as it would have.
        try
        {
            if (!userGesture) return false;

            // Alt-click (save to disk) and the rest stay Chromium's.
            if (targetDisposition is not (CefWindowOpenDisposition.NewForegroundTab
                or CefWindowOpenDisposition.NewBackgroundTab
                or CefWindowOpenDisposition.NewWindow
                or CefWindowOpenDisposition.NewPopup)) return false;

            _openInNewTab(targetUrl, userGesture: true);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Unable to open `{Url}` in a new tab", targetUrl);
            return false;
        }
    }

    protected override CefResourceRequestHandler GetResourceRequestHandler(
        CefBrowser browser,
        CefFrame frame,
        CefRequest request,
        bool isNavigation,
        bool isDownload,
        string requestInitiator,
        ref bool disableDefaultHandling)
    {
        // Called on a Chromium thread, where an escaping exception aborts the process
        // instead of surfacing as an error, so every failure here falls through to
        // "allow". Returning null means default handling.
        try
        {
            // A real download is never an ad, and must not be interfered with.
            if (isDownload) return null!;
            if (!_adBlocker.ShouldBlock(request.Url, request.ResourceType)) return null!;

            return new BlockedResourceRequestHandler();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "In-app browser filter failed; allowing the request through");
            return null!;
        }
    }
}

/// <summary>
/// Cancels the single request it is created for.
/// </summary>
internal sealed class BlockedResourceRequestHandler : CefResourceRequestHandler
{
    protected override CefCookieAccessFilter GetCookieAccessFilter(CefBrowser browser, CefFrame frame, CefRequest request) => null!;

    protected override CefReturnValue OnBeforeResourceLoad(CefBrowser browser, CefFrame frame, CefRequest request, CefCallback callback)
        => CefReturnValue.Cancel;
}
