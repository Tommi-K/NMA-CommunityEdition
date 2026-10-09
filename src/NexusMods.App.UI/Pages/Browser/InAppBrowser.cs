using System.Reactive.Disposables;
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

    /// <summary>
    /// How long a freshly opened driver tab is given to report itself in before the download
    /// after it gives up on reusing it and opens one of its own.
    /// </summary>
    /// <remarks>
    /// The tab registers itself when its view activates, which happens on the UI thread
    /// after the workspace has built it, so it is never ready the instant it is asked for.
    /// </remarks>
    private static readonly TimeSpan DriverRegistrationTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<InAppBrowser> _logger;
    private readonly IWindowManager _windowManager;
    private readonly BrowserDownloadTracker _downloadTracker;

    /// <summary>
    /// Holds the queue to one download at a time; see <see cref="IInAppBrowser.StartDownload"/>.
    /// </summary>
    private readonly SemaphoreSlim _downloadQueue = new(initialCount: 1, maxCount: 1);

    /// <summary>
    /// How many <see cref="BeginDownloadSession"/> calls are currently open. While this is
    /// above zero the downloads share one tab; see <see cref="IInAppBrowser.BeginDownloadSession"/>.
    /// </summary>
    private int _sessionDepth;

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

    public IDisposable BeginDownloadSession()
    {
        Interlocked.Increment(ref _sessionDepth);

        return Disposable.Create(this, static self =>
        {
            if (Interlocked.Decrement(ref self._sessionDepth) > 0) return;

            // The tab was kept open across the run's downloads, so this is what gets rid of
            // it. A tab the run never managed to open leaves nothing to close.
            self._downloadTracker.Driver?.CloseDriverTab();
        });
    }

    public async ValueTask<InAppDownloadOutcome> StartDownload(Uri uri, string? title = null, CancellationToken cancellationToken = default)
    {
        await _downloadQueue.WaitAsync(cancellationToken);

        try
        {
            var inSession = Volatile.Read(ref _sessionDepth) > 0;
            var requestId = _downloadTracker.Add();

            try
            {
                if (!await PointTabAtDownload(uri, title, requestId, inSession, cancellationToken))
                    return InAppDownloadOutcome.Unavailable;

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

    /// <summary>
    /// Gets <paramref name="uri"/> loading in a tab, reusing the run's tab where there is one.
    /// </summary>
    /// <returns>False when no tab could be opened, so the caller can fall back.</returns>
    private async ValueTask<bool> PointTabAtDownload(Uri uri, string? title, Guid requestId, bool inSession, CancellationToken cancellationToken)
    {
        if (!inSession)
        {
            // On its own, so it gets a tab that closes itself once the handoff lands.
            return TryOpenTab(uri, title, closeAfterDownload: true, autoStartDownload: true, downloadRequestId: requestId, isDownloadDriver: false);
        }

        var driver = _downloadTracker.Driver;
        if (driver is not null)
        {
            // The tab is already open and on screen, so the page is simply loaded into it.
            // Not selecting it again is the point: the user is free to look at something
            // else while the run works through the collection.
            driver.DriveDownload(uri, title, requestId);
            return true;
        }

        // First download of the run. Its tab is brought to the front once, because Chromium
        // only starts loading a page in a tab that has been shown, and then stays put for
        // the rest of the run.
        if (!TryOpenTab(uri, title, closeAfterDownload: false, autoStartDownload: true, downloadRequestId: requestId, isDownloadDriver: true))
            return false;

        await WaitForDriverRegistration(cancellationToken);
        return true;
    }

    /// <summary>
    /// Waits for a newly opened driver tab to report itself, so the next download can find it.
    /// </summary>
    /// <remarks>
    /// Giving up is not a failure: the download that opened the tab is already loading in it,
    /// and the next one opens a tab of its own rather than waiting any longer.
    /// </remarks>
    private async ValueTask WaitForDriverRegistration(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + DriverRegistrationTimeout;

        while (_downloadTracker.Driver is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        if (_downloadTracker.Driver is null)
            _logger.LogWarning("The download tab did not report itself within {Timeout}; the downloads after this one will each open their own", DriverRegistrationTimeout);
    }

    private bool TryOpenTab(Uri uri, string? title, bool closeAfterDownload, bool autoStartDownload, Guid? downloadRequestId, bool isDownloadDriver = false)
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
                        IsDownloadDriver = isDownloadDriver,
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
