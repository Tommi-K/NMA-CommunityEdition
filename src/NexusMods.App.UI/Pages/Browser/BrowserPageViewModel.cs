using Avalonia.Threading;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.CLI.Types;
using NexusMods.Sdk;
using NexusMods.UI.Sdk.Icons;
using Xilium.CefGlue.Common.Handlers;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Pages.Browser;

[UsedImplicitly]
public class BrowserPageViewModel : APageViewModel<IBrowserPageViewModel>, IBrowserPageViewModel, IBrowserDownloadDriver
{
    private readonly ILogger<BrowserPageViewModel> _logger;
    private readonly IOSInterop _osInterop;
    private readonly IIpcProtocolHandler[] _protocolHandlers;
    private readonly ILoginManager _loginManager;
    private readonly BrowserDownloadTracker _downloadTracker;

    [Reactive] public BrowserPageContext? Context { get; set; }
    [Reactive] public string Address { get; set; } = "about:blank";
    [Reactive] public string PageTitle { get; set; } = string.Empty;
    [Reactive] public bool CanGoBack { get; set; }
    [Reactive] public bool CanGoForward { get; set; }
    [Reactive] public bool IsLoading { get; set; }

    public ReactiveCommand<Unit, Unit> CommandOpenInSystemBrowser { get; }
    public ReactiveCommand<Unit, Unit> CommandGoBack { get; }
    public ReactiveCommand<Unit, Unit> CommandGoForward { get; }
    public ReactiveCommand<Unit, Unit> CommandReload { get; }

    public RequestHandler BrowserRequestHandler { get; }
    public LifeSpanHandler BrowserLifeSpanHandler { get; }
    public ContextMenuHandler BrowserContextMenuHandler { get; }

    public BrowserPageViewModel(
        ILogger<BrowserPageViewModel> logger,
        IWindowManager windowManager,
        IOSInterop osInterop,
        IServiceProvider serviceProvider) : base(windowManager)
    {
        _logger = logger;
        _osInterop = osInterop;
        _protocolHandlers = serviceProvider.GetServices<IIpcProtocolHandler>().ToArray();
        _loginManager = serviceProvider.GetRequiredService<ILoginManager>();
        _downloadTracker = serviceProvider.GetRequiredService<BrowserDownloadTracker>();

        // One of each handler per tab: CefGlue disposes them along with the browser they are
        // attached to, so they can't be shared. The ad rules behind them are a shared singleton.
        BrowserRequestHandler = new InAppBrowserRequestHandler(serviceProvider.GetRequiredService<AdBlocker>(), logger, OpenLinkInNewTab);
        BrowserLifeSpanHandler = new NewTabLifeSpanHandler(logger, OpenLinkInNewTab);
        BrowserContextMenuHandler = new NewTabContextMenuHandler(logger, OpenLinkInNewTab);

        TabTitle = "Mod page";
        TabIcon = IconValues.Nexus;

        CommandOpenInSystemBrowser = ReactiveCommand.Create(() =>
        {
            if (Uri.TryCreate(Address, UriKind.Absolute, out var uri)) _osInterop.OpenUri(uri);
        });

        // Empty bodies on purpose: the view performs the navigation, these only say when
        // it is allowed to. Chromium reports the two flags through LoadingStateChange.
        CommandGoBack = ReactiveCommand.Create(() => { }, this.WhenAnyValue(vm => vm.CanGoBack));
        CommandGoForward = ReactiveCommand.Create(() => { }, this.WhenAnyValue(vm => vm.CanGoForward));
        CommandReload = ReactiveCommand.Create(() => { });

        // Not tied to activation: the tab header takes its title as soon as the tab is created,
        // and a tab opened in the background would otherwise read "Mod page" until it is shown.
        this.WhenAnyValue(vm => vm.Context)
            .Subscribe(context =>
            {
                if (context?.InitialTitle is not null) TabTitle = context.InitialTitle;
            });

        this.WhenActivated(disposables =>
        {
            this.WhenAnyValue(vm => vm.Context)
                .Where(context => context is not null)
                .Subscribe(context => Address = context!.Uri.ToString())
                .DisposeWith(disposables);

            // Keep the tab header in step with the page.
            this.WhenAnyValue(vm => vm.PageTitle)
                .Where(static title => !string.IsNullOrWhiteSpace(title))
                .Subscribe(title => TabTitle = title)
                .DisposeWith(disposables);

            // A run of downloads loads one page after another into this tab rather than
            // opening a tab per mod, so it has to be reachable for as long as it is open.
            //
            // Only while the download it was opened for is still being waited on, which is
            // what tells this apart from the same tab restored out of a persisted workspace:
            // that one is just a page the user left open, and handing a new run to it would
            // drive a tab that has never been on screen and so has nothing loading in it.
            if (Context is { IsDownloadDriver: true, DownloadRequestId: { } driverRequestId } && _downloadTracker.IsTracked(driverRequestId))
            {
                _downloadTracker.RegisterDriver(this);
                Disposable.Create(this, static self => self._downloadTracker.UnregisterDriver(self)).DisposeWith(disposables);
            }
        });
    }

