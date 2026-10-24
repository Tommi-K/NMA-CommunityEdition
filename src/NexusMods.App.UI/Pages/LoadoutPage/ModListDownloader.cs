using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.Telemetry;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.App.UI.Pages.LoadoutPage;

/// <summary>
/// Downloads the mods an imported mod list names that the loadout doesn't have, and installs them.
/// </summary>
/// <remarks>
/// The same two routes as <see cref="ModUpdateDownloader"/>: a premium account is handed each file
/// by the API, and everyone else has each mod's download page driven in the app's own browser tab,
/// one after another. That second route only hands the download over; the file arrives in the
/// library some time later, so it is waited for before being installed.
/// </remarks>
[UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
internal sealed class ModListDownloader
{
    // How long to keep waiting for downloads that were handed over when none has arrived for a
    // while. A free account's speed is capped, so a single big mod can take this long on its own;
    // past it, the rest are taken to have failed - the site turning the download down, say.
    private static readonly TimeSpan ArrivalInactivityTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan ArrivalPollInterval = TimeSpan.FromSeconds(2);

    private const int MaxParallelApiDownloads = 3;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ModListDownloader> _logger;
    private readonly IConnection _connection;
    private readonly ILoginManager _loginManager;
    private readonly NexusModsLibrary _nexusModsLibrary;
    private readonly ILibraryService _libraryService;
    private readonly ILoadoutManager _loadoutManager;
    private readonly IGameDomainToGameIdMappingCache _mappingCache;
    private readonly TemporaryFileManager _temporaryFileManager;

    public ModListDownloader(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = serviceProvider.GetRequiredService<ILogger<ModListDownloader>>();
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _loginManager = serviceProvider.GetRequiredService<ILoginManager>();
        _nexusModsLibrary = serviceProvider.GetRequiredService<NexusModsLibrary>();
        _libraryService = serviceProvider.GetRequiredService<ILibraryService>();
        _loadoutManager = serviceProvider.GetRequiredService<ILoadoutManager>();
        _mappingCache = serviceProvider.GetRequiredService<IGameDomainToGameIdMappingCache>();
        _temporaryFileManager = serviceProvider.GetRequiredService<TemporaryFileManager>();
    }

    /// <summary>
    /// Downloads each of <paramref name="mods"/> that has a Nexus Mods source, unless the library
    /// already has its file, and installs it into the loadout.
    /// </summary>
    /// <param name="loadoutId">The loadout to install into.</param>
    /// <param name="mods">The mods to fetch, highest priority first, as the list has them.</param>
    /// <param name="parent">The collection to install into, or none for the loadout's default.</param>
    /// <param name="cancellationToken">Stops the run; whatever was installed stays installed.</param>
    public async ValueTask<ModListDownloadResult> DownloadAndInstall(
        LoadoutId loadoutId,
        IReadOnlyList<ModListMissingMod> mods,
        Optional<LoadoutItemGroupId> parent,
        CancellationToken cancellationToken)
    {
        var failed = new List<string>();

        // Lowest priority first, so that each install lands above the ones before it, which is the
        // order the list wants. The import that follows puts every one of them in place anyway.
        var files = new List<(ModListMissingMod Mod, NexusModsFileMetadata.ReadOnly File)>();
        foreach (var mod in mods.Reverse())
        {
            if (mod.Nexus is not { } source)
            {
                failed.Add($"{mod.Name}: the list doesn't say where to download it from");
                continue;
            }

            var file = await TryResolveFile(source, cancellationToken);
            if (file is null)
            {
                failed.Add($"{mod.Name}: nothing to download on its Nexus Mods page ({source.GameDomain}/{source.ModId})");
                continue;
            }

            if (files.Any(existing => existing.File.Id == file.Value.Id)) continue;
            files.Add((mod, file.Value));
        }

        var toDownload = files.Where(pair => !TryGetLibraryItem(pair.File, out _)).ToArray();
        _logger.LogInformation("Importing a mod list: {Download} of {Count} mods to download, {Unresolved} without a file to download",
            toDownload.Length, files.Count, failed.Count);

        var notStarted = _loginManager.IsPremium
            ? await DownloadThroughApi(toDownload, failed, cancellationToken)
            : await DownloadThroughBrowser(toDownload, failed, cancellationToken);

        var installed = 0;
        var pending = files.Where(pair => !notStarted.Contains(pair.File.Id)).ToList();
        var lastArrival = DateTimeOffset.UtcNow;

        // Installed as they arrive rather than in list order, so that one download that never
        // comes doesn't hold up all the others behind it.
        while (pending.Count > 0)
        {
            var arrived = pending.Where(pair => TryGetLibraryItem(pair.File, out _)).ToArray();

            foreach (var (mod, file) in arrived)
            {
                pending.RemoveAll(pair => pair.File.Id == file.Id);
                if (!TryGetLibraryItem(file, out var libraryItem)) continue;

                try
                {
                    // Awaited one at a time: an installer with options asks the user, and two of
                    // those at once would be one too many.
                    await _loadoutManager.InstallItem(libraryItem.AsLibraryItem(), loadoutId, parent);
                    installed++;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _logger.LogError(e, "Failed to install `{Name}` from an imported mod list", mod.Name);
                    failed.Add($"{mod.Name}: couldn't be installed ({e.Message})");
                }
            }

            if (arrived.Length > 0) lastArrival = DateTimeOffset.UtcNow;
            if (pending.Count == 0) break;

            if (DateTimeOffset.UtcNow - lastArrival > ArrivalInactivityTimeout)
            {
                _logger.LogWarning("Gave up waiting for {Count} downloads of an imported mod list", pending.Count);
                failed.AddRange(pending.Select(static pair => $"{pair.Mod.Name}: the download didn't arrive"));
                break;
            }

            await Task.Delay(ArrivalPollInterval, cancellationToken);
        }

        return new ModListDownloadResult(installed, failed.ToArray());
    }

    /// <summary>
    /// The file a source names, or the main file of its page when it names none or the one it names
    /// is gone.
    /// </summary>
    private async ValueTask<NexusModsFileMetadata.ReadOnly?> TryResolveFile(ModListNexusSource source, CancellationToken cancellationToken)
    {
        try
        {
            var gameId = _mappingCache[source.GameDomain];
            var modPage = await _nexusModsLibrary.GetOrAddModPage(source.ModId, gameId, cancellationToken);

            if (source.FileId is { } fileId)
            {
                try
                {
                    return await _nexusModsLibrary.GetOrAddFile(fileId, modPage, cancellationToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Authors remove old files all the time; the mod's current file is the best
                    // stand-in for the one the list was made with.
                    _logger.LogInformation(e, "File {FileId} of {Domain}/{ModId} is gone, taking the page's main file", fileId, source.GameDomain, source.ModId);
                }
            }

            return await _nexusModsLibrary.GetOrAddMainFile(modPage, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Couldn't find {Domain}/{ModId} on Nexus Mods", source.GameDomain, source.ModId);
            return null;
        }
    }

    /// <returns>The files that failed, which there is no point waiting for.</returns>
    private async ValueTask<HashSet<EntityId>> DownloadThroughApi(
        (ModListMissingMod Mod, NexusModsFileMetadata.ReadOnly File)[] files,
        List<string> failed,
        CancellationToken cancellationToken)
    {
        var gate = new Lock();
        var notStarted = new HashSet<EntityId>();

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelApiDownloads, CancellationToken = cancellationToken },
            async (pair, token) =>
            {
                try
                {
                    await using var tempPath = _temporaryFileManager.CreateFile();
                    var job = await _nexusModsLibrary.CreateDownloadJob(tempPath, pair.File, cancellationToken: token);
                    await _libraryService.AddDownload(job);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _logger.LogError(e, "Failed to download `{Name}` for an imported mod list", pair.Mod.Name);
                    lock (gate)
                    {
                        failed.Add($"{pair.Mod.Name}: the download failed ({e.Message})");
                        notStarted.Add(pair.File.Id);
                    }
                }
            }
        );

        return notStarted;
    }

    /// <returns>The files whose page handed nothing over, which there is no point waiting for.</returns>
    private async ValueTask<HashSet<EntityId>> DownloadThroughBrowser(
        (ModListMissingMod Mod, NexusModsFileMetadata.ReadOnly File)[] files,
        List<string> failed,
        CancellationToken cancellationToken)
    {
        var notStarted = new HashSet<EntityId>();
        if (files.Length == 0) return notStarted;

        var inAppBrowser = _serviceProvider.GetService<IInAppBrowser>();
        var osInterop = _serviceProvider.GetRequiredService<IOSInterop>();

        // One tab for the whole run, brought to the front once and then left where it is.
        using var downloadSession = inAppBrowser?.BeginDownloadSession();

        foreach (var (mod, file) in files)
        {
            var uri = NexusModsUrlBuilder.GetFileDownloadUri(
                file.ModPage.GameDomain,
                file.ModPage.Uid.ModId,
                file.Uid.FileId,
                useNxmLink: true
            );

            if (inAppBrowser is not null)
            {
                // Awaited: one download page at a time is all the site allows a free account.
                var outcome = await inAppBrowser.StartDownload(uri, mod.Name, cancellationToken);
                if (outcome == InAppDownloadOutcome.Started) continue;

                if (outcome == InAppDownloadOutcome.NotStarted)
                {
                    // The run's tab moves on to the next page, so this one is the user's to fetch.
                    failed.Add($"{mod.Name}: its download page didn't start the download ({uri.GetLeftPart(UriPartial.Path)})");
                    notStarted.Add(file.Id);
                    continue;
                }
            }

            // No tab to drive: the page goes to the system browser, whose download comes back to
            // the app the usual way, and is waited for like the others.
            osInterop.OpenUri(uri);
        }

        return notStarted;
    }

    private bool TryGetLibraryItem(NexusModsFileMetadata.ReadOnly file, out NexusModsLibraryItem.ReadOnly libraryItem)
    {
        return NexusModsLibraryItem.FindByFileMetadata(_connection.Db, file).TryGetFirst(out libraryItem);
    }

}

/// <summary>
/// What <see cref="ModListDownloader.DownloadAndInstall"/> managed.
/// </summary>
/// <param name="Installed">How many mods were installed.</param>
/// <param name="Failed">What couldn't be fetched or installed, a line each, naming the mod and why.</param>
internal sealed record ModListDownloadResult(int Installed, string[] Failed);
