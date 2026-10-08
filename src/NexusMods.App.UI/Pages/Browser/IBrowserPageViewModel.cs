using System.Reactive;
using NexusMods.App.UI.WorkspaceSystem;
using ReactiveUI;

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
    /// Opens the current address in the system browser instead.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CommandOpenInSystemBrowser { get; }

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
