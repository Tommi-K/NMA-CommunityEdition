using Microsoft.Extensions.Logging;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Handlers;

namespace NexusMods.App.UI.Pages.Browser;

/// <summary>
/// Applies <see cref="AdBlocker"/> to everything the embedded browser loads.
/// </summary>
/// <remarks>
/// One per browser, because CefGlue owns the handler's lifetime and disposes it with the
/// browser it was attached to. The rules themselves live on the shared
/// <see cref="AdBlocker"/> singleton.
/// </remarks>
internal sealed class AdBlockRequestHandler : RequestHandler
{
    private readonly AdBlocker _adBlocker;
    private readonly ILogger _logger;

    public AdBlockRequestHandler(AdBlocker adBlocker, ILogger logger)
    {
        _adBlocker = adBlocker;
        _logger = logger;
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
