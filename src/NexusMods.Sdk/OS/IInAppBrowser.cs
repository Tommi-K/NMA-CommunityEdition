using JetBrains.Annotations;

namespace NexusMods.Sdk;

/// <summary>
/// Opens web pages inside the app instead of handing them to the system browser.
/// </summary>
/// <remarks>
/// Implemented by the UI, which has the tab system. Code in lower layers resolves this
/// optionally and falls back to <see cref="IOSInterop.OpenUri"/> when it isn't available,
/// so a headless host keeps working.
/// </remarks>
[PublicAPI]
public interface IInAppBrowser
{
    /// <summary>
    /// Tries to open <paramref name="uri"/> in a tab.
    /// </summary>
    /// <param name="uri">The page to show.</param>
    /// <param name="title">Tab title to use until the page reports its own.</param>
    /// <param name="closeAfterDownload">
    /// True for a tab that only exists to start a download, such as the download page a
    /// non-premium "Download" button sends the user to: it is closed again as soon as the
    /// <c>nxm://</c> handoff has been taken. Leave false for a tab the user is browsing
    /// in, so that downloading a mod doesn't take the page away from them.
    /// </param>
    /// <param name="autoStartDownload">
    /// True to press the download button on the page once it loads, instead of leaving it
    /// to the user. Only meaningful for a page that has one.
    /// </param>
    /// <returns>False when the page could not be opened, in which case the caller should fall back.</returns>
    bool TryOpen(Uri uri, string? title = null, bool closeAfterDownload = false, bool autoStartDownload = false);
}