    /// <summary>
    /// The download and page the script was last handed out for, which the view asks for at
    /// both the start and the end of a load.
    /// </summary>
    private (Guid? RequestId, string Url) _lastArmed;

    public string? TryGetPageLoadScript(string pageUrl)
    {
        if (Context?.AutoStartDownload != true) return null;

        // A tab whose download is no longer being waited on -- one restored from an earlier
        // session, or one left behind when a run moved to a fresh tab -- is an ordinary page now.
        // Shown again, it would otherwise hand its file over a second time.
        if (Context.DownloadRequestId is { } requestId && !_downloadTracker.IsTracked(requestId)) return null;

        // Only the first ask for a page is logged and reported; the second is the same script
        // going in again in case the document it first went into was replaced, and the
        // script itself makes sure it only ever runs once in a document.
        var armed = (Context.DownloadRequestId, pageUrl);
        var isRepeat = armed == _lastArmed;
        _lastArmed = armed;

        // Logged either way: if the page moves itself somewhere without a file in the query
        // -- a download widget on its own URL, say -- the script is never armed there, and
        // without this that would look identical to the script running and finding nothing.
        if (!IsNexusFileDownloadPage(pageUrl))
        {
            if (!isRepeat) _logger.LogInformation("Auto-download not armed, no file in the query: {Url}", pageUrl);
            return null;
        }

        // Only for the file the tab is downloading now: the end of the previous page's load can
        // still be on its way once the tab has been pointed at the next one.
        if (!isRepeat && Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri) && IsCurrentFile(GetFileId(pageUri)))
        {
            _logger.LogInformation("Auto-download armed on {Url}", pageUrl);
            if (Context.DownloadRequestId is { } armedRequestId) _downloadTracker.ReportArmed(armedRequestId);
        }

