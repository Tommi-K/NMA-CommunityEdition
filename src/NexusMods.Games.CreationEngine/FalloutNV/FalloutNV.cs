using System.Collections.Immutable;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.Emitters;
using NexusMods.Games.CreationEngine.Gamebryo;
using NexusMods.Games.CreationEngine.Installers;
using NexusMods.Games.FOMOD;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;

namespace NexusMods.Games.CreationEngine.FalloutNV;

/// <remarks>
/// Deliberately not an <c>IGameWithDlc</c>. That feature switches a game's own content off
/// by leaving its plugin out of the generated plugins file, which does not work here: New
/// Vegas ships a zero byte <c>.nam</c> marker beside each add-on's plugin, and a plugin
/// with a matching <c>.nam</c> is loaded whatever the plugins file says. Offering the
/// toggle would mean offering one that quietly does nothing.
/// </remarks>
public class FalloutNV : ICreationEngineGame, IGameData<FalloutNV>
{
    private readonly IStreamSourceDispatcher _streamSource;

    public static GameId GameId { get; } = GameId.From("CreationEngine.FalloutNV");
    public static string DisplayName => "Fallout: New Vegas";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(130);

    /// <remarks>
    /// 22380 is the standard build; 22490 is the separate "enplczru" build Steam serves in
    /// some regions, which is the same game in a differently named folder. There is no Epic
    /// entry because the launcher's catalog item id for New Vegas isn't published anywhere
    /// verifiable - an Epic copy has to be added by hand, and <see cref="GetLocations"/>
    /// still puts its settings in the right place once it is.
    /// </remarks>
    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [22380u, 22490u],
        GOGProductIds = [1454587428L],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<FalloutNV>("NexusMods.Games.CreationEngine.Resources.FalloutNV.thumbnail.webp");
    public IStreamFactory TileImage { get; } = new EmbeddedResourceStreamFactory<FalloutNV>("NexusMods.Games.CreationEngine.Resources.FalloutNV.tile.webp");

    private readonly Lazy<ILoadoutSynchronizer> _synchronizer;
    public ILoadoutSynchronizer Synchronizer => _synchronizer.Value;
    public ILibraryItemInstaller[] LibraryItemInstallers { get; }
    private readonly Lazy<ISortOrderManager> _sortOrderManager;
    public ISortOrderManager SortOrderManager => _sortOrderManager.Value;
    public IDiagnosticEmitter[] DiagnosticEmitters { get; }

    public FalloutNV(IServiceProvider provider)
    {
        _streamSource = provider.GetRequiredService<IStreamSourceDispatcher>();

        _synchronizer = new Lazy<ILoadoutSynchronizer>(() => new FalloutNVSynchronizer(provider, this));
        _sortOrderManager = new Lazy<ISortOrderManager>(() =>
        {
            var sortOrderManager = provider.GetRequiredService<SortOrderManager>();
            sortOrderManager.RegisterSortOrderVarieties([], this);
            return sortOrderManager;
        });

        DiagnosticEmitters =
        [
            new MissingMasterEmitter(this),
        ];

        LibraryItemInstallers =
        [
            FomodXmlInstaller.Create(provider, new GamePath(LocationId.Game, "Data")),
            new StopPatternInstaller(provider)
            {
                GameId = GameId,
                GameAliases = ["Fallout New Vegas", "Fallout: New Vegas", "FalloutNV", "Fallout NV", "FNV"],
                TopLevelDirs = FalloutNVKnownPaths.TopLevelFolders,
                StopPatterns = ["(^|/)nvse(/|$)"],
                // No .esl: light plugins arrived with Fallout 4 and the New Vegas engine
                // will not load them.
                PluginLikeExtensions = [KnownCEExtensions.ESM, KnownCEExtensions.ESP],
                // Anchored, because these all belong in the game root and nowhere else:
                // the NVSE plugins that mods ship inside Data/NVSE/Plugins have names of
                // the same shape, and an unanchored pattern would hoist them out of Data.
                EngineFiles =
                [
                    // NVSE / xNVSE
                    @"^nvse_loader\.exe$",
                    @"^nvse_.*\.dll$",
                    // 4GB patcher, which replaces the game's Bink stub in the root folder
                    @"^binkw32\.dll$",
                    @"^fnv4gb\.exe$",
                    // Wrappers that mods load through: ENB and the various ASI loaders
                    @"^d3d9\.dll$",
                    @"^dinput8\.dll$",
                ],
            }.Build(),
        ];
    }

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        var folderName = (gameLocatorResult.Path / FalloutNVKnownPaths.EpicMarkerFile).FileExists
            ? FalloutNVKnownPaths.EpicAppDataFolderName
            : FalloutNVKnownPaths.AppDataFolderName;

        return new Dictionary<LocationId, AbsolutePath>()
        {
            { LocationId.Game, gameLocatorResult.Path },
            { LocationId.AppData, fileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory) / folderName },
            { LocationId.Preferences, fileSystem.GetKnownPath(KnownPath.MyGamesDirectory) / folderName },
        }.ToImmutableDictionary();
    }

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "FalloutNV.exe");

    public async ValueTask<PluginHeader?> ParsePlugin(Hash hash, RelativePath? name = null)
    {
        var fileName = name?.FileName.ToString() ?? "unknown.esm";
        await using var stream = await _streamSource.OpenAsync(hash);
        // Vanilla plugins may not be backed up yet on a fresh loadout, so the stream source
        // returns null. Matches Fallout 4 and Skyrim: the callers treat null as "unknown
        // plugin" and skip it rather than failing the sync.
        if (stream is null) return null;
        return await GamebryoPluginHeaderReader.ReadAsync(stream, fileName);
    }

    public GamePath PluginsFile => FalloutNVKnownPaths.PluginsFile;

    /// <inheritdoc />
    /// <remarks>
    /// New Vegas predates the asterisk format: its plugins file lists the active plugins
    /// and nothing else. The order of the lines is not the load order either - the engine
    /// sorts plugins by their file modification time - so this file only decides what is
    /// switched on.
    /// </remarks>
    public PluginsFileFormat PluginsFileFormat => PluginsFileFormat.ActiveOnly;

    /// <inheritdoc />
    public Extension[] PluginExtensions => KnownCEExtensions.GamebryoPluginFiles;
}
