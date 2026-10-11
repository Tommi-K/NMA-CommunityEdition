namespace NexusMods.CLI.Types;

/// <summary>
/// Where a protocol link being handled came from.
/// </summary>
/// <remarks>
/// This decides whether handling the link should pull the app's window in front of whatever
/// the user is doing: a link arriving from outside is the user asking for the app, so the app
/// shows itself, while a link the app produced for itself is not, and a collection being
/// auto-downloaded produces one per mod. It also decides how much the handler has to check
/// for itself; see <see cref="AppRequested"/>.
/// </remarks>
public enum ProtocolLinkSource
{
    /// <summary>
    /// Handed to the app from outside it, such as a "Mod Manager Download" link the user
    /// clicked on the website in their own browser.
    /// </summary>
    External = 0,

    /// <summary>
    /// Produced by the app itself, such as the user pressing a "Mod Manager Download" button
    /// on a page they are browsing in the in-app browser.
    /// </summary>
    InApp = 1,

    /// <summary>
    /// Produced for one particular file the app went and asked for, such as each mod of a
    /// collection being downloaded through the in-app browser.
    /// </summary>
    /// <remarks>
    /// Handling one of these does not check that the file's game is one the app manages: the
    /// app picked the file itself and already knows what it is for, and a collection can list
    /// a file hosted under a domain that is not a game at all, such as <c>site</c>.
    /// </remarks>
    AppRequested = 2,
}
