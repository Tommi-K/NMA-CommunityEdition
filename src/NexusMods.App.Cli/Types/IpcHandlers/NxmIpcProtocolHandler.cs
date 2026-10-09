using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Sdk.EventBus;
using NexusMods.Abstractions.GOG;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Collections;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.IndexSegments;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Networking.NexusWebApi.Auth;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Tracking;

namespace NexusMods.CLI.Types.IpcHandlers;

/// <summary>
/// a handler for nxm:// urls
/// </summary>
// ReSharper disable once InconsistentNaming
public class NxmIpcProtocolHandler : IIpcProtocolHandler
{
    /// <inheritdoc/>
    public string Protocol => "nxm";

    private readonly ILogger<NxmIpcProtocolHandler> _logger;
    private readonly ILoginManager _loginManager;
    private readonly OAuth _oauth;
    private readonly IGameDomainToGameIdMappingCache _cache;

    private readonly IServiceProvider _serviceProvider;
    private readonly IClient _client;
    private readonly IEventBus _eventBus;

    /// <summary>
    /// Collection revisions this handler has downloads running for, so that the same link
    /// twice over doesn't ask for the same files twice.
    /// </summary>
    private readonly ConcurrentDictionary<CollectionRevisionMetadataId, byte> _downloadingCollections = new();

    /// <summary>
    /// constructor
    /// </summary>
    public NxmIpcProtocolHandler(
        IServiceProvider serviceProvider,
        ILogger<NxmIpcProtocolHandler> logger, 
        OAuth oauth,
        IClient client,
        IGameDomainToGameIdMappingCache cache,
        ILoginManager loginManager)
    {
        _serviceProvider = serviceProvider;
        _eventBus = serviceProvider.GetRequiredService<IEventBus>();

        _logger = logger;
        _oauth = oauth;
        _client = client;
        _cache = cache;
        _loginManager = loginManager;
    }

    /// <inheritdoc/>
    public async Task Handle(string url, CancellationToken cancel, ProtocolLinkSource source = ProtocolLinkSource.External)
    {
        var parsed = NXMUrl.Parse(url);

        // NOTE(erri120): don't log OAuth callbacks, they contain sensitive information
        if (parsed is not NXMOAuthUrl && parsed is not NXMGogAuthUrl) _logger.LogDebug("Received NXM URL: {Url}", parsed.ToString());
        else _logger.LogDebug("Received URL of type {Type}", parsed.GetType());

        var isUserLogged = await _loginManager.GetIsUserLoggedInAsync(cancel);
        switch (parsed)
        {
            case NXMOAuthUrl oauthUrl:
                _oauth.AddUrl(oauthUrl);
                break;
            case NXMGogAuthUrl gogUrl:
                _client.AuthUrl(gogUrl);
                break;
            case NXMProtocolRegistrationCheck protocolRegistrationTest:
                _eventBus.Send(new CliMessages.TestProtocolRegistration(protocolRegistrationTest.Id));
                break;
            case NXMModUrl modUrl:
                await HandleModUrl(modUrl, cancel, source);
                break;
            case NXMCollectionUrl collectionUrl:
                await HandleCollectionUrl(collectionUrl, cancel, source);
                break;
            default:
                _logger.LogWarning("Unknown NXM URL type: {Url}", parsed);
                break;
        }
    }

