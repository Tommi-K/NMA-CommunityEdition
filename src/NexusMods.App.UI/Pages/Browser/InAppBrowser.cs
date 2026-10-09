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
    /// <summary>
    /// How long one download page is given to hand its download over before the queue moves on.
    /// </summary>
    /// <remarks>
    /// The injected script spends up to 30 seconds looking for the button; the rest is room
    /// for the page to load and for the countdown a free download sits behind.
    /// </remarks>
    private static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Pause between one download and the next, while the tab that just handed its download
    /// over is still closing itself and the handler it started is getting going.
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    private readonly ILogger<InAppBrowser> _logger;
    private readonly IWindowManager _windowManager;
    private readonly BrowserDownloadTracker _downloadTracker;

    /// <summary>
    /// Holds the queue to one download at a time; see <see cref="IInAppBrowser.StartDownload"/>.
    /// </summary>
    private readonly SemaphoreSlim _downloadQueue = new(initialCount: 1, maxCount: 1);

    public InAppBrowser(ILogger<InAppBrowser> logger, IWindowManager windowManager, BrowserDownloadTracker downloadTracker)
    {
        _logger = logger;
        _windowManager = windowManager;
        _downloadTracker = downloadTracker;
    }

    public bool TryOpen(Uri uri, string? title = null, bool closeAfterDownload = false, bool autoStartDownload = false)
    {
        return TryOpenTab(uri, title, closeAfterDownload, autoStartDownload, downloadRequestId: null);
    }

    public async ValueTask<InAppDownloadOutcome> StartDownload(Uri uri, string? title = null, CancellationToken cancellationToken = default)
    {
        await _downloadQueue.WaitAsync(cancellationToken);

        try
        {
            var requestId = _downloadTracker.Add();

            try
            {
                var opened = TryOpenTab(uri, title, closeAfterDownload: true, autoStartDownload: true, downloadRequestId: requestId);
                if (!opened) return InAppDownloadOutcome.Unavailable;

                var started = await _downloadTracker.WaitForHandoff(requestId, HandoffTimeout, cancellationToken);
                if (!started)
                {
                    _logger.LogWarning("No download arrived from `{Uri}` within {Timeout}", uri, HandoffTimeout);
                    return InAppDownloadOutcome.NotStarted;
                }

                await Task.Delay(SettleDelay, cancellationToken);
                return InAppDownloadOutcome.Started;
            }
            finally
            {
                _downloadTracker.Remove(requestId);
            }
        }
        finally
        {
            _downloadQueue.Release();
        }
    }

    private bool TryOpenTab(Uri uri, string? title, bool closeAfterDownload, bool autoStartDownload, Guid? downloadRequestId)
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
                        CloseAfterDownload = closeAfterDownload,
                        AutoStartDownload = autoStartDownload,
                        DownloadRequestId = downloadRequestId,
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