        // A premium account downloads through the API and never reaches this page, so this
        // is all but always the slow button. Asking anyway keeps the choice correct rather
        // than relying on how the page happens to be reached today -- and sending a
        // non-premium account to the fast button would land them on a purchase page
        // instead of a download.
        return AutoDownloadScript.Build(preferFast: _loginManager.IsPremium);
    }

    /// <summary>
    /// Whether <paramref name="url"/> is the Nexus Mods page for a specific file, which is
    /// the only page with a download button to press.
    /// </summary>
    private static bool IsNexusFileDownloadPage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) return false;

        // Matched on a label boundary so a lookalike domain can't pass.
        var host = uri.Host;
        if (!host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase)) return false;

        return GetFileId(uri) is not null;
    }

    /// <summary>
    /// Ends the wait for this tab's download because the page for file
    /// <paramref name="fileId"/> has none to give.
    /// </summary>
    /// <remarks>
    /// Ignored unless that is the file the tab is currently downloading: a report can still
    /// be on its way from a page the tab has since moved on from.
    /// </remarks>
    public void ReportNothingToDownload(string fileId, string reason)
    {
        if (Context?.DownloadRequestId is not { } requestId) return;
        if (!string.Equals(GetFileId(Context.Uri), fileId, StringComparison.Ordinal)) return;

        _logger.LogInformation("Auto-download gave up on {Url}: {Reason}", Context.Uri, reason);
        _downloadTracker.ReportNothingToDownload(requestId);
    }

    /// <summary>
    /// Whether <paramref name="fileId"/>, reported by a page, is the file the tab is
    /// downloading now. Given the benefit of the doubt when either side doesn't say.
    /// </summary>
    private bool IsCurrentFile(string? fileId)
    {
        var current = Context is null ? null : GetFileId(Context.Uri);
        return current is null || fileId is null || string.Equals(current, fileId, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>file_id</c> in the query of <paramref name="uri"/>, if it has one.
    /// </summary>
    public static string? GetFileId(Uri uri)
    {
        foreach (var parameter in uri.Query.TrimStart('?').Split('&'))
        {
            if (parameter.StartsWith("file_id=", StringComparison.OrdinalIgnoreCase)) return parameter["file_id=".Length..];
        }

        return null;
    }

    /// <summary>
    /// The file an <c>nxm://</c> link such as <c>nxm://game/mods/1/files/2?key=…</c> is for.
    /// </summary>
    private static string? GetHandoffFileId(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var files = Array.IndexOf(segments, "files");
        return files >= 0 && files + 1 < segments.Length ? segments[files + 1] : null;
    }

    public bool TryHandleAppUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        // Everything the embedded browser can render is left to it; only our own
        // protocols are pulled back into the app. This is what makes the download
        // handoff work without bouncing through the OS handler.
        var handler = _protocolHandlers.FirstOrDefault(x => string.Equals(x.Protocol, uri.Scheme, StringComparison.OrdinalIgnoreCase));
        if (handler is null) return false;

        _logger.LogInformation("Handling `{Scheme}` link from the in-app browser", uri.Scheme);

        // A tab with a request id on it was opened by the app for this one file, so the handler
        // is told it asked for this itself. A tab the user is browsing in is not: a link they
        // press there is theirs, and is checked the same way one from their own browser is.
        var source = Context?.DownloadRequestId is null
            ? ProtocolLinkSource.InApp
            : ProtocolLinkSource.AppRequested;

        // Fire and forget: the handler starts a download job and the browser must not block.
        _ = Task.Run(async () =>
        {
            try
            {
                // Said to come from inside the app, so handling it doesn't pull the window
                // in front of whatever the user has gone off to do: a collection downloaded
                // without premium hands one of these over per mod.
                await handler.Handle(url, CancellationToken.None, source);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to handle `{Url}` from the in-app browser", url);
            }
        });

        // A caller that asked for this one download in particular is waiting on it, and the
        // next mod in the collection it is working through doesn't start until this one is
        // away. Reported before the tab closes, since closing is what ends the tab's life.
        //
        // Only for the file that caller asked for. A link from a page the tab has already moved
        // on from is still downloaded, but counting it would move the run past a file whose own
        // page never handed anything over.
        if (Context?.DownloadRequestId is { } downloadRequestId)
        {
            if (IsCurrentFile(GetHandoffFileId(uri))) _downloadTracker.ReportHandoff(downloadRequestId);
            else _logger.LogInformation("`{Url}` isn't the file this tab is downloading, so it doesn't count as its handoff", url);
        }

        // A tab opened by a "Download" button exists only to get the download started, so
        // get it out of the way now that the handoff has been taken. A tab the user opened
        // to browse in stays open: they are still reading the page they downloaded from.
        if (Context?.CloseAfterDownload == true) CloseTab();

        return true;
    }

    /// <summary>
    /// Opens a link the page wanted somewhere other than this tab in a tab of its own, next
    /// to this one in the same panel.
    /// </summary>
    /// <remarks>
    /// Always behind this tab, whatever kind of click it was: the user is still reading this
    /// page, and goes to the new one when they are ready.
    /// </remarks>
    private void OpenLinkInNewTab(string url, bool userGesture)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

                // A download link is the app's to take, wherever the page wanted it opened.
                if (TryHandleAppUri(url)) return;

                if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                    !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Not opening `{Url}` in a new tab, the in-app browser only shows web pages", url);
                    return;
                }

                // A page opening tabs nobody clicked for is almost always a pop-under ad.
                if (!userGesture)
                {
                    _logger.LogInformation("Blocked a new tab the page opened on its own: {Url}", url);
                    return;
                }

                var pageData = new PageData
                {
                    FactoryId = BrowserPageFactory.StaticId,
                    Context = new BrowserPageContext
                    {
                        Uri = uri,
                        // Until the page reports its own.
                        InitialTitle = uri.Host + uri.AbsolutePath.TrimEnd('/'),
                    },
                };

                var workspaceController = GetWorkspaceController();
                workspaceController.OpenPage(WorkspaceId, pageData, new OpenPageBehavior.NewTab(PanelId), selectTab: false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Unable to open `{Url}` in a new tab", url);
            }
        });
    }

    /// <inheritdoc/>
    public void DriveDownload(Uri uri, string? title, Guid requestId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Context is null) return;

            var isSamePage = string.Equals(Address, uri.ToString(), StringComparison.Ordinal);

            // The request id is what the handoff is reported back under, so the context has
            // to carry the new one before the page gets a chance to load. Assigning it is
            // also what moves `Address` on, which is what the view navigates on.
            Context = Context with { Uri = uri, InitialTitle = title, DownloadRequestId = requestId };

            // The view only navigates when the address actually changes, so a run that comes
            // back round to a page it has already been on has to be told to load it again.
            if (isSamePage) CommandReload.Execute(Unit.Default).Subscribe();
        });
    }

    /// <inheritdoc/>
    public void CloseDriverTab() => CloseTab();

    /// <inheritdoc/>
    public void BringToFront()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var workspaceController = GetWorkspaceController();
                if (!workspaceController.TryGetWorkspace(WorkspaceId, out var workspace)) return;

                if (workspaceController.ActiveWorkspaceId != WorkspaceId) workspaceController.ChangeActiveWorkspace(WorkspaceId);
                workspace.Panels.FirstOrDefault(panel => panel.Id == PanelId)?.SelectTab(TabId);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Unable to bring the download tab to the front");
            }
        });
    }

    private void CloseTab()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var workspaceController = GetWorkspaceController();
                if (!workspaceController.TryGetWorkspace(WorkspaceId, out var workspace)) return;

                var panel = workspace.Panels.FirstOrDefault(panel => panel.Id == PanelId);
                panel?.CloseTab(TabId);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Unable to close the in-app browser tab");
            }
        });
    }
}
