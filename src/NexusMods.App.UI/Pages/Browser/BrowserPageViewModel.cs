using Avalonia.Threading;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.CLI.Types;
using NexusMods.Sdk;
using NexusMods.UI.Sdk.Icons;
using Xilium.CefGlue.Common.Handlers;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Pages.Browser;

[UsedImplicitly]
public class BrowserPageViewModel : APageViewModel<IBrowserPageViewModel>, IBrowserPageViewModel
{
    private readonly ILogger<BrowserPageViewModel> _logger;
    private readonly IOSInterop _osInterop;
    private readonly IIpcProtocolHandler[] _protocolHandlers;

    [Reactive] public BrowserPageContext? Context { get; set; }
    [Reactive] public string Address { get; set; } = "about:blank";
    [Reactive] public string PageTitle { get; set; } = string.Empty;

    public ReactiveCommand<Unit, Unit> CommandOpenInSystemBrowser { get; }

    public RequestHandler BrowserRequestHandler { get; }

    public BrowserPageViewModel(
        ILogger<BrowserPageViewModel> logger,
        IWindowManager windowManager,
        IOSInterop osInterop,
        IServiceProvider serviceProvider) : base(windowManager)
    {
        _logger = logger;
        _osInterop = osInterop;
        _protocolHandlers = serviceProvider.GetServices<IIpcProtocolHandler>().ToArray();

        // One handler per tab: CefGlue disposes it along with the browser it is attached
        // to, so it can't be shared. The rules behind it are a shared singleton.
        BrowserRequestHandler = new AdBlockRequestHandler(serviceProvider.GetRequiredService<AdBlocker>(), logger);

        TabTitle = "Mod page";
        TabIcon = IconValues.Nexus;

        CommandOpenInSystemBrowser = ReactiveCommand.Create(() =>
        {
            if (Uri.TryCreate(Address, UriKind.Absolute, out var uri)) _osInterop.OpenUri(uri);
        });

        this.WhenActivated(disposables =>
        {
            this.WhenAnyValue(vm => vm.Context)
                .Where(context => context is not null)
                .Subscribe(context =>
                {
                    Address = context!.Uri.ToString();
                    if (context.InitialTitle is not null) TabTitle = context.InitialTitle;
                })
                .DisposeWith(disposables);

            // Keep the tab header in step with the page.
            this.WhenAnyValue(vm => vm.PageTitle)
                .Where(static title => !string.IsNullOrWhiteSpace(title))
                .Subscribe(title => TabTitle = title)
                .DisposeWith(disposables);
        });
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

        // Fire and forget: the handler starts a download job and the browser must not block.
        _ = Task.Run(async () =>
        {
            try
            {
                await handler.Handle(url, CancellationToken.None);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to handle `{Url}` from the in-app browser", url);
            }
        });

        // A tab opened by a "Download" button exists only to get the download started, so
        // get it out of the way now that the handoff has been taken. A tab the user opened
        // to browse in stays open: they are still reading the page they downloaded from.
        if (Context?.CloseAfterDownload == true) CloseTab();

        return true;
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
