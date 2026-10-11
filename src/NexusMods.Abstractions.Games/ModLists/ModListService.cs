using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Collections;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Conflicts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Games;

/// <inheritdoc cref="IModListService"/>
public class ModListService : IModListService
{
    private readonly IConnection _connection;
    private readonly ILoadoutManager _loadoutManager;
    private readonly ILogger<ModListService> _logger;

    /// <summary>
    /// Constructor.
    /// </summary>
    public ModListService(IConnection connection, ILoadoutManager loadoutManager, ILogger<ModListService> logger)
    {
        _connection = connection;
        _loadoutManager = loadoutManager;
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
            .Select(static mod => ModListEntry.Mod(mod.Name, mod.IsEnabled));

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
            (ordered, enabled, disabled, unmatchedMods) = await ImportModList(loadoutId, import.ModList, collectionGroupId, token);
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

#region mod list

    /// <summary>
    /// A mod of the loadout as the mod list format sees it.
    /// </summary>
    private record struct Mod(LoadoutItemGroupId GroupId, string Name, bool IsEnabled, bool IsLocked, ConflictPriority Priority);

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
                Priority: priority.Priority
            ));
        }

        return mods;
    }

    private async ValueTask<(int Ordered, int Enabled, int Disabled, string[] Unmatched)> ImportModList(
        LoadoutId loadoutId,
        string contents,
        Optional<CollectionGroupId> collectionGroupId,
        CancellationToken token)
    {
        var entries = ModOrganizerModList.Parse(contents)
            .Where(static entry => entry.Kind == ModListEntryKind.Mod)
            .ToArray();

        // Repeated names each consume a different mod, so that two mods sharing a name still end up
        // at two different positions instead of fighting over one. Cloning a collection gives the
        // loadout a second copy of every mod in it under the same names, and one of the two copies is
        // usually locked, so unlocked candidates go first: those are the ones the user can act on.
        var byName = GetMods(_connection.Db, loadoutId, collectionGroupId)
            .GroupBy(static mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => new Queue<Mod>(group.OrderBy(static mod => mod.IsLocked)),
                StringComparer.OrdinalIgnoreCase
            );

        var matched = new List<(ModListEntry Entry, Mod Mod)>(capacity: entries.Length);
        var unmatched = new List<string>();

        foreach (var entry in entries)
        {
            if (byName.TryGetValue(entry.Name, out var candidates) && candidates.Count > 0)
                matched.Add((entry, candidates.Dequeue()));
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
        // the same contents it would write.
        var generated = await TryGenerateIntrinsicPluginList(loadout, token);
        return generated is null ? null : ModOrganizerPluginList.Write(ModOrganizerPluginList.Parse(generated));
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