    private async Task HandleCollectionUrl(NXMCollectionUrl collectionUrl, CancellationToken cancel, ProtocolLinkSource source)
    {
        var isUserLogged = await _loginManager.GetIsUserLoggedInAsync(cancel);
        if (!isUserLogged)
        {
            _logger.LogWarning("Download failed: User is not logged in");
            _eventBus.Send(new CliMessages.CollectionAddFailed(new FailureReason.NotLoggedIn()));
            return;
        }
        
        var domain = GameDomain.From(collectionUrl.Game);
        var nexusModsLibrary = _serviceProvider.GetRequiredService<NexusModsLibrary>();
        var library = _serviceProvider.GetRequiredService<ILibraryService>();
        var connection = _serviceProvider.GetRequiredService<IConnection>();

        var game = GetManagedGameFor(domain);
        if (game is null)
        {
            _logger.LogWarning("Collection add aborted: {Game} is not a managed game", collectionUrl.Game);
            _eventBus.Send(new CliMessages.CollectionAddFailed(new FailureReason.GameNotManaged(collectionUrl.Game)));
            return;
        }
                    
        var temporaryFileManager = _serviceProvider.GetRequiredService<TemporaryFileManager>();
        _eventBus.Send(new CliMessages.CollectionAddStarted(source));

        try
        {
            await using var destination = temporaryFileManager.CreateFile();

            var slug = collectionUrl.Collection.Slug;
            var revision = collectionUrl.Revision;

            var db = connection.Db;
            var list = db.Datoms(
                (NexusModsCollectionLibraryFile.CollectionSlug, slug),
                (NexusModsCollectionLibraryFile.CollectionRevisionNumber, revision)
            );

            var sw = Stopwatch.StartNew();
            if (!list.Select(id => NexusModsCollectionLibraryFile.Load(db, id)).TryGetFirst(x => x.IsValid(), out var collectionFile))
            {
                var downloadJob = nexusModsLibrary.CreateCollectionDownloadJob(destination, collectionUrl.Collection.Slug, collectionUrl.Revision, CancellationToken.None);
                var libraryFile = await library.AddDownload(downloadJob);

                if (!libraryFile.TryGetAsNexusModsCollectionLibraryFile(out collectionFile))
                    throw new InvalidOperationException("The library file is not a NexusModsCollectionLibraryFile");
            }

            var collectionRevision = await nexusModsLibrary.GetOrAddCollectionRevision(collectionFile, collectionUrl.Collection.Slug, collectionUrl.Revision, CancellationToken.None);
            Events.CollectionsDownloadCompleted(
                collectionId: collectionRevision.Collection.CollectionId.Value,
                revisionId: collectionRevision.RevisionId.Value,
                gameId: collectionRevision.Collection.GameId.Value,
                modCount: collectionRevision.Downloads.Count,
                duration: sw
            );

            _eventBus.Send(new CliMessages.CollectionAddSucceeded(collectionRevision));

            StartDownloadingCollection(collectionRevision);
        }
        catch (Exception e)
        {
            _eventBus.Send(new CliMessages.CollectionAddFailed(new FailureReason.Unknown(e)));
            throw;
        }
    }

    /// <summary>
    /// Starts downloading the collection's required files.
    /// </summary>
    /// <remarks>
    /// An <c>nxm://</c> collection link asks for the collection to be downloaded, not just
    /// to be put in the library, so the files are asked for here rather than waiting for the
    /// user to press the button on the page that has just opened for them.
    ///
    /// Deliberately not awaited: the download is a job, and the job monitor owns it from here
    /// -- it goes on running, and reporting its progress, whichever caller handed us the link
    /// and whether or not that caller is still waiting. Without premium a collection is
    /// downloaded a page at a time and can take the better part of an hour, which is far too
    /// long to hold a protocol handler open for.
    /// </remarks>
    private void StartDownloadingCollection(CollectionRevisionMetadata.ReadOnly revision)
    {
        // The same link arriving twice -- a second click, or a page that fires it again --
        // should not ask for every file a second time. Kept here rather than read off the
        // job monitor, whose job list is bound for the UI and not safe to walk from the
        // thread a protocol handler happens to run on.
        if (!_downloadingCollections.TryAdd(revision.Id, 0))
        {
            _logger.LogInformation("`{CollectionName}` is already being downloaded, leaving that to finish", revision.Collection.Name);
            return;
        }

        var collectionDownloader = _serviceProvider.GetRequiredService<CollectionDownloader>();
        var connection = _serviceProvider.GetRequiredService<IConnection>();

        _logger.LogInformation("Downloading the required files of `{CollectionName}` revision {RevisionNumber}", revision.Collection.Name, revision.RevisionNumber);

        _ = Task.Run(async () =>
        {
            try
            {
                await collectionDownloader.DownloadItems(
                    revision,
                    itemType: CollectionDownloader.ItemType.Required,
                    db: connection.Db,
                    cancellationToken: CancellationToken.None
                );
            }
            catch (OperationCanceledException)
            {
                // Cancelled from the jobs view, where it is already reported.
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Exception while downloading `{CollectionName}` for an nxm link", revision.Collection.Name);
            }
            finally
            {
                _downloadingCollections.TryRemove(revision.Id, out _);
            }
        });
    }

