using JetBrains.Annotations;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;

namespace NexusMods.Abstractions.Loadouts;

/// <summary>
/// Marks a mod the app installed to stand in for a requirement whose only copies in the loadout are
/// in collections that are switched off.
/// </summary>
/// <remarks>
/// The stand-in is a copy of the same file, so it is only wanted while no other copy is on: it is
/// switched off, and <see cref="StoodDown"/> set, when another copy of the file is switched on - the
/// collection being switched back on, say - and switched back on when that copy goes off again. A
/// stand-in the user switches on or off by hand is left as they set it.
/// </remarks>
[PublicAPI]
[Include<LoadoutItemGroup>]
public partial class RequirementStandIn : IModelDefinition
{
    private const string Namespace = "NexusMods.Loadouts.RequirementStandIn";

    /// <summary>
    /// Marker.
    /// </summary>
    public static readonly MarkerAttribute StandIn = new(Namespace, nameof(StandIn)) { IsIndexed = true };

    /// <summary>
    /// Set while the app has switched the stand-in off because another copy of the file is on.
    /// </summary>
    public static readonly MarkerAttribute StoodDown = new(Namespace, nameof(StoodDown));
}
