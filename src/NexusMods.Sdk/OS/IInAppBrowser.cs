using JetBrains.Annotations;

namespace NexusMods.Sdk;

/// <summary>
/// Opens web pages inside the app instead of handing them to the system browser.
/// </summary>
/// <remarks>
/// Implemented by the UI, which has the tab system. Code in lower layers resolves this
/// optionally and falls back to <see cref="IOSInterop.OpenUri"/> when it isn't available,
/// so a headless host keeps working.
/// </remarks>
[PublicAPI]
public interface IInAppBrowser
{
    /// <summary>
    /// Tries to open <paramref name="uri"/> in a tab.
    /// </summary>
    /// <returns>False when the page could not be opened, in which case the caller should fall back.</returns>
    bool TryOpen(Uri uri, string? title = null);
}