    private async Task HandleModUrl(NXMModUrl modUrl, CancellationToken cancel, ProtocolLinkSource source)
    {
        var isUserLogged = await _loginManager.GetIsUserLoggedInAsync(cancel);
        if (!isUserLogged)
        {
            _logger.LogWarning("Download failed: User is not logged in");
            _eventBus.Send(new CliMessages.ModDownloadFailed(new FailureReason.NotLoggedIn()));
            return;
        }
        
        var nexusModsLibrary = _serviceProvider.GetRequiredService<NexusModsLibrary>();

        var (alreadyDownloaded, items) = await nexusModsLibrary.IsAlreadyDownloaded(modUrl, cancellationToken: cancel);
        if (alreadyDownloaded)
        {
            _logger.LogInformation("File `{Game}/{ModId}/{FileId}` has already been downloaded and will be skipped", modUrl.Game, modUrl.ModId, modUrl.FileId);
            _eventBus.Send(new CliMessages.ModDownloadFailed(new FailureReason.AlreadyExists(items.First().AsLibraryItem().Name)));
            return;
        }
        
        var domain = GameDomain.From(modUrl.Game);
        var game = GetManagedGameFor(domain);
        if (game is null)
        {
            _logger.LogWarning("Mod download aborted: {Game} is not a managed game", modUrl.Game);
            _eventBus.Send(new CliMessages.ModDownloadFailed(new FailureReason.GameNotManaged(modUrl.Game)));
            return;
        }

        var library = _serviceProvider.GetRequiredService<ILibraryService>();
        var temporaryFileManager = _serviceProvider.GetRequiredService<TemporaryFileManager>();

        _eventBus.Send(new CliMessages.ModDownloadStarted(source));
        
        LibraryFile.ReadOnly? libraryFile = null;
        try
        {
            await using var destination = temporaryFileManager.CreateFile();
            var downloadJob = await nexusModsLibrary.CreateDownloadJob(destination, modUrl, cancellationToken: cancel);

            libraryFile = await library.AddDownload(downloadJob);
            
            _eventBus.Send(new CliMessages.ModDownloadSucceeded(libraryFile.Value.AsLibraryItem(), source));
        }
        catch (TaskCanceledException)
        {
            // User-initiated cancellation should not be treated as an error
            _logger.LogInformation("Mod download cancelled by user for {Game}/{ModId}/{FileId}", modUrl.Game, modUrl.ModId, modUrl.FileId);
            // Don't rethrow TaskCanceledException for user-initiated cancellations
            // Don't send ModDownloadFailed event for user-initiated cancellations
        }
        catch (Exception e)
        {
            _eventBus.Send(new CliMessages.ModDownloadFailed(new FailureReason.Unknown(e)));
            throw;
        }
    }
    
    
    private GameInstallation? GetManagedGameFor(GameDomain domain)
    {
        var gameRegistry = _serviceProvider.GetRequiredService<IGameRegistry>();
        var connection = _serviceProvider.GetRequiredService<IConnection>();
        var syncService = _serviceProvider.GetRequiredService<ISynchronizerService>();

        var gameId = _cache[domain];
        foreach (var installedGame in gameRegistry.LocateGameInstallations())
        {
            if (installedGame.Game.NexusModsGameId != gameId) continue;
            if (syncService.TryGetLastAppliedLoadout(installedGame, out _)) return installedGame;

            var activeLoadouts = Loadout.All(connection.Db).Where(ld => ld.Game.NexusModsGameId == installedGame.Game.NexusModsGameId);
            if (!activeLoadouts.Any()) continue;
            return installedGame;
        }

        return null;
    }
}

