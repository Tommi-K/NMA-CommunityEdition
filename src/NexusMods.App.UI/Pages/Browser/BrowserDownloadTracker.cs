using System.Collections.Concurrent;
using JetBrains.Annotations;

namespace NexusMods.App.UI.Pages.Browser;

/// <summary>
/// A tab that download pages are driven through, one after another.
/// </summary>
/// <remarks>
/// Implemented by the browser page's view model. Only the tab itself can navigate its
/// Chromium instance, so a run of downloads that wants to reuse one tab has to reach it
/// through something like this.
/// </remarks>
internal interface IBrowserDownloadDriver
{
    /// <summary>
    /// Points the tab at the next download page.
    /// </summary>
    /// <param name="uri">The page to load.</param>
    /// <param name="title">Tab title to use until the page reports its own.</param>
    /// <param name="requestId">The download this page is being loaded for.</param>
    void DriveDownload(Uri uri, string? title, Guid requestId);

    /// <summary>
    /// Closes the tab.
    /// </summary>
    void CloseDriverTab();

    /// <summary>
    /// Shows the tab again, switching to its workspace if need be.
    /// </summary>
    /// <remarks>
    /// Chromium only loads pages in a tab that is on screen, so a run whose tab the user has
    /// moved away from stops getting anywhere until it is shown again.
    /// </remarks>
    void BringToFront();
}

/// <summary>
/// How waiting for a download page to hand its download over ended.
/// </summary>
internal enum HandoffResult
{
    /// <summary>
    /// The page handed its download to the app.
    /// </summary>
    HandedOff,

    /// <summary>
    /// The page loaded, but has no download to give: the script looked for one until its
    /// deadline, or the page itself came back as missing.
    /// </summary>
    NothingToDownload,

    /// <summary>
    /// The page never got far enough for the script to be put on it.
    /// </summary>
    NeverArmed,

    /// <summary>
    /// The script was put on the page, but nothing came of it in time.
    /// </summary>
    TimedOut,
}

/// <summary>
/// Pairs a tab opened to start a download with whoever is waiting for that download, and
/// holds the tab a run of downloads is being driven through.
/// </summary>
/// <remarks>
/// A tab is opened through the workspace system, which hands back no view model, so there
/// is no object to await. What there is instead is the request id that travels out in the
/// tab's <see cref="BrowserPageContext"/> and comes back here when that tab takes a
/// <c>nxm://</c> handoff.
/// </remarks>
[UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
internal sealed class BrowserDownloadTracker
{
    private readonly ConcurrentDictionary<Guid, PendingDownload> _pending = new();

    /// <summary>
    /// The tab the current run of downloads is being driven through, if one is open.
    /// </summary>
    private volatile IBrowserDownloadDriver? _driver;

    /// <summary>
    /// A download being waited on: whether its page has been armed yet, and how it ended.
    /// </summary>
    /// <remarks>
    /// Asynchronous continuations: both are set from the UI thread, and the waiter must not be
    /// resumed on it.
    /// </remarks>
    private sealed class PendingDownload
    {
        public readonly TaskCompletionSource Armed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<HandoffResult> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Starts tracking a download about to be opened, returning the id to send out with it.
    /// </summary>
    public Guid Add()
    {
        var requestId = Guid.NewGuid();
        _pending[requestId] = new PendingDownload();
        return requestId;
    }

    /// <summary>
    /// Stops tracking <paramref name="requestId"/>, whether or not it ever reported back.
    /// </summary>
    public void Remove(Guid requestId) => _pending.TryRemove(requestId, out _);

    /// <summary>
    /// Waits for the tab to hand its download to the app.
    /// </summary>
    /// <param name="requestId">The download.</param>
    /// <param name="armingTimeout">
    /// How long the page has to get far enough to be armed. A page that hasn't by then is
    /// stuck, and waiting the full <paramref name="handoffTimeout"/> on it gains nothing.
    /// </param>
    /// <param name="handoffTimeout">How long the armed page has to hand its download over.</param>
    /// <param name="cancellationToken">Gives up waiting.</param>
    public async Task<HandoffResult> WaitForHandoff(Guid requestId, TimeSpan armingTimeout, TimeSpan handoffTimeout, CancellationToken cancellationToken)
    {
        if (!_pending.TryGetValue(requestId, out var pending)) return HandoffResult.NeverArmed;

        try
        {
            await Task.WhenAny(pending.Armed.Task, pending.Result.Task).WaitAsync(armingTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return HandoffResult.NeverArmed;
        }

        try
        {
            return await pending.Result.Task.WaitAsync(handoffTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return HandoffResult.TimedOut;
        }
    }

    /// <summary>
    /// Reports that the script has been put on the page loading for <paramref name="requestId"/>.
    /// </summary>
    public void ReportArmed(Guid requestId)
    {
        if (_pending.TryGetValue(requestId, out var pending)) pending.Armed.TrySetResult();
    }

    /// <summary>
    /// Reports that the tab opened for <paramref name="requestId"/> has handed its download
    /// to the app. Ignored for an id nothing is waiting on, which is what a tab restored
    /// from a persisted workspace carries.
    /// </summary>
    public void ReportHandoff(Guid requestId)
    {
        if (_pending.TryGetValue(requestId, out var pending)) pending.Result.TrySetResult(HandoffResult.HandedOff);
    }

    /// <summary>
    /// Reports that the page loaded for <paramref name="requestId"/> has no download to hand
    /// over, so the wait can end now.
    /// </summary>
    public void ReportNothingToDownload(Guid requestId)
    {
        if (_pending.TryGetValue(requestId, out var pending)) pending.Result.TrySetResult(HandoffResult.NothingToDownload);
    }

    /// <summary>
    /// Whether <paramref name="requestId"/> is a download something is waiting on right now.
    /// </summary>
    /// <remarks>
    /// False for the id a tab restored from a persisted workspace carries, which is how such
    /// a tab is told apart from one this run has just opened.
    /// </remarks>
    public bool IsTracked(Guid requestId) => _pending.ContainsKey(requestId);

    /// <summary>
    /// Offers <paramref name="driver"/> as the tab for the current run of downloads.
    /// </summary>
    /// <remarks>
    /// Only the first tab to offer itself is kept. A workspace restored from disk can bring
    /// back a driver tab from a previous run, and the user is free to open more; whichever
    /// got there first is the one the run keeps using, and the rest are ordinary tabs.
    /// </remarks>
    public void RegisterDriver(IBrowserDownloadDriver driver) => Interlocked.CompareExchange(ref _driver, driver, null);

    /// <summary>
    /// Gives up <paramref name="driver"/>'s place as the tab for the current run, if it held
    /// one. Called when its tab closes, however that came about.
    /// </summary>
    public void UnregisterDriver(IBrowserDownloadDriver driver) => Interlocked.CompareExchange(ref _driver, null, driver);

    /// <summary>
    /// The tab the current run of downloads is being driven through, or null when the next
    /// download will have to open one.
    /// </summary>
    public IBrowserDownloadDriver? Driver => _driver;
}
