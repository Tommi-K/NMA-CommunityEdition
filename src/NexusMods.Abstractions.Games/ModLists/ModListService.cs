using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Collections;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Conflicts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Paths;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Abstractions.Games;

/// <inheritdoc cref="IModListService"/>
public class ModListService : IModListService
{
    private readonly IConnection _connection;
    private readonly ILoadoutManager _loadoutManager;
    private readonly IGameDomainToGameIdMappingCache _mappingCache;
    private readonly ILogger<ModListService> _logger;

    /// <summary>
    /// Constructor.
    /// </summary>
    public ModListService(IConnection connection, ILoadoutManager loadoutManager, IGameDomainToGameIdMappingCache mappingCache, ILogger<ModListService> logger)
    {
        _connection = connection;
        _loadoutManager = loadoutManager;
        _mappingCache = mappingCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<ModListExport> Export(LoadoutId loadoutId, Optional<CollectionGroupId> collectionGroupId = default, CancellationToken token = default)
    {
        var db = _connection.Db;
        var loadout = Loadout.Load(db, loadoutId);
        if (!loadout.IsValid()) throw new ArgumentException($"Loadout {loadoutId} does not exist", nameof(loadoutId));

        // Highest priority first: that is the order Mod Organizer stores modlist.txt in.
        var mods = GetMods(db, loadoutId, collectionGroupId)
            .OrderByDescending(static mod => mod.Priority)
            .Select(static mod => ModListEntry.Mod(mod.Name, mod.IsEnabled) with { Nexus = mod.Nexus });

        var pluginList = await ExportPluginList(loadout, token);
        return new ModListExport(ModOrganizerModList.Write(mods), pluginList);
    }

    /// <inheritdoc />
    public async ValueTask<ModListImportResult> Import(LoadoutId loadoutId, ModListImport import, Optional<CollectionGroupId> collectionGroupId = default, CancellationToken token = default)
    {
        var loadout = Loadout.Load(_connection.Db, loadoutId);
        if (!loadout.IsValid()) throw new ArgumentException($"Loadout {loadoutId} does not exist", nameof(loadoutId));

        // The load order goes first. Switching mods on and off makes the sort order reconcile itself in
        // the background, and writing the load order while that is in flight can lose the write.
        var (pluginListStatus, pluginsOrdered, unmatchedPlugins) = import.PluginList is null
            ? (ModListImportStatus.NotProvided, 0, Array.Empty<string>())
            : await ImportPluginList(loadout, import.PluginList, token);

        var modListStatus = ModListImportStatus.NotProvided;
        var ordered = 0;
        var enabled = 0;
        var disabled = 0;
        var unmatchedMods = Array.Empty<string>();

        if (import.ModList is not null)
        {
            (ordered, enabled, disabled, unmatchedMods) = await ImportModList(loadout, import, collectionGroupId, token);
            modListStatus = ModListImportStatus.Applied;
        }

        return new ModListImportResult
        {
            ModListStatus = modListStatus,
            ModsOrdered = ordered,
            ModsEnabled = enabled,
            ModsDisabled = disabled,
            UnmatchedMods = unmatchedMods,
            PluginListStatus = pluginListStatus,
            PluginsOrdered = pluginsOrdered,
            UnmatchedPlugins = unmatchedPlugins,
        };
    }

    /// <inheritdoc />
    public async ValueTask<ModListMissingMod[]> FindMissing(LoadoutId loadoutId, ModListImport import, CancellationToken token = default)
    {
        var loadout = Loadout.Load(_connection.Db, loadoutId);
        if (!loadout.IsValid()) throw new ArgumentException($"Loadout {loadoutId} does not exist", nameof(loadoutId));
        if (import.ModList is null) return [];

        var entries = await ReadModEntries(loadout, import, token);

        // Against the whole loadout even when the import is for one collection: a mod that is
        // installed anywhere doesn't need downloading again.
        var matcher = new ModMatcher(GetMods(_connection.Db, loadoutId, Optional<CollectionGroupId>.None));

        return entries
            .Where(entry => !matcher.TryTake(entry, out _))
            .Select(static entry => new ModListMissingMod(entry.Name, entry.IsEnabled, entry.Nexus))
            .ToArray();
    }

#region mod list

    /// <summary>
    /// A mod of the loadout as the mod list format sees it.
    /// </summary>
    private record struct Mod(LoadoutItemGroupId GroupId, string Name, bool IsEnabled, bool IsLocked, ConflictPriority Priority, ModListNexusSource? Nexus);

    /// <summary>
    /// The loadout's mods, in no particular order.
    /// </summary>
    /// <remarks>
    /// Conflict priorities are the app's own mod order, and every installed mod has one, which
    /// makes them the list of mods this format is about.
    /// </remarks>
    private static List<Mod> GetMods(IDb db, LoadoutId loadoutId, Optional<CollectionGroupId> collectionGroupId)
    {
        var mods = new List<Mod>();

        foreach (var priority in LoadoutItemGroupPriority.FindByLoadout(db, loadoutId))
        {
            var group = priority.Target;
            if (!group.IsValid()) continue;

            var item = group.AsLoadoutItem();

            if (collectionGroupId.HasValue && (!item.HasParent() || item.ParentId.Value != collectionGroupId.Value.Value))
                continue;

            var parentIsDisabledCollection = item.HasParent()
                                             && item.Parent.TryGetAsCollectionGroup(out var collection)
                                             && collection.AsLoadoutItemGroup().AsLoadoutItem().IsDisabled;

            // A collection can mark an item as required, in which case the user can't switch it off
            // through the app either, so an import must not try to.
            var isLocked = parentIsDisabledCollection
                           || (NexusCollectionItemLoadoutGroup.IsRequired.TryGetValue(item, out var isRequired) && isRequired);

            mods.Add(new Mod(
                GroupId: group.LoadoutItemGroupId,
                Name: item.Name,
                IsEnabled: !item.IsDisabled && !parentIsDisabledCollection,
                IsLocked: isLocked,
                Priority: priority.Priority,
                Nexus: GetNexusSource(group)
            ));
        }

        return mods;
    }

    /// <summary>
    /// The Nexus Mods file a mod was installed from, when it came from there.
    /// </summary>
    private static ModListNexusSource? GetNexusSource(LoadoutItemGroup.ReadOnly group)
    {
        if (!group.TryGetAsLibraryLinkedLoadoutItem(out var linked)) return null;
        if (!linked.LibraryItem.TryGetAsNexusModsLibraryItem(out var nexusItem)) return null;

        var modPage = nexusItem.ModPageMetadata;
        return new ModListNexusSource(modPage.GameDomain, modPage.Uid.ModId, nexusItem.FileMetadata.Uid.FileId);
    }

    /// <summary>
    /// The mod entries of the list, highest priority first, each with its Nexus Mods source where
    /// one is known.
    /// </summary>
    /// <remarks>
    /// The list may say itself, in comment lines this app writes. Otherwise, for a list that sits in
    /// a Mod Organizer profile, each mod's <c>meta.ini</c> in the instance's mods folder says.
    /// </remarks>
    private async ValueTask<ModListEntry[]> ReadModEntries(Loadout.ReadOnly loadout, ModListImport import, CancellationToken token)
    {
        var entries = ModOrganizerModList.Parse(import.ModList ?? string.Empty)
            .Where(static entry => entry.Kind == ModListEntryKind.Mod)
            .ToArray();

        var modsFolder = import.ModListPath is { } path ? ModOrganizerMetaIni.FindModsFolder(path) : null;
        if (modsFolder is null) return entries;

        GameDomain fallbackDomain;
        try
        {
            fallbackDomain = _mappingCache[loadout.Installation.GameId];
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "No site name for the game of loadout {LoadoutId}, skipping Mod Organizer's meta.ini files", loadout.LoadoutId);
            return entries;
        }

        var found = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            if (entries[i].Nexus is not null) continue;

            var metaIni = modsFolder.Value / entries[i].Name / ModOrganizerMetaIni.FileName;
            if (!metaIni.FileExists) continue;

            try
            {
                var source = ModOrganizerMetaIni.ReadNexusSource(await metaIni.ReadAllTextAsync(token), fallbackDomain);
                if (source is null) continue;

                entries[i] = entries[i] with { Nexus = source };
                found++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "Couldn't read `{Path}`", metaIni);
            }
        }

        _logger.LogInformation("Read the Nexus Mods source of {Found} of {Count} mods from Mod Organizer's folder `{Folder}`", found, entries.Length, modsFolder.Value);
        return entries;
    }

    /// <summary>
    /// Pairs list entries with installed mods: by the exact Nexus Mods file, then by the mod
    /// page, then by the file under any game, then by name. Each installed mod is handed out once.
    /// </summary>
    /// <remarks>
    /// The Nexus Mods source matters because the names often differ: Mod Organizer names a mod's
    /// folder after the download, the app after the mod page. Matching the page as well as the
    /// file is what lets a list that names an older file still find the newer one installed.
    /// <para/>
    /// Repeated names each consume a different mod, so that two mods sharing a name still end up
    /// at two different positions instead of fighting over one. Cloning a collection gives the
    /// loadout a second copy of every mod in it under the same names, and one of the two copies is
    /// usually locked, so unlocked candidates go first: those are the ones the user can act on.
    /// </remarks>
    private sealed class ModMatcher
    {
        private readonly HashSet<LoadoutItemGroupId> _taken = [];
        private readonly Dictionary<(GameDomain, ModId, FileId), Queue<Mod>> _byFile = [];
        private readonly Dictionary<(ModId, FileId), Queue<Mod>> _byFileAnyGame = [];
        private readonly Dictionary<(GameDomain, ModId), Queue<Mod>> _byPage = [];
        private readonly Dictionary<string, Queue<Mod>> _byName = new(StringComparer.OrdinalIgnoreCase);

        public ModMatcher(IEnumerable<Mod> mods)
        {
            foreach (var mod in mods.OrderBy(static mod => mod.IsLocked))
            {
                Enqueue(_byName, mod.Name, mod);
                if (mod.Nexus is not { } nexus) continue;

                Enqueue(_byPage, (nexus.GameDomain, nexus.ModId), mod);
                if (nexus.FileId is not { } fileId) continue;

                Enqueue(_byFile, (nexus.GameDomain, nexus.ModId, fileId), mod);
                Enqueue(_byFileAnyGame, (nexus.ModId, fileId), mod);
            }
        }

        public bool TryTake(ModListEntry entry, out Mod mod)
        {
            if (entry.Nexus is { } nexus)
            {
                if (nexus.FileId is { } fileId && TryTake(_byFile, (nexus.GameDomain, nexus.ModId, fileId), out mod)) return true;
                if (TryTake(_byPage, (nexus.GameDomain, nexus.ModId), out mod)) return true;

                // Mod Organizer names the game in its own words, and a tool hosted in the site's own
                // section is filed under the game it was installed for. The same mod and file ids
                // under another game are all but certainly the same file.
                if (nexus.FileId is { } anyGameFileId && TryTake(_byFileAnyGame, (nexus.ModId, anyGameFileId), out mod)) return true;
            }

            return TryTake(_byName, entry.Name, out mod);
        }

        private bool TryTake<TKey>(Dictionary<TKey, Queue<Mod>> index, TKey key, out Mod mod) where TKey : notnull
        {
            mod = default;
            if (!index.TryGetValue(key, out var queue)) return false;

            while (queue.TryDequeue(out var candidate))
            {
                if (!_taken.Add(candidate.GroupId)) continue;

                mod = candidate;
                return true;
            }

            return false;
        }

        private static void Enqueue<TKey>(Dictionary<TKey, Queue<Mod>> index, TKey key, Mod mod) where TKey : notnull
        {
            if (!index.TryGetValue(key, out var queue))
            {
                queue = new Queue<Mod>();
                index[key] = queue;
            }

            queue.Enqueue(mod);
        }
    }

    private async ValueTask<(int Ordered, int Enabled, int Disabled, string[] Unmatched)> ImportModList(
        Loadout.ReadOnly loadout,
        ModListImport import,
        Optional<CollectionGroupId> collectionGroupId,
        CancellationToken token)
    {
        var loadoutId = loadout.LoadoutId;
        var entries = await ReadModEntries(loadout, import, token);
        var matcher = new ModMatcher(GetMods(_connection.Db, loadoutId, collectionGroupId));

        var matched = new List<(ModListEntry Entry, Mod Mod)>(capacity: entries.Length);
        var unmatched = new List<string>();

        foreach (var entry in entries)
        {
            if (matcher.TryTake(entry, out var mod))
                matched.Add((entry, mod));
            else
                unmatched.Add(entry.Name);
        }

        // The file runs from highest to lowest priority, ReorderMods wants the reverse.
        var orderedGroupIds = matched.Select(static pair => pair.Mod.GroupId).Reverse().ToArray();
        await _loadoutManager.ReorderMods(loadoutId, orderedGroupIds);

        var (enabled, disabled) = await ApplyEnabledStates(matched, token);

        _logger.LogInformation(
            "Imported a mod list into loadout {LoadoutId}: ordered {Ordered} mods, enabled {Enabled}, disabled {Disabled}, {Unmatched} names unmatched",
            loadoutId, orderedGroupIds.Length, enabled, disabled, unmatched.Count
        );

        return (orderedGroupIds.Length, enabled, disabled, unmatched.ToArray());
    }

    private async ValueTask<(int Enabled, int Disabled)> ApplyEnabledStates(List<(ModListEntry Entry, Mod Mod)> matched, CancellationToken token)
    {
        var toEnable = new List<Mod>();
        var toDisable = new List<Mod>();

        foreach (var (entry, mod) in matched)
        {
            if (mod.IsLocked || entry.IsEnabled == mod.IsEnabled) continue;

            if (entry.IsEnabled) toEnable.Add(mod);
            else toDisable.Add(mod);
        }

        if (toEnable.Count == 0 && toDisable.Count == 0) return (0, 0);
        if (token.IsCancellationRequested) return (0, 0);

        using var tx = _connection.BeginTransaction();

        foreach (var mod in toEnable)
            tx.Retract(mod.GroupId.Value, LoadoutItem.Disabled, Null.Instance);

        foreach (var mod in toDisable)
            tx.Add(mod.GroupId.Value, LoadoutItem.Disabled, Null.Instance);

        await tx.Commit();
        return (toEnable.Count, toDisable.Count);
    }

