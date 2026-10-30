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
    /// The least time between one download page being asked for and the next, to begin with.
    /// </summary>
    /// <remarks>
    /// None at the moment, for testing, which also means a suspension has nothing to double.
    /// A page hands its link over within moments of starting to load, so a collection is paged
    /// through as fast as Nexus Mods can answer, and it answers that by suspending the address:
    /// at half a second a page, that took 214 pages in a little over two minutes. The limit
    /// isn't published. Two seconds is the pace of an earlier run of 319 pages that wasn't
    /// suspended. What a pace costs depends on the files: large ones can't keep up with the
    /// pages anyway, while a stretch of small ones goes no faster than it.
    /// </remarks>
    private static readonly TimeSpan MinimumPageInterval = TimeSpan.Zero;

    /// <summary>
    /// The most <see cref="_pageInterval"/> grows to, however many suspensions there are.
    /// </summary>
    private static readonly TimeSpan MaximumPageInterval = TimeSpan.FromSeconds(16);

    /// <summary>
    /// How long a suspension is taken to last when the notice doesn't say. The one seen so far
    /// asked for this long.
    /// </summary>
    private static readonly TimeSpan AssumedSuspension = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How much longer than a suspension is asked to last the run stays away for: a request
    /// that lands just before it lifts gains nothing, and may count against the address.
    /// </summary>
    private static readonly TimeSpan SuspensionMargin = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When the last download page was asked for. Only touched while holding <see cref="_downloadQueue"/>.
    /// </summary>
    private long _lastPageRequested;

    /// <summary>
    /// The least time between download pages as of now. Doubled by each suspension and kept for
    /// as long as the app runs: since the limit isn't published, being suspended is the only
    /// sign of having gone too fast. Only touched while holding <see cref="_downloadQueue"/>.
    /// </summary>
    private TimeSpan _pageInterval = MinimumPageInterval;

    /// <summary>
    /// When a run of downloads may next ask for a page. Outlives the run, since pressing
    /// Download again doesn't lift a suspension. Only touched while holding <see cref="_downloadQueue"/>.
    /// </summary>
    private DateTimeOffset _suspendedUntil;

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
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Holds the queue to one download at a time; see <see cref="IInAppBrowser.StartDownload"/>.
    /// </summary>
    private readonly SemaphoreSlim _downloadQueue = new(initialCount: 1, maxCount: 1);

    /// <summary>
    /// How many <see cref="BeginDownloadSession"/> calls are currently open. While this is
    /// above zero the downloads share one tab; see <see cref="IInAppBrowser.BeginDownloadSession"/>.
    /// </summary>
    private int _sessionDepth;

    public InAppBrowser(ILogger<InAppBrowser> logger, IWindowManager windowManager, BrowserDownloadTracker downloadTracker, TimeProvider timeProvider)
    {
        _logger = logger;
        _windowManager = windowManager;
        _downloadTracker = downloadTracker;
        _timeProvider = timeProvider;
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

            while (true)
            {
                await WaitForPageTurn(inSession, cancellationToken);

                var outcome = await AskPageForDownload(uri, title, inSession, cancellationToken);
                if (outcome is not null) return outcome.Value;
            }
        }
        finally
        {
            _downloadQueue.Release();
        }
    }

    /// <summary>
    /// Waits until the next download page may be asked for.
    /// </summary>
    /// <remarks>
    /// Only a run of downloads waits out a suspension, as it is what would otherwise keep
    /// asking. A single download is someone pressing a button, who is better off being shown
    /// the notice than having nothing happen for ten minutes.
    /// </remarks>
    private async ValueTask WaitForPageTurn(bool inSession, CancellationToken cancellationToken)
    {
        var suspended = _suspendedUntil - _timeProvider.GetUtcNow();
        if (inSession && suspended > TimeSpan.Zero)
            await Task.Delay(suspended, _timeProvider, cancellationToken);

        var sinceLastPage = _timeProvider.GetElapsedTime(_lastPageRequested);
        if (_lastPageRequested != 0 && sinceLastPage < _pageInterval)
            await Task.Delay(_pageInterval - sinceLastPage, _timeProvider, cancellationToken);
    }

    /// <summary>
    /// Loads the download page and waits for it to hand its download over.
    /// </summary>
    /// <returns>Null when the page has to be tried again, once the wait for the next page is over.</returns>
    private async ValueTask<InAppDownloadOutcome?> AskPageForDownload(Uri uri, string? title, bool inSession, CancellationToken cancellationToken)
    {
        var requestId = _downloadTracker.Add();

        try
        {
            _lastPageRequested = _timeProvider.GetTimestamp();

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

                _lastPageRequested = _timeProvider.GetTimestamp();

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
                    if (!inSession) await Task.Delay(SettleDelay, _timeProvider, cancellationToken);
                    return InAppDownloadOutcome.Started;
                case HandoffResult.Suspended:
                    NoteSuspension(_downloadTracker.GetRetryAfter(requestId));

                    // The file isn't to blame, so a run tries it again rather than counting it as
                    // failed. A tab of its own stays on the notice, for the user to read.
                    return inSession ? null : InAppDownloadOutcome.NotStarted;
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

    /// <summary>
    /// Holds off asking for pages for as long as Nexus Mods asked, and slows down afterwards.
    /// </summary>
    private void NoteSuspension(TimeSpan? retryAfter)
    {
        _suspendedUntil = _timeProvider.GetUtcNow() + (retryAfter ?? AssumedSuspension) + SuspensionMargin;

        var doubled = _pageInterval * 2;
        _pageInterval = doubled < MaximumPageInterval ? doubled : MaximumPageInterval;

        _logger.LogWarning("Nexus Mods has suspended this address for sending too many requests. A run of downloads won't ask for another page until {Until}, then one every {Interval}", _suspendedUntil.ToLocalTime(), _pageInterval);

        _downloadTracker.Driver?.ShowPausedUntil(_suspendedUntil);
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
            // The tab is already open and on screen, so it simply takes the next page.
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
