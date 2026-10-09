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
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _pending = new();

    /// <summary>
    /// The tab the current run of downloads is being driven through, if one is open.
    /// </summary>
    private volatile IBrowserDownloadDriver? _driver;

    /// <summary>
    /// Starts tracking a download about to be opened, returning the id to send out with it.
    /// </summary>
    public Guid Add()
    {
        var requestId = Guid.NewGuid();

        // Asynchronous continuations: the result is set from the UI thread, and the waiter
        // must not be resumed on it.
        _pending[requestId] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return requestId;
    }

    /// <summary>
    /// Stops tracking <paramref name="requestId"/>, whether or not it ever reported back.
    /// </summary>
    public void Remove(Guid requestId) => _pending.TryRemove(requestId, out _);

    /// <summary>
    /// Waits for the tab to hand its download to the app.
    /// </summary>
    /// <returns>False when <paramref name="timeout"/> ran out first.</returns>
    public async Task<bool> WaitForHandoff(Guid requestId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_pending.TryGetValue(requestId, out var pending)) return false;

        try
        {
            return await pending.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reports that the tab opened for <paramref name="requestId"/> has handed its download
    /// to the app. Ignored for an id nothing is waiting on, which is what a tab restored
    /// from a persisted workspace carries.
    /// </summary>
    public void ReportHandoff(Guid requestId)
    {
        if (_pending.TryGetValue(requestId, out var pending)) pending.TrySetResult(true);
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