#endregion mod list

#region plugin list

    /// <summary>
    /// Finds the load order the game would deploy, or null when it has none to describe.
    /// </summary>
    private async ValueTask<string?> ExportPluginList(Loadout.ReadOnly loadout, CancellationToken token)
    {
        // A sort order is the user's own load order, so it wins over anything we'd derive ourselves.
        if (TryGetLoadOrder(loadout, out var variety, out var sortOrderId))
        {
            var items = variety.GetSortOrderItems(sortOrderId)
                .OrderBy(static item => item.SortIndex)
                .Select(static item => item.DisplayName);

            return ModOrganizerPluginList.Write(items);
        }

        // Otherwise the game may generate the file itself on apply, in which case we can ask it for
        // the same contents it would write. Its entries are kept as they are: Mod Organizer reads the
        // game's own dialect, and in the asterisk one a name without its `*` is a disabled plugin.
        var generated = await TryGenerateIntrinsicPluginList(loadout, token);
        return generated is null ? null : ModOrganizerPluginList.Write(ModOrganizerPluginList.ParseEntries(generated));
    }

    private async ValueTask<(ModListImportStatus Status, int Ordered, string[] Unmatched)> ImportPluginList(
        Loadout.ReadOnly loadout,
        string contents,
        CancellationToken token)
    {
        // Games that generate their load order from the plugins' own master references - the Creation
        // Engine ones - have nothing to store a user-supplied order in, so there is nothing to apply.
        if (!TryGetLoadOrder(loadout, out var variety, out var sortOrderId))
            return (ModListImportStatus.NotSupported, 0, []);

        var byName = variety.GetSortOrderItems(sortOrderId)
            .GroupBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => new Queue<IReactiveSortItem>(group), StringComparer.OrdinalIgnoreCase);

        var orderedKeys = new List<ISortItemKey>();
        var unmatched = new List<string>();

        foreach (var pluginName in ModOrganizerPluginList.Parse(contents))
        {
            if (byName.TryGetValue(pluginName, out var candidates) && candidates.Count > 0)
                orderedKeys.Add(candidates.Dequeue().Key);
            else
                unmatched.Add(pluginName);
        }

        await variety.ReorderItems(sortOrderId, orderedKeys.ToArray(), token: token);

        _logger.LogInformation(
            "Imported a plugin list into loadout {LoadoutId}: ordered {Ordered} plugins, {Unmatched} names unmatched",
            loadout.LoadoutId, orderedKeys.Count, unmatched.Count
        );

        return (ModListImportStatus.Applied, orderedKeys.Count, unmatched.ToArray());
    }

    /// <summary>
    /// Finds the game's editable load order, if it has one.
    /// </summary>
    /// <remarks>
    /// A game could in principle have several sort orders - Cyberpunk's archive and REDmod orders, for
    /// instance - and the format has only one file to put them in, so the first one wins.
    /// </remarks>
    private static bool TryGetLoadOrder(Loadout.ReadOnly loadout, out ISortOrderVariety variety, out SortOrderId sortOrderId)
    {
        var game = loadout.InstallationInstance.GetGame();

        foreach (var candidate in game.SortOrderManager.GetSortOrderVarieties())
        {
            var candidateId = candidate.GetSortOrderIdFor(loadout.LoadoutId);
            if (!candidateId.HasValue) continue;

            variety = candidate;
            sortOrderId = candidateId.Value;
            return true;
        }

        variety = null!;
        sortOrderId = default(SortOrderId);
        return false;
    }

    /// <summary>
    /// Asks the game to generate its plugins file, as it would when the loadout is applied.
    /// </summary>
    /// <remarks>
    /// This indexes the game folder to build the sync tree, which is the same work an apply does,
    /// so it is only worth doing on an explicit user action.
    /// </remarks>
    private async ValueTask<string?> TryGenerateIntrinsicPluginList(Loadout.ReadOnly loadout, CancellationToken token)
    {
        // Only the base synchronizer knows about intrinsic files; a game with its own implementation
        // simply has none to ask for.
        if (loadout.InstallationInstance.GetGame().Synchronizer is not ALoadoutSynchronizer synchronizer) return null;

        var intrinsicFile = synchronizer.IntrinsicFiles(loadout)
            .Where(kv => string.Equals(kv.Key.FileName.ToString(), ModOrganizerPluginList.FileName, StringComparison.OrdinalIgnoreCase))
            .Select(static kv => kv.Value)
            .FirstOrDefault();

        if (intrinsicFile is null) return null;

        try
        {
            var syncTree = await synchronizer.BuildSyncTree(loadout);
            synchronizer.ProcessSyncTree(syncTree);

            if (token.IsCancellationRequested) return null;

            using var stream = new MemoryStream();
            await intrinsicFile.Write(stream, loadout, syncTree);
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (Exception ex)
        {
            // An export is worth delivering without the load order half rather than not at all.
            _logger.LogWarning(ex, "Unable to generate the plugins file for loadout {LoadoutId}", loadout.LoadoutId);
            return null;
        }
    }

#endregion plugin list
}
