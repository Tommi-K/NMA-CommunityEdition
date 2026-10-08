using System.Diagnostics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
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

            Disposable.Create(() =>
            {
                browser.AddressChanged -= OnAddressChanged;
                browser.TitleChanged -= OnTitleChanged;
                browser.LoadError -= OnLoadError;
            }).DisposeWith(d);

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
