using System.Reactive;
using NexusMods.App.UI.WorkspaceSystem;
using ReactiveUI;
using Xilium.CefGlue.Common.Handlers;

namespace NexusMods.App.UI.Pages.Browser;

public interface IBrowserPageViewModel : IPageViewModelInterface
{
    /// <summary>
    /// Gets or sets the current context of the page.
    /// </summary>
    public BrowserPageContext? Context { get; set; }

    /// <summary>
    /// The address the embedded browser should be showing.
    /// </summary>
    public string Address { get; set; }

    /// <summary>
    /// Title reported by the loaded page.
    /// </summary>
    public string PageTitle { get; set; }

    /// <summary>
    /// Whether the page has somewhere to go back to.
    /// </summary>
    public bool CanGoBack { get; set; }

    /// <summary>
    /// Whether the page has somewhere to go forward to.
    /// </summary>
    public bool CanGoForward { get; set; }

    /// <summary>
    /// Whether the page is still loading.
    /// </summary>
    public bool IsLoading { get; set; }

    /// <summary>
    /// Opens the current address in the system browser instead.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CommandOpenInSystemBrowser { get; }

    // The three commands below carry no behaviour of their own: only the view holds the
    // Chromium instance, so it subscribes to them and drives the navigation. Keeping them
    // here is what lets the buttons' enabled state stay bound to the view model.

    /// <summary>
    /// Signals that the embedded browser should go back one page.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CommandGoBack { get; }

    /// <summary>
    /// Signals that the embedded browser should go forward one page.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CommandGoForward { get; }

    /// <summary>
    /// Signals that the embedded browser should reload the current page.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CommandReload { get; }

    /// <summary>
    /// Handler the embedded browser filters its requests through, dropping the ones
    /// aimed at known ad and tracker domains.
    /// </summary>
    public RequestHandler BrowserRequestHandler { get; }

    /// <summary>
    /// The script to run once <paramref name="pageUrl"/> has finished loading, or null when
    /// there is nothing to do for it.
    /// </summary>
    /// <remarks>
    /// Returns a script only for a tab that was opened to start a download, and only on the
    /// Nexus Mods page that actually carries a download button.
    /// </remarks>
    public string? TryGetPageLoadScript(string pageUrl);

    /// <summary>
    /// Handles a URL the embedded browser can't navigate to itself, such as an
    /// <c>nxm://</c> download handoff. Also closes the tab afterwards when it was opened
    /// only to start a download; see <see cref="BrowserPageContext.CloseAfterDownload"/>.
    /// </summary>
    /// <returns>
    /// True when the app took the URL, in which case the browser should cancel the navigation.
    /// </returns>
    public bool TryHandleAppUri(string url);
}
