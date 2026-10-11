using JetBrains.Annotations;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// A game that ships optional content of its own which can be turned off without uninstalling it.
/// </summary>
/// <remarks>
/// This is for content that belongs to the game installation - DLC the store delivered - not for
/// mods. Mods are loadout items and carry their own enabled state.
/// </remarks>
[PublicAPI]
public interface IGameWithDlc
{
    /// <summary>
    /// The optional content this game knows about, in the order it should be shown to the user.
    /// </summary>
    /// <remarks>
    /// The list describes what the game <i>can</i> have, not what is installed; entries whose
    /// plugin isn't present in the game folder are filtered out where they are displayed.
    /// </remarks>
    IReadOnlyList<GameDlc> Dlc { get; }
}

/// <summary>
/// One piece of the game's own optional content, identified by the plugin that activates it.
/// </summary>
[PublicAPI]
public record GameDlc
{
    /// <summary>
    /// Stable identifier, stored in the data model when the user switches this content off.
    /// Never change it for existing content, or loadouts will forget that it was switched off.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The name to show the user, e.g. "Far Harbor".
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The plugin that loads the content, e.g. <c>Data/DLCCoast.esm</c>. Turning the content off
    /// means leaving this plugin out of the generated plugins file; the game then loads neither it
    /// nor the archives named after it. The path also tells the UI whether the content is installed
    /// at all.
    /// </summary>
    public required GamePath Plugin { get; init; }
}
