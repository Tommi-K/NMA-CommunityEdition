using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileSystemGlobbing;
using NexusMods.Abstractions.Cli;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.DataModel.Undo;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.DataModel.CommandLine.Verbs;

/// <summary>
/// Loadout management verbs for the commandline interface
/// </summary>
public static class LoadoutManagementVerbs
{
    /// <summary>
    /// Register the loadout management verbs
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection AddLoadoutManagementVerbs(this IServiceCollection services) =>
        services
            .AddModule("loadouts", "Commands for managing loadouts as a whole")
            .AddModule("loadout", "Commands for managing a specific loadout")
            .AddModule("loadout groups", "Commands for managing the file groups in a loadout")
            .AddModule("loadout group", "Commands for managing a specific group of files in a loadout")
            .AddModule("loadout group items", "Commands for managing the items in a group of files in a loadout")
            .AddModule("loadout version", "Commands for managing the version of a loadout")
            .AddVerb(() => SetVersion)
            .AddVerb(() => Synchronize)
            .AddVerb(() => InstallMod)
            .AddVerb(() => Reindex)
            .AddVerb(() => ListLoadouts)
            .AddVerb(() => BackupFiles)
            .AddVerb(() => ListGroupContents)
            .AddVerb(() => ListGroups)
            .AddVerb(() => DeleteGroupItems)
            .AddVerb(() => ListRevisions)
            .AddVerb(() => ExportModList)
            .AddVerb(() => ImportModList)
            .AddVerb(() => Revert);

