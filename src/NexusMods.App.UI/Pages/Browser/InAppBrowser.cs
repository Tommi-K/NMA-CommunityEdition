using Avalonia.Threading;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using DynamicData.Kernel;
using NexusMods.Sdk;

namespace NexusMods.App.UI.Pages.Browser;

/// <summary>
/// Opens web pages as tabs in the active workspace.
/// </summary>
[UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
internal sealed class InAppBrowser : IInAppBrowser
{
    private readonly ILogger<InAppBrowser> _logger;
    private readonly IWindowManager _windowManager;

    public InAppBrowser(ILogger<InAppBrowser> logger, IWindowManager windowManager)
    {
        _logger = logger;
        _windowManager = windowManager;
    }

    public bool TryOpen(Uri uri, string? title = null)
    {
        try
        {
            return Dispatcher.UIThread.Invoke(() =>
            {
                var workspaceController = _windowManager.ActiveWorkspaceController;

                var pageData = new PageData
                {
                    FactoryId = BrowserPageFactory.StaticId,
                    Context = new BrowserPageContext
                    {
                        Uri = uri,
                        InitialTitle = title,
                    },
                };

                workspaceController.OpenPage(
                    workspaceController.ActiveWorkspaceId,
                    pageData,
                    new OpenPageBehavior(new OpenPageBehavior.NewTab(Optional<PanelId>.None)),
                    selectTab: true
                );

                return true;
            });
        }
        catch (Exception e)
        {
            // Caller falls back to the system browser.
            _logger.LogWarning(e, "Unable to open `{Uri}` in an app tab", uri);
            return false;
        }
    }
}
