using DynamicData.Kernel;
using JetBrains.Annotations;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Paths;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// Moves a loadout's mod order, the enabled state of its mods and its load order in and out of
/// Mod Organizer 2's <c>modlist.txt</c>/<c>plugins.txt</c> pair.
/// </summary>
/// <remarks>
/// Mod Organizer's format carries names only. The app adds where each mod comes from on Nexus
/// Mods as comment lines Mod Organizer skips (see <see cref="ModListNexusSource"/>), and reads the
/// same from Mod Organizer's own <c>meta.ini</c> files when the list sits in its folder. That is
/// what lets <see cref="FindMissing"/> say what to download for the mods a loadout doesn't have;
/// the import itself only acts on installed mods. Mod Organizer's separator entries are read and
/// skipped, and never written, because the app has nothing to keep them in.
/// </remarks>
[PublicAPI]
public interface IModListService
{
    /// <summary>
    /// Produces the file contents describing the current state of the loadout.
    /// </summary>
    /// <param name="loadoutId">The loadout to describe.</param>
    /// <param name="collectionGroupId">
    /// Restricts the mod list to one collection. The load order is always the whole loadout's,
    /// because that is the granularity the game reads it at.
    /// </param>
    ValueTask<ModListExport> Export(LoadoutId loadoutId, Optional<CollectionGroupId> collectionGroupId = default, CancellationToken token = default);

    /// <summary>
    /// Applies the given file contents to the loadout.
    /// </summary>
    /// <param name="loadoutId">The loadout to change.</param>
    /// <param name="import">The file contents to apply. Either half may be left out.</param>
    /// <param name="collectionGroupId">Restricts the mods that may be matched to one collection.</param>
    ValueTask<ModListImportResult> Import(LoadoutId loadoutId, ModListImport import, Optional<CollectionGroupId> collectionGroupId = default, CancellationToken token = default);

    /// <summary>
    /// The mods in the list that the loadout doesn't have installed, highest priority first, each
    /// with where to download it from when that is known.
    /// </summary>
    ValueTask<ModListMissingMod[]> FindMissing(LoadoutId loadoutId, ModListImport import, CancellationToken token = default);
}

/// <summary>
/// A mod a list names that the loadout doesn't have.
/// </summary>
/// <param name="Name">The name the list gives it.</param>
/// <param name="IsEnabled">Whether the list has it switched on.</param>
/// <param name="Nexus">Where to download it from, or null when the list doesn't say.</param>
[PublicAPI]
public record ModListMissingMod(string Name, bool IsEnabled, ModListNexusSource? Nexus);

/// <summary>
/// The file contents produced by <see cref="IModListService.Export"/>.
/// </summary>
/// <param name="ModList">Contents of a <c>modlist.txt</c>.</param>
/// <param name="PluginList">
/// Contents of a <c>plugins.txt</c>, or <c>null</c> when the game has no load order to write.
/// </param>
[PublicAPI]
public record ModListExport(string ModList, string? PluginList);

/// <summary>
/// The file contents handed to <see cref="IModListService.Import"/>.
/// </summary>
/// <param name="ModList">Contents of a <c>modlist.txt</c>, or <c>null</c> to leave the mod order alone.</param>
/// <param name="PluginList">Contents of a <c>plugins.txt</c>, or <c>null</c> to leave the load order alone.</param>
[PublicAPI]
public record ModListImport(string? ModList, string? PluginList)
{
    /// <summary>
    /// Where <see cref="ModList"/> was read from, when it came from a file. A list inside a Mod
    /// Organizer profile has the instance's <c>meta.ini</c> files next to it, which say where each
    /// mod came from on Nexus Mods.
    /// </summary>
    public AbsolutePath? ModListPath { get; init; }
}

/// <summary>
/// What became of one half of an import.
/// </summary>
[PublicAPI]
public enum ModListImportStatus
{
    /// <summary>
    /// The caller did not supply this half.
    /// </summary>
    NotProvided,

    /// <summary>
    /// The caller supplied it and it was written to the loadout.
    /// </summary>
    Applied,

    /// <summary>
    /// The caller supplied it but this game has nothing to apply it to.
    /// </summary>
    NotSupported,
}

/// <summary>
/// What <see cref="IModListService.Import"/> changed, and what it could not place.
/// </summary>
[PublicAPI]
public record ModListImportResult
{
    /// <summary>
    /// Outcome of the <c>modlist.txt</c> half.
    /// </summary>
    public required ModListImportStatus ModListStatus { get; init; }

    /// <summary>
    /// How many of the loadout's mods were put in the order the file asked for.
    /// </summary>
    public required int ModsOrdered { get; init; }

    /// <summary>
    /// How many mods were switched on because the file had them enabled.
    /// </summary>
    public required int ModsEnabled { get; init; }

    /// <summary>
    /// How many mods were switched off because the file had them disabled.
    /// </summary>
    public required int ModsDisabled { get; init; }

    /// <summary>
    /// Names from the file that no installed mod matched, in the order they appeared.
    /// </summary>
    public required string[] UnmatchedMods { get; init; }

    /// <summary>
    /// Outcome of the <c>plugins.txt</c> half.
    /// </summary>
    public required ModListImportStatus PluginListStatus { get; init; }

    /// <summary>
    /// How many plugins were put in the order the file asked for.
    /// </summary>
    public required int PluginsOrdered { get; init; }

    /// <summary>
    /// Plugin names from the file that the loadout does not have, in the order they appeared.
    /// </summary>
    public required string[] UnmatchedPlugins { get; init; }
}