    [Verb("loadout version set", "Sets the game version for a loadout")]
    private static async Task<int> SetVersion([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to set the version for")] Loadout.ReadOnly loadout,
        [Option("v", "version", "Version to set")] string version,
        [Injected] IFileHashesService hasherService)
    {
        if (!hasherService.TryGetLocatorIdsForVanityVersion(loadout.Installation.Store, VanityVersion.From(version), out var newCommonIds))
        {
            await renderer.Error("Version {0} not found", version);
            return -1;
        }

        // Get the actual version from the ids, so that we can sanitize the version string, and collapse multiple
        // versions into a single version string
        if (!hasherService.TryGetVanityVersion((loadout.Installation.Store, newCommonIds), out var actualVersion))
        {
            await renderer.Error("Version {0} not found", version);
            return -1;
        }
        
        using var tx = loadout.Db.Connection.BeginTransaction();

        loadout = loadout.Rebase();
        // Retract the old ids
        foreach (var existingId in loadout.LocatorIds)
            tx.Retract(loadout, Loadout.LocatorIds, existingId);
        // And the old version
        tx.Retract(loadout, Loadout.GameVersion, loadout.GameVersion);
        
        // Add the new ids and version
        foreach (var newId in newCommonIds)
            tx.Add(loadout, Loadout.LocatorIds, newId);
        
        tx.Add(loadout, Loadout.GameVersion, actualVersion);
        
        await tx.Commit();
        
        return 0;
    }

    [Verb("loadout synchronize", "Synchronize the loadout with the game folders, adding any changes in the game folder to the loadout and applying any new changes in the loadout to the game folder")]
    private static async Task<int> Synchronize([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to apply")] Loadout.ReadOnly loadout,
        [Injected] ISynchronizerService syncService)
    {
        await syncService.Synchronize(loadout);
        return 0;
    }

    [Verb("loadout backup-game-files", "Archives all the files on disk that are otherwise not backed up")]
    private static async Task<int> BackupFiles(
        [Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to backup files for")]
        Loadout.ReadOnly loadout,
        [Injected] IFileStore fileStore)
    {
        var game = loadout.InstallationInstance.GetGame();
        var gameInstallation = loadout.InstallationInstance;
        var tree = await game.Synchronizer.BuildSyncTree(loadout);
        game.Synchronizer.ProcessSyncTree(tree);
        var toBackup = tree.Where(f => f.Value.HaveDisk && 
                                       !f.Value.Signature.HasFlag(Signature.DiskArchived) && 
                                       f.Value.SourceItemType == LoadoutSourceItemType.Game)
            .Select(f => (Path: gameInstallation.Locations.ToAbsolutePath(f.Key), f.Value.Disk.Size, f.Value.Disk.Hash))
            .ToArray();
        await renderer.TextLine("Backing up {0} files for a total of {1}", toBackup.Length, toBackup.Aggregate(Size.Zero, (a, b) => a + b.Size));
        
        var entries = toBackup.Select(f => new ArchivedFileEntry(new NativeFileStreamFactory(f.Path), f.Hash, f.Size)).ToArray();
        await fileStore.BackupFiles(entries);
        return 0;
    }

    
    [Verb("loadout install", "Installs a mod into a loadout")]
    private static async Task<int> InstallMod([Injected] IRenderer renderer,
        [Option("l", "loadout", "loadout to add the mod to")] Loadout.ReadOnly loadout,
        [Option("f", "file", "Mod file to install")] AbsolutePath file,
        [Option("n", "name", "Name of the mod after installing")] string name,
        [Injected] ILibraryService libraryService,
        [Injected] ILoadoutManager loadoutManager,
        [Injected] CancellationToken token)
    {
        return await renderer.WithProgress(token, async () =>
        {
            var localFile = await libraryService.AddLocalFile(file); 
            await loadoutManager.InstallItem(localFile.AsLibraryFile().AsLibraryItem(), loadout);
            return 0;
        });
    }
    
    [Verb("loadout reindex", "Re-indexes the on-disk state of the loadout")]
    private static async Task<int> Reindex([Injected] IRenderer renderer,
        [Option("l", "loadout", "loadout to add the mod to")] Loadout.ReadOnly loadout,
        [Injected] CancellationToken token)
    {
        await renderer.Text("Reindexing {0}", loadout.Name);
        var synchronizer = loadout.InstallationInstance.GetGame().Synchronizer;
        await synchronizer.ReindexState(loadout.InstallationInstance);
        return 0;
    }

    [Verb("loadouts list", "Lists all the loadouts")]
    private static async Task<int> ListLoadouts([Injected] IRenderer renderer,
        [Injected] IConnection conn,
        [Injected] CancellationToken token)
    {
        var db = conn.Db;
        await Loadout.All(db)
            .Where(x => x.IsVisible())
            .Select(list => (list.LoadoutId, list.Name, list.Installation.Name, list.GameVersion, list.Installation.Store, LoadoutItem.FindByLoadout(db, list.Id)))
            .RenderTable(renderer, "Id", "Name", "Game", "Version", "Store", "Items");
        return 0;
    }

    [Verb("loadout group list", "Lists the contents of a loadout group")]
    private static async Task<int> ListGroupContents([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to load")] Loadout.ReadOnly loadout,
        [Option("g", "group", "Name of the group to list")] string groupName,
        [Option("f", "filterFiles", "Filter files by the given glob", true)] Matcher? filterFiles,
        [Injected] CancellationToken token)
    {
        var mod = LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemGroup()
            .First(m => m.AsLoadoutItem().Name == groupName);

        if (!mod.IsValid())
            return await renderer.InputError("Group {0} not found", groupName);
        
        Func<string, bool> filter = _ => true;

        if (filterFiles != null) 
            filter = s => filterFiles.Match(s).HasMatches;
        
        await mod.Children
            .Select(c =>
                {
                    var hasPath = c.TryGetAsLoadoutItemWithTargetPath(out var withPath);
                    var hasFile = withPath.TryGetAsLoadoutFile(out var withFile);

                    if (hasPath && hasFile)
                        return (withPath.TargetPath.Item2, withPath.TargetPath.Item3, withFile.Hash.ToString());

                    if (hasPath)
                        return (withPath.TargetPath.Item2, withPath.TargetPath.Item3, "<none>");

                    return default((LocationId, RelativePath, string));

                })
            .Where(v => v != default((LocationId, RelativePath, string)))
            .Where(f => filter(f.Item2.ToString()))
            .OrderBy(v => v.Item1)
            .ThenBy(v => v.Item2)
            .RenderTable(renderer, "Folder", "File", "Hash");
        return 0;
    }

    [Verb("loadout group items delete", "Deletes items from a group that match a given pattern")]
    private static async Task<int> DeleteGroupItems(
        [Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to load")] Loadout.ReadOnly loadout,
        [Option("g", "group", "Name of the group to list")] string groupName,
        [Option("f", "filterFiles", "Filter files by the given glob")] Matcher filterFiles,
        [Injected] CancellationToken token)
    {
        var mod = LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemGroup()
            .First(m => m.AsLoadoutItem().Name == groupName);
        
        if (!mod.IsValid())
            return await renderer.InputError("Group {0} not found", groupName);

        var ids = mod.Children
            .OfTypeLoadoutItemWithTargetPath()
            .Where(t => filterFiles.Match(t.TargetPath.Item3.ToString()).HasMatches)
            .Select(f => f.Id)
            .ToArray();
        
        await renderer.Text("Deleting {0} items", ids.Length);
        
        using var tx = loadout.Db.Connection.BeginTransaction();
        foreach (var id in ids)
            tx.Delete(id, false);
        await tx.Commit();
        
        await renderer.Text("Complete", ids.Length);
        
        return 0;
    }

    [Verb("loadout groups list", "Lists the groups in a loadout")]
    private static async Task<int> ListGroups([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to load")] Loadout.ReadOnly loadout,
        [Injected] CancellationToken token)
    {
        await LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemGroup()
            .Select(mod => (mod.AsLoadoutItem().Name, mod.Children.Count))
            .RenderTable(renderer, "Name", "Items");

        return 0;
    }

    /// <summary>
    /// Resolves the optional collection name of the two mod list verbs. No name means the whole loadout.
    /// </summary>
    private static bool TryResolveCollection(Loadout.ReadOnly loadout, string? name, out Optional<CollectionGroupId> collectionGroupId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            collectionGroupId = Optional<CollectionGroupId>.None;
            return true;
        }

        var match = CollectionGroup.All(loadout.Db)
            .Where(group => group.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId == loadout.LoadoutId)
            .FirstOrOptional(group => string.Equals(group.AsLoadoutItemGroup().AsLoadoutItem().Name, name, StringComparison.OrdinalIgnoreCase));

        collectionGroupId = match.Convert(static group => group.CollectionGroupId);
        return match.HasValue;
    }

    [Verb("loadout export-mod-list", "Writes the loadout's mod order, enabled mods and load order to a Mod Organizer style modlist.txt and plugins.txt")]
    private static async Task<int> ExportModList([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to export")] Loadout.ReadOnly loadout,
        [Option("o", "output", "Directory the two files are written to")] AbsolutePath output,
        [Option("c", "collection", "Name of a collection to limit the mod list to", true)] string? collection,
        [Injected] IModListService modListService,
        [Injected] CancellationToken token)
    {
        if (!TryResolveCollection(loadout, collection, out var collectionGroupId))
            return await renderer.InputError("No collection named {0} in this loadout", collection!);

        var export = await modListService.Export(loadout.LoadoutId, collectionGroupId, token);

        output.CreateDirectory();

        var modListPath = output / ModOrganizerModList.FileName;
        await modListPath.WriteAllTextAsync(export.ModList, token);
        await renderer.TextLine("Wrote {0}", modListPath);

        if (export.PluginList is null)
        {
            await renderer.TextLine("{0} has no load order to write", loadout.Game.DisplayName);
            return 0;
        }

        var pluginListPath = output / ModOrganizerPluginList.FileName;
        await pluginListPath.WriteAllTextAsync(export.PluginList, token);
        await renderer.TextLine("Wrote {0}", pluginListPath);
        return 0;
    }

    [Verb("loadout import-mod-list", "Applies a Mod Organizer style modlist.txt and plugins.txt to the loadout's mod order, enabled mods and load order")]
    private static async Task<int> ImportModList([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to change")] Loadout.ReadOnly loadout,
        [Option("i", "input", "Directory holding modlist.txt and/or plugins.txt")] AbsolutePath input,
        [Option("c", "collection", "Name of a collection to limit the matched mods to", true)] string? collection,
        [Option("n", "dry-run", "Only report which mods the list names that aren't installed, and where to download them from; change nothing", true)] bool dryRun,
        [Injected] IModListService modListService,
        [Injected] CancellationToken token)
    {
        if (!TryResolveCollection(loadout, collection, out var collectionGroupId))
            return await renderer.InputError("No collection named {0} in this loadout", collection!);

        var modListPath = input / ModOrganizerModList.FileName;
        var pluginListPath = input / ModOrganizerPluginList.FileName;

        var modList = modListPath.FileExists ? await modListPath.ReadAllTextAsync(token) : null;
        var pluginList = pluginListPath.FileExists ? await pluginListPath.ReadAllTextAsync(token) : null;

        if (modList is null && pluginList is null)
            return await renderer.InputError("Found neither {0} nor {1} in {2}", ModOrganizerModList.FileName, ModOrganizerPluginList.FileName, input);

        var import = new ModListImport(modList, pluginList) { ModListPath = modListPath.FileExists ? modListPath : null };

        var missing = await modListService.FindMissing(loadout.LoadoutId, import, token);
        if (missing.Length > 0)
        {
            await renderer.TextLine("{0} mods in the list are not installed, {1} of them with a Nexus Mods source to download from (downloading is done from the app):",
                missing.Length, missing.Count(static mod => mod.Nexus is not null));
            await renderer.Table(["Mod", "Nexus Mods"], missing.Select(mod => new object[] { mod.Name, mod.Nexus?.ToCommentLine()[ModListNexusSource.CommentPrefix.Length..] ?? "-" }));
        }

        if (dryRun)
        {
            await renderer.TextLine("Dry run: {0} mods in the list, {1} of them installed; nothing was changed",
                ModOrganizerModList.Parse(modList ?? string.Empty).Count(static entry => entry.Kind == ModListEntryKind.Mod),
                ModOrganizerModList.Parse(modList ?? string.Empty).Count(static entry => entry.Kind == ModListEntryKind.Mod) - missing.Length);
            return 0;
        }

        var result = await modListService.Import(loadout.LoadoutId, import, collectionGroupId, token);

        if (result.ModListStatus == ModListImportStatus.Applied)
        {
            await renderer.TextLine("Ordered {0} mods, enabled {1}, disabled {2}", result.ModsOrdered, result.ModsEnabled, result.ModsDisabled);
            if (result.UnmatchedMods.Length > 0)
            {
                await renderer.TextLine("{0} names matched no installed mod:", result.UnmatchedMods.Length);
                await renderer.Table(["Mod"], result.UnmatchedMods.Select(name => new object[] { name }));
            }
        }

        switch (result.PluginListStatus)
        {
            case ModListImportStatus.Applied:
                await renderer.TextLine("Ordered {0} plugins", result.PluginsOrdered);
                if (result.UnmatchedPlugins.Length > 0)
                {
                    await renderer.TextLine("{0} plugins are not in this loadout:", result.UnmatchedPlugins.Length);
                    await renderer.Table(["Plugin"], result.UnmatchedPlugins.Select(name => new object[] { name }));
                }
                break;
            case ModListImportStatus.NotSupported:
                await renderer.TextLine("{0} builds its load order itself, so the plugin order was not applied", loadout.Game.DisplayName);
                break;
        }

        return 0;
    }

    [Verb("loadout revisions", "Lists revisions for a loadout")]
    private static async Task<int> ListRevisions([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to load")] Loadout.ReadOnly loadout,
        [Injected] IConnection connection,
        [Injected] UndoService undoService,
        [Injected] CancellationToken token)
    {
        var revisions = undoService.RevisionsFor(loadout);

        await revisions.OrderBy(r => r.Revision.Timestamp)
            .Select((r, idx) => (idx, r.Revision.Timestamp, r.Revision.TxEntity, r.ModCount))
            .RenderTable(renderer, "Rev #", "Timestamp", "Tx", "ModCount");

        return 0;
    }

    [Verb("loadout revert", "Reverts a loadout to a specific revision")]
    private static async Task<int> Revert([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to load")] Loadout.ReadOnly loadout,
        [Option("r", "revisionNumber", "Revision number to revert to")] int revisionNumber,
        [Injected] IConnection connection,
        [Injected] UndoService undoService,
        [Injected] CancellationToken token)
    {
        
        var revisions = undoService.RevisionsFor(loadout);
        
        var revision = revisions.OrderBy(r => r.Revision.Timestamp)
            .Select((r, idx) => (Idx: idx, r.Revision))
            .FirstOrDefault(row => row.Idx == revisionNumber);
        
        await undoService.RevertTo(revision.Revision);
        
        return 0;
    }
}
