using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;

namespace NexusMods.Abstractions.Loadouts;

/// <summary>
/// A file that belongs to the game installation itself, recorded locally when the
/// file hashes DB has no manifest for this build.
/// </summary>
/// <remarks>
/// The hashes DB answers "which files shipped with this game?" via the store's
/// manifests, and the synchronizer relies on that answer to tell the game's own
/// files apart from the user's. With no manifest, every file in the folder looks
/// like an unknown file the app has to take ownership of, which means archiving
/// the entire game install.
///
/// For those games we fall back to the next best definition: the folder as it was
/// when the app first indexed it. These entries are a snapshot of that initial
/// disk state and are only consulted when the hashes DB yields nothing for the
/// loadout, so real manifests always win.
/// </remarks>
public partial class GameBaselineFile : IModelDefinition
{
    /// <summary>
    /// Put entries in a user partition so they are all grouped together
    /// </summary>
    public static readonly PartitionId EntryPartition = PartitionId.User(5);

    private const string Namespace = "NexusMods.Loadouts.GameBaselineFile";

    /// <summary>
    /// The path to the file. Always in <see cref="LocationId.Game"/>, as that is the
    /// only location the game layer of the sync tree describes.
    /// </summary>
    public static readonly GamePathParentAttribute Path = new(Namespace, nameof(Path));

    /// <summary>
    /// The hash of the file as it was when the game folder was first indexed.
    /// </summary>
    public static readonly HashAttribute Hash = new(Namespace, nameof(Hash));

    /// <summary>
    /// The size of the file (in bytes)
    /// </summary>
    public static readonly SizeAttribute Size = new(Namespace, nameof(Size));

    /// <summary>
    /// The owning game installation
    /// </summary>
    public static readonly ReferenceAttribute<Sdk.Games.GameInstallMetadata> Game = new(Namespace, nameof(Game));
}
