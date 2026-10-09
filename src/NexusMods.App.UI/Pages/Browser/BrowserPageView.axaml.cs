using System.Diagnostics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Xilium.CefGlue.Avalonia;
using Xilium.CefGlue.Common.Events;

namespace NexusMods.App.UI.Pages.Browser;

public partial class BrowserPageView : ReactiveUserControl<IBrowserPageViewModel>
{
    private AvaloniaCefBrowser? _browser;

    public BrowserPageView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
        {
            this.BindCommand(ViewModel, vm => vm.CommandOpenInSystemBrowser, view => view.OpenInSystemBrowserButton)
                .DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.Address, view => view.AddressText.Text)
                .DisposeWith(d);

            var browser = GetOrCreateBrowser();

            browser.AddressChanged += OnAddressChanged;
            browser.TitleChanged += OnTitleChanged;
            browser.LoadError += OnLoadError;
            browser.LoadingStateChange += OnLoadingStateChange;
            browser.LoadEnd += OnLoadEnd;
            browser.ConsoleMessage += OnConsoleMessage;

            Disposable.Create(() =>
            {
                browser.AddressChanged -= OnAddressChanged;
                browser.TitleChanged -= OnTitleChanged;
                browser.LoadError -= OnLoadError;
                browser.LoadingStateChange -= OnLoadingStateChange;
                browser.LoadEnd -= OnLoadEnd;
                browser.ConsoleMessage -= OnConsoleMessage;
            }).DisposeWith(d);

            // The commands only signal intent; the browser lives here, so this is where
            // the navigation actually happens.
            this.BindCommand(ViewModel, vm => vm.CommandGoBack, view => view.BackButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.CommandGoForward, view => view.ForwardButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.CommandReload, view => view.ReloadButton).DisposeWith(d);

            ViewModel!.CommandGoBack.Subscribe(_ => browser.GoBack()).DisposeWith(d);
            ViewModel!.CommandGoForward.Subscribe(_ => browser.GoForward()).DisposeWith(d);
            ViewModel!.CommandReload.Subscribe(_ => browser.Reload(ignoreCache: false)).DisposeWith(d);

            this.WhenAnyValue(view => view.ViewModel!.Address)
                .Where(static address => !string.IsNullOrWhiteSpace(address))
                .Subscribe(address =>
                {
                    if (!string.Equals(browser.Address, address, StringComparison.Ordinal))
                        browser.Address = address;
                })
                .DisposeWith(d);
        });
    }

    private AvaloniaCefBrowser GetOrCreateBrowser()
    {
        if (_browser is not null) return _browser;

        _browser = new AvaloniaCefBrowser();

        // Set before the first navigation so nothing slips past the filter.
        if (ViewModel is not null) _browser.RequestHandler = ViewModel.BrowserRequestHandler;

        BrowserHost.Child = _browser;
        return _browser;
    }

    // Every handler below is raised on a Chromium thread, not the UI thread. Touching a
    // bound property from there makes Avalonia throw, and an exception escaping onto a
    // CEF thread aborts the whole process rather than surfacing as an error, so each one
    // marshals to the UI thread and swallows its own failures.
    private void OnAddressChanged(object sender, string address) => PostToUi(() =>
    {
        if (ViewModel is not null) ViewModel.Address = address;
    });

    private void OnTitleChanged(object sender, string title) => PostToUi(() =>
    {
        if (ViewModel is not null) ViewModel.PageTitle = title;
    });

    /// <summary>
    /// Chromium owns the history, so the back/forward buttons take their enabled state
    /// from here rather than from anything the app tracks itself.
    /// </summary>
    private void OnLoadingStateChange(object sender, LoadingStateChangeEventArgs e)
    {
        var canGoBack = e.CanGoBack;
        var canGoForward = e.CanGoForward;
        var isLoading = e.IsLoading;

        PostToUi(() =>
        {
            if (ViewModel is null) return;
            ViewModel.CanGoBack = canGoBack;
            ViewModel.CanGoForward = canGoForward;
            ViewModel.IsLoading = isLoading;
        });
    }

    /// <summary>
    /// A tab opened to start a download gets it going itself once the page is there.
    /// </summary>
    private void OnLoadEnd(object sender, LoadEndEventArgs e)
    {
        // Sub-frames load too, and a Nexus page has plenty; only the page itself matters.
        if (!e.Frame.IsMain) return;

        // Read here, on the Chromium thread the frame belongs to, rather than passing the
        // frame itself to the UI thread.
        var url = e.Frame.Url;

        PostToUi(() => StartDownloadWhenReady(url));
    }

    /// <summary>
    /// Starts the download for a page the tab was opened to download from.
    /// </summary>
    /// <remarks>
    /// The work happens in the injected script, which reports the page's own
    /// <c>nxm://</c> handoff link back over the console for
    /// <see cref="OnConsoleMessage"/> to act on, and presses the download button if the
    /// page doesn't state one.
    /// </remarks>
    private void StartDownloadWhenReady(string pageUrl)
    {
        if (ViewModel is null || _browser is null) return;

        var script = ViewModel.TryGetPageLoadScript(pageUrl);
        if (script is null) return;

        _browser.ExecuteJavaScript(script, pageUrl, line: 0);
    }

    /// <summary>
    /// Surfaces the injected script's own reports in the app log.
    /// </summary>
    /// <remarks>
    /// Filtered to the script's marker: the rest of a page's console output is not ours and
    /// would bury the log. Without this the script failing to find a button would be
    /// invisible, since Nexus Mods' markup can change under us at any time.
    /// </remarks>
    private void OnConsoleMessage(object sender, ConsoleMessageEventArgs e)
    {
        var message = e.Message;
        if (message is null || !message.StartsWith(AutoDownloadScript.LogPrefix, StringComparison.Ordinal)) return;

        PostToUi(() =>
        {
            ReactiveUiExtensions.DefaultLogger.LogInformation("In-app browser {Message}", message);
            TryHandOffReportedLink(message);
        });
    }

    /// <summary>
    /// Acts on the handoff link the injected script found, if this message is one.
    /// </summary>
    private void TryHandOffReportedLink(string message)
    {
        var body = message[AutoDownloadScript.LogPrefix.Length..];
        if (!body.StartsWith(AutoDownloadScript.HandoffPrefix, StringComparison.Ordinal)) return;

        var link = body[AutoDownloadScript.HandoffPrefix.Length..].Trim();

        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("nxm", StringComparison.OrdinalIgnoreCase))
        {
            ReactiveUiExtensions.DefaultLogger.LogWarning("Auto-download reported an unusable handoff link `{Link}`", link);
            return;
        }

        if (ViewModel?.TryHandleAppUri(link) == true)
        {
            ReactiveUiExtensions.DefaultLogger.LogInformation("Auto-download started from the page's own handoff link");
            return;
        }

        ReactiveUiExtensions.DefaultLogger.LogWarning("Auto-download could not hand off `{Link}`", link);
    }

    /// <summary>
    /// Chromium can't resolve our own schemes, so a click on an `nxm://` link surfaces
    /// here as a failed navigation. That is where the download handoff is picked up.
    /// Whether the tab then closes is the view model's call, since it knows why the tab
    /// was opened.
    /// </summary>
    private void OnLoadError(object sender, LoadErrorEventArgs e)
    {
        var failedUrl = e.FailedUrl;
        PostToUi(() => ViewModel?.TryHandleAppUri(failedUrl));
    }

    private static void PostToUi(Action action)
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"In-app browser handler failed: {e}");
                }
            });
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Unable to post in-app browser event to the UI thread: {e}");
        }
    }
}
