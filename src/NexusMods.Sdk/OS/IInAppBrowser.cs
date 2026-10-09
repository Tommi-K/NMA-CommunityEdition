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

    /// <summary>
    /// Opens <paramref name="uri"/> in a tab that starts the download itself, and waits for
    /// that download to reach the app.
    /// </summary>
    /// <remarks>
    /// One at a time, however many callers ask at once: a tab has to be the one on screen to
    /// load its page and press its button, and the free download sits behind a countdown, so
    /// a collection's worth of tabs opened together would fight over the foreground and
    /// mostly time out. Waiting here is what turns a list of mods into one download after
    /// another.
    /// </remarks>
    /// <param name="uri">The download page to drive.</param>
    /// <param name="title">Tab title to use until the page reports its own.</param>
    /// <param name="cancellationToken">Gives up waiting, and the queued place in line with it.</param>
    ValueTask<InAppDownloadOutcome> StartDownload(Uri uri, string? title = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// How an attempt to start a download in a tab turned out.
/// </summary>
[PublicAPI]
public enum InAppDownloadOutcome
{
    /// <summary>
    /// No tab could be opened, so nothing was attempted and the caller should fall back to
    /// the system browser.
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// The page handed a download to the app.
    /// </summary>
    Started = 1,

    /// <summary>
    /// The tab opened, but no download had arrived by the time the wait ran out. The tab is
    /// left on the page, so the user can still press the button themselves.
    /// </summary>
    NotStarted = 2,
}
