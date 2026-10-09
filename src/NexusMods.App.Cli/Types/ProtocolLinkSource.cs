namespace NexusMods.CLI.Types;

/// <summary>
/// Where a protocol link being handled came from.
/// </summary>
/// <remarks>
/// What this decides is whether handling the link should pull the app's window in front of
/// whatever the user is doing. A link arriving from outside is the user asking for the app,
/// so the app shows itself. A link the app produced for itself is not, and a collection
/// being auto-downloaded produces one per mod.
/// </remarks>
public enum ProtocolLinkSource
{
    /// <summary>
    /// Handed to the app from outside it, such as a "Mod Manager Download" link the user
    /// clicked on the website in their own browser.
    /// </summary>
    External = 0,

    /// <summary>
    /// Produced by the app itself, such as the in-app browser working through a collection's
    /// downloads.
    /// </summary>
    InApp = 1,
}
