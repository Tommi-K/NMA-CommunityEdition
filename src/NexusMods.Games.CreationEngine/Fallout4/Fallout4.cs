using System.Collections.Immutable;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Plugins.Records;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.Emitters;
using NexusMods.Games.CreationEngine.Installers;
using NexusMods.Games.FOMOD;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;

namespace NexusMods.Games.CreationEngine.Fallout4;

public class Fallout4 : ICreationEngineGame, IGameWithDlc, IGameData<Fallout4>
{
    private readonly IStreamSourceDispatcher _streamSource;

    public static GameId GameId { get; } = GameId.From("CreationEngine.Fallout4");
    public static string DisplayName => "Fallout 4";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(1151);

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [377160u],
        GOGProductIds = [1998527297L],
        EGSCatalogItemId = ["61d52ce4d09d41e48800c22784d13ae8"],
        XboxPackageIdentifiers = ["BethesdaSoftworks.Fallout4-PC"],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<Fallout4>("NexusMods.Games.CreationEngine.Resources.Fallout4.thumbnail.webp");
    public IStreamFactory TileImage { get; } = new EmbeddedResourceStreamFactory<Fallout4>("NexusMods.Games.CreationEngine.Resources.Fallout4.tile.webp");

    private readonly Lazy<ILoadoutSynchronizer> _synchronizer;
    public ILoadoutSynchronizer Synchronizer => _synchronizer.Value;
    public ILibraryItemInstaller[] LibraryItemInstallers { get; }
    private readonly Lazy<ISortOrderManager> _sortOrderManager;
    public ISortOrderManager SortOrderManager => _sortOrderManager.Value;
    public IDiagnosticEmitter[] DiagnosticEmitters { get; }

    public Fallout4(IServiceProvider provider)
    {
        _streamSource = provider.GetRequiredService<IStreamSourceDispatcher>();

        _synchronizer = new Lazy<ILoadoutSynchronizer>(() => new Fallout4Synchronizer(provider, this));
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
                GameAliases = ["Fallout 4", "Fallout4", "FO4", "F4"],
                TopLevelDirs = KnownPaths.CommonTopLevelFolders,
                StopPatterns = ["(^|/)f4se(/|$)"],
                EngineFiles = [
                    // F4SE
                    @"f4se_loader\.exe", 
                    @"f4se_.*\.dll",
                    // Plugin Preloader (new
                    @"winhttp\.dll",
                    @"xSE\ PluginPreloader\.xml",
                    // Plugin Preloader (old)
                    @"IpHlpAPI\.dll",
                ],
            }.Build(),
        ];
    }

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        return new Dictionary<LocationId, AbsolutePath>()
        {
            { LocationId.Game, gameLocatorResult.Path },
            { LocationId.AppData, fileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory) / "Fallout4" },
            { LocationId.Preferences, fileSystem.GetKnownPath(KnownPath.MyGamesDirectory) / "Fallout4" },
        }.ToImmutableDictionary();
    }

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "Fallout4.exe");

    private static readonly GroupMask EmptyGroupMask = new(false);
    public async ValueTask<PluginHeader?> ParsePlugin(Hash hash, RelativePath? name = null)
    {
        var fileName = name?.FileName.ToString() ?? "unknown.esm";
        var key = ModKey.FromFileName(fileName);
        await using var stream = await _streamSource.OpenAsync(hash);
        // Vanilla plugins may not be backed up yet on a fresh loadout, so the
        // stream source returns null. Return null instead of NRE-crashing in
        // Mutagen; MissingMasterEmitter treats a null result as "unknown mod"
        // and skips the diagnostic gracefully.
        if (stream is null) return null;
        var meta = ParsingMeta.Factory(BinaryReadParameters.Default, GameRelease.Fallout4, key, stream);
        await using var mutagenStream = new MutagenBinaryReadStream(stream, meta);
        using var frame = new MutagenFrame(mutagenStream);
        IMod mod = Fallout4Mod.CreateFromBinary(frame, Fallout4Release.Fallout4, EmptyGroupMask);
        return new PluginHeader(mod.ModKey, mod.MasterReferences.Select(static m => m.Master).ToArray());
    }

    public GamePath PluginsFile => Fallout4KnownPaths.PluginsFile;

    /// <inheritdoc />
    /// <remarks>
    /// Bethesda's own add-ons, in release order. Fallout 4 activates these through
    /// <c>plugins.txt</c> (its <c>Fallout4.ccc</c> lists only Creation Club content), so leaving a
    /// plugin out of the generated list is enough to switch the content off - the matching
    /// <c>.ba2</c> archives are named after the plugin and are loaded with it.
    /// </remarks>
    public IReadOnlyList<GameDlc> Dlc { get; } =
    [
        new GameDlc { Id = "DLCRobot", Name = "Automatron", Plugin = KnownPaths.Data / "DLCRobot.esm" },
        new GameDlc { Id = "DLCworkshop01", Name = "Wasteland Workshop", Plugin = KnownPaths.Data / "DLCworkshop01.esm" },
        new GameDlc { Id = "DLCCoast", Name = "Far Harbor", Plugin = KnownPaths.Data / "DLCCoast.esm" },
        new GameDlc { Id = "DLCworkshop02", Name = "Contraptions Workshop", Plugin = KnownPaths.Data / "DLCworkshop02.esm" },
        new GameDlc { Id = "DLCworkshop03", Name = "Vault-Tec Workshop", Plugin = KnownPaths.Data / "DLCworkshop03.esm" },
        new GameDlc { Id = "DLCNukaWorld", Name = "Nuka-World", Plugin = KnownPaths.Data / "DLCNukaWorld.esm" },
        new GameDlc { Id = "DLCUltraHighResolution", Name = "High Resolution Texture Pack", Plugin = KnownPaths.Data / "DLCUltraHighResolution.esm" },
    ];
}
