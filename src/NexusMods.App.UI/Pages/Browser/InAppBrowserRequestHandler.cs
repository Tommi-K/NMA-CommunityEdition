using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Handlers;

namespace NexusMods.App.UI.Pages.Browser;

/// <summary>
/// Applies <see cref="AdBlocker"/> to everything the embedded browser loads, leaves out what a
/// page loaded only for its download has no use for, and sends a link the user asked to open
/// in a new tab to one.
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
    private readonly Func<bool> _isLoadingForItsDownload;

    private int _skippedRequests;
    private readonly ConcurrentDictionary<CefResourceType, int> _requestsLetThrough = new();

    /// <param name="adBlocker">The rules for ads and trackers.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="openInNewTab">Opens a link in a new tab.</param>
    /// <param name="isLoadingForItsDownload">
    /// Whether the page in this tab is being loaded only to get its download. Called on a
    /// Chromium thread.
    /// </param>
    public InAppBrowserRequestHandler(AdBlocker adBlocker, ILogger logger, OpenLinkInNewTab openInNewTab, Func<bool> isLoadingForItsDownload)
    {
        _adBlocker = adBlocker;
        _logger = logger;
        _openInNewTab = openInNewTab;
        _isLoadingForItsDownload = isLoadingForItsDownload;
    }

    /// <summary>
    /// How many requests have been skipped because of <see cref="IsNeedlessForDownload"/>.
    /// </summary>
    public int SkippedRequests => Volatile.Read(ref _skippedRequests);

    /// <summary>
    /// What pages loaded only for their download asked for and were given, by type, most first.
    /// Some of it comes out of Chromium's cache rather than off the network.
    /// </summary>
    public string RequestsLetThrough => string.Join(", ", _requestsLetThrough
        .OrderByDescending(static pair => pair.Value)
        .Select(static pair => $"{pair.Key} {pair.Value}"));

    /// <summary>
    /// Whether a request is one a page loaded only for its download can do without.
    /// </summary>
    /// <remarks>
    /// Images are what is left to save on such a page: every mod has its own, so nothing is
    /// cached for a mod not seen before, while the site's scripts and styles are shared by all
    /// its pages and cached for a year. Scripts stay in any case, since the download button
    /// needs them on a page that doesn't state its link. Only the page's own frame is touched,
    /// as a Cloudflare check runs in a frame of its own and has to be left to work.
    /// </remarks>
    internal static bool IsNeedlessForDownload(CefResourceType resourceType, bool isMainFrame)
    {
        return isMainFrame && resourceType is CefResourceType.Image
            or CefResourceType.Media
            or CefResourceType.Favicon
            or CefResourceType.Prefetch
            or CefResourceType.Ping;
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

            var isForDownload = _isLoadingForItsDownload();

            if (isForDownload && IsNeedlessForDownload(request.ResourceType, frame?.IsMain == true))
            {
                Interlocked.Increment(ref _skippedRequests);
                return new BlockedResourceRequestHandler();
            }

            if (_adBlocker.ShouldBlock(request.Url, request.ResourceType)) return new BlockedResourceRequestHandler();

            if (isForDownload) _requestsLetThrough.AddOrUpdate(request.ResourceType, addValue: 1, static (_, count) => count + 1);

            return null!;
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
