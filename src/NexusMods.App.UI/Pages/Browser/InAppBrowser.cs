using System.Diagnostics;
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
    /// How long a download page has to start loading, which is when the script goes on it,
    /// before it is given up on as stuck.
    /// </summary>
    /// <remarks>
    /// A page normally gets there in well under a second. One that hasn't after this long is
    /// stuck, and waiting out <see cref="HandoffTimeout"/> on it as well would only hold up
    /// every download queued behind it.
    /// </remarks>
    private static readonly TimeSpan ArmingTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long one download page is given to hand its download over once the script is on
    /// it, before the queue moves on.
    /// </summary>
    /// <remarks>
    /// The injected script spends up to 30 seconds looking for the link or the button, and
    /// says so when it gives up; the rest is room for the countdown a free download sits
    /// behind, if the button had to be pressed.
    /// </remarks>
    private static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Pause after a download whose tab was its own, while that tab closes itself.
    /// </summary>
    /// <remarks>
    /// Not needed in a session, where the next download goes into the same tab.
    /// </remarks>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The least time between one download page being asked for and the next.
    /// </summary>
    /// <remarks>
    /// A page hands its link over within moments of starting to load, so without this a
    /// collection would be paged through as fast as Nexus Mods can answer. This keeps it to a
    /// pace that is still several times what it used to be.
    /// </remarks>
    private static readonly TimeSpan MinimumPageInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// When the last download page was asked for. Only touched while holding <see cref="_downloadQueue"/>.
    /// </summary>
    private long _lastPageRequested;

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
                var sinceLastPage = Stopwatch.GetElapsedTime(_lastPageRequested);
                if (_lastPageRequested != 0 && sinceLastPage < MinimumPageInterval)
                    await Task.Delay(MinimumPageInterval - sinceLastPage, cancellationToken);

                _lastPageRequested = Stopwatch.GetTimestamp();

                if (!await PointTabAtDownload(uri, title, requestId, inSession, cancellationToken))
                    return InAppDownloadOutcome.Unavailable;

                var result = await _downloadTracker.WaitForHandoff(requestId, ArmingTimeout, HandoffTimeout, cancellationToken);

                // Chromium only loads pages in a tab that is on screen, so a page that hasn't even
                // started loading is most often one whose tab the user has moved away from: every
                // page after it would wait out the same timeout. The tab is shown again -- or a
                // fresh one opened, if it has gone -- and the page gets one more go.
                if (result is HandoffResult.NeverArmed && inSession)
                {
                    _logger.LogInformation("The page at `{Uri}` didn't start loading within {Timeout}; bringing the download tab back to the front to try it again", uri, ArmingTimeout);

                    if (_downloadTracker.Driver is { } driver)
                    {
                        driver.BringToFront();
                        driver.DriveDownload(uri, title, requestId);
                    }
                    else if (!await PointTabAtDownload(uri, title, requestId, inSession, cancellationToken))
                    {
                        return InAppDownloadOutcome.Unavailable;
                    }

                    result = await _downloadTracker.WaitForHandoff(requestId, ArmingTimeout, HandoffTimeout, cancellationToken);
                }

                switch (result)
                {
                    case HandoffResult.HandedOff:
                        // A tab of its own closes itself now that its download is away; one in
                        // a session is about to be pointed at the next page instead.
                        if (!inSession) await Task.Delay(SettleDelay, cancellationToken);
                        return InAppDownloadOutcome.Started;
                    case HandoffResult.NothingToDownload:
                        _logger.LogWarning("The page at `{Uri}` has no download to hand over", uri);
                        return InAppDownloadOutcome.NotStarted;
                    case HandoffResult.NeverArmed:
                        _logger.LogWarning("The page at `{Uri}` didn't start loading within {Timeout}", uri, ArmingTimeout);
                        return InAppDownloadOutcome.NotStarted;
                    default:
                        _logger.LogWarning("No download arrived from `{Uri}` within {Timeout}", uri, HandoffTimeout);
                        return InAppDownloadOutcome.NotStarted;
                }
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
