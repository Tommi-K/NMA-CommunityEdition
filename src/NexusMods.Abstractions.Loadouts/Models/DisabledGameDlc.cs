using JetBrains.Annotations;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts;

/// <summary>
/// Marks one piece of the game's own optional content (a DLC) as switched off for a loadout.
/// </summary>
/// <remarks>
/// DLC are not loadout items: they reach the sync tree as game files from the store's manifest, so
/// there is no loadout item to carry a disabled flag the way a mod does. The files stay on disk
/// either way - what makes a DLC active is its plugin appearing in the game's plugins file, which
/// the app regenerates on every apply. An entry here means "leave that plugin out".
///
/// Entries are per loadout, so one loadout can play without Far Harbor while another keeps it.
/// </remarks>
[PublicAPI]
public partial class DisabledGameDlc : IModelDefinition
{
    private const string Namespace = "NexusMods.Loadouts.DisabledGameDlc";

    /// <summary>
    /// The id of the content that is switched off, as given by the game's DLC definition.
    /// </summary>
    public static readonly StringAttribute DlcId = new(Namespace, nameof(DlcId));

    /// <summary>
    /// The loadout this applies to.
    /// </summary>
    public static readonly ReferenceAttribute<Loadout> Loadout = new(Namespace, nameof(Loadout))
    {
        IsIndexed = true,
    };
}
