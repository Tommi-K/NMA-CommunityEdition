using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Backend.Games.Locators;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Backend.Games;

[UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
[Obsolete("built on ManuallyAddedGame, which is a hack that will be removed soon tm")]
internal class ManuallyAddedGameService : IManuallyAddedGameService
{
    private readonly ILogger _logger;
    private readonly IConnection _connection;
    private readonly IGameRegistry _gameRegistry;
    private readonly IServiceProvider _serviceProvider;

    public ManuallyAddedGameService(IServiceProvider serviceProvider)
    {
        _logger = serviceProvider.GetRequiredService<ILogger<ManuallyAddedGameService>>();
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _gameRegistry = serviceProvider.GetRequiredService<IGameRegistry>();
        _serviceProvider = serviceProvider;
    }

    public async Task<ManualGameAddResult> AddGame(AbsolutePath directory)
    {
        if (!directory.DirectoryExists())
            return new ManualGameAddResult(ManualGameAddStatus.DirectoryNotFound);

        if (!TryIdentifyGame(directory, out var game, out var probe))
        {
            _logger.LogInformation("No supported game recognised in `{Path}`", directory);
            return new ManualGameAddResult(ManualGameAddStatus.NotRecognized);
        }

        var gameId = game.NexusModsGameId.Value;
        var path = directory.ToString();

        // Don't register a second entry for a folder we already know about, whether it
        // was added manually before or a store locator found it on its own.
        var addedBefore = ManuallyAddedGame
            .All(_connection.Db)
            .Any(entity => entity.GameId == gameId && ArePathsEqual(entity.Path, path));

        var locatedAlready = _gameRegistry
            .LocateGameInstallations()
            .Any(installation => installation.Game.GameId == game.GameId && installation.LocatorResult.Path == directory);

        if (addedBefore || locatedAlready)
        {
            _logger.LogInformation("{Game} at `{Path}` is already known, not adding it again", game.DisplayName, directory);
            return new ManualGameAddResult(ManualGameAddStatus.AlreadyKnown, game);
        }

        var version = TryReadPrimaryFileVersion(probe);

        using var tx = _connection.BeginTransaction();
        _ = new ManuallyAddedGame.New(tx)
        {
            GameId = gameId,
            Path = path,
            // The hashes DB is the authority on versions; this is only what the
            // executable claims, and is empty for games that don't carry a version.
            Version = version ?? string.Empty,
        };

        await tx.Commit();

        // Locator results are cached, so the new entry is invisible until we drop it.
        _gameRegistry.ClearCache();

        _logger.LogInformation("Manually added {Game} (version `{Version}`) at `{Path}`", game.DisplayName, version ?? "unknown", directory);
        return new ManualGameAddResult(ManualGameAddStatus.Added, game, version);
    }

    /// <summary>
    /// Finds the supported game whose primary file lives in <paramref name="directory"/>.
    /// </summary>
    private bool TryIdentifyGame(
        AbsolutePath directory,
        [NotNullWhen(true)] out IGameData? game,
        [NotNullWhen(true)] out GameInstallation? installation)
    {
        game = null;
        installation = null;

        // Needed to build the provisional locator result we probe with.
        var locator = _serviceProvider.GetServices<IGameLocator>().OfType<ManuallyAddedLocator>().FirstOrDefault();
        if (locator is null)
        {
            _logger.LogError("{Locator} isn't registered, cannot add games manually", nameof(ManuallyAddedLocator));
            return false;
        }

        foreach (var candidate in _serviceProvider.GetServices<IGameData>())
        {
            // The locator can only yield games that have a Nexus Mods ID.
            if (!candidate.NexusModsGameId.HasValue) continue;

            try
            {
                var probe = BuildInstallation(candidate, directory, locator);
                var primaryFile = probe.Locations.ToAbsolutePath(candidate.GetPrimaryFile(probe));
                if (!primaryFile.FileExists) continue;

                game = candidate;
                installation = probe;
                return true;
            }
            catch (Exception e)
            {
                // A game whose locations or primary file can't be resolved for this
                // folder simply isn't the game we're looking at.
                _logger.LogDebug(e, "Failed to probe `{Path}` as {Game}", directory, candidate.DisplayName);
            }
        }

        return false;
    }

    private static GameInstallation BuildInstallation(IGameData game, AbsolutePath directory, IGameLocator locator)
    {
        var locatorResult = new GameLocatorResult
        {
            Game = game,
            Path = directory,
            Store = GameStore.ManuallyAdded,
            StoreIdentifier = directory.ToString(),
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = locator,
        };

        var locations = GameLocations.Create(game.GetLocations(directory.FileSystem, locatorResult));
        return new GameInstallation(locatorResult, locations);
    }

    /// <summary>
    /// Reads the version resource off the game's primary file, the same way loadout
    /// creation does when the hashes DB has no matching version definition.
    /// </summary>
    private static string? TryReadPrimaryFileVersion(GameInstallation installation)
    {
        try
        {
            var primaryFile = installation.Locations.ToAbsolutePath(installation.Game.GetPrimaryFile(installation));
            if (!primaryFile.FileExists) return null;

            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(primaryFile.ToNativeSeparators(OSInformation.Shared));
            var raw = info.ProductVersion ?? info.FileVersion;
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static bool ArePathsEqual(string left, string right) => string.Equals(
        left.TrimEnd('/', '\\'),
        right.TrimEnd('/', '\\'),
        OSInformation.Shared.IsLinux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase
    );
}
