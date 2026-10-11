using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Mutagen.Bethesda.Plugins;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Diagnostics.References;

using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.Emitters;

public class MissingMasterEmitter : ILoadoutDiagnosticEmitter
{
    private readonly ICreationEngineGame _game;
    public MissingMasterEmitter(ICreationEngineGame game)
    {
        _game = game;
    }
    
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        
        var plugins = syncTree.Where(node =>
            node.Value.HaveLoadout &&
            _game.PluginExtensions.Contains(node.Key.Extension) &&
            node.Key.Parent == KnownPaths.Data);
        
        // Index the plugins in the loadout
        Dictionary<string, LoadoutItemGroup.ReadOnly> pluginsInLoadout = new();
        foreach (var item in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath())
        {
            var path = (GamePath)item.TargetPath;
            if (path.LocationId == LocationId.Game && path.Parent == KnownPaths.Data && _game.PluginExtensions.Contains(path.Extension))
                pluginsInLoadout[path.FileName] = item.AsLoadoutItem().Parent;

        }

        // Index the plugins that will be on-disk in the game folder
        Dictionary<ModKey, (SyncNode Node, PluginHeader Header)> pluginHeaders = new();
        foreach (var (key, node) in plugins)
        {
            var header = await _game.ParsePlugin(node.Loadout.Hash, key.Path.FileName);
            if (header is null)
                continue;
            pluginHeaders[header.Value.ModKey] = (node, header.Value);
        }

        // For each on-disk plugin, check if it has all the required masters
        foreach (var header in pluginHeaders)
        {
            foreach (var master in header.Value.Header.Masters)
            {
                // If the master is already in the loadout, skip it
                if (pluginHeaders.ContainsKey(master))
                    continue;

                // If the master is disabled, emit a diagnostic
                if (pluginsInLoadout.TryGetValue(master.FileName, out var disabled))
                {
                    yield return Diagnostics.CreateMissingMasterIsDisabled(
                        new LoadoutItemGroupReference()
                        {
                            TxId = loadout.Db.BasisTxId,
                            DataId = header.Value.Node.Loadout.EntityId
                        },
                        header.Key,
                        master,
                        new LoadoutItemGroupReference()
                            {
                                TxId = loadout.Db.BasisTxId,
                                DataId = disabled.LoadoutItemGroupId,
                            }
                    );
                }
                // Otherwise, emit a diagnostic that the master is missing
                else
                {
                    yield return Diagnostics.CreateMissingMaster(
                        new LoadoutItemGroupReference()
                        {
                            TxId = loadout.Db.BasisTxId,
                            DataId = header.Value.Node.Loadout.EntityId
                        },
                        header.Key,
                        master
                    );
                }
            }
        }

    }
}
