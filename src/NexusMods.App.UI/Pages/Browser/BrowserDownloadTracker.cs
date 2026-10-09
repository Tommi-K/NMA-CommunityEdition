using System.Collections.Concurrent;
using JetBrains.Annotations;

namespace NexusMods.App.UI.Pages.Browser;

/// <summary>
/// Pairs a tab opened to start a download with whoever is waiting for that download.
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
}
