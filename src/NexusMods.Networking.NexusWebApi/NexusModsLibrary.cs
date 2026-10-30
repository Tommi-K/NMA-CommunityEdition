using System.Collections.Concurrent;
using System.Text.Json;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.DTOs;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Abstractions.Telemetry;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.HttpDownloader;
using NexusMods.Networking.NexusWebApi.Extensions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Networking.NexusWebApi;

/// <summary>
/// Methods for connecting the Nexus Mods API with the Library.
/// </summary>
[PublicAPI]
public partial class NexusModsLibrary
{
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnection _connection;
    private readonly INexusApiClient _apiClient;
    private readonly IGraphQlClient _graphQlClient;
    private readonly HttpClient _httpClient;
    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly IFileStore _fileStore;
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly IGameDomainToGameIdMappingCache _mappingCache;

    /// <summary>
    /// Constructor.
    /// </summary>
    public NexusModsLibrary(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = serviceProvider.GetRequiredService<ILogger<NexusModsLibrary>>();
        _httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _apiClient = serviceProvider.GetRequiredService<INexusApiClient>();
        _temporaryFileManager = serviceProvider.GetRequiredService<TemporaryFileManager>();
        _fileStore = serviceProvider.GetRequiredService<IFileStore>();
        _jsonSerializerOptions = serviceProvider.GetRequiredService<JsonSerializerOptions>();
        _mappingCache = serviceProvider.GetRequiredService<IGameDomainToGameIdMappingCache>();
        _graphQlClient = serviceProvider.GetRequiredService<IGraphQlClient>();
    }

    /// <summary>
    /// Whether the account is premium, read from the user info the login manager already
    /// holds, so it costs no request of its own. Nobody being logged in counts as not premium.
    /// </summary>
    private static bool IsPremium(IServiceProvider serviceProvider)
    {
        return serviceProvider.GetService<ILoginManager>()?.IsPremium == true;
    }

    /// <summary>
    /// Whether downloads go straight to fetching the file, without asking the server about it
    /// first: everyone's but a premium account's do, which saves a round trip per file.
    /// </summary>
    internal static bool SkipsMetadataRequest(IServiceProvider serviceProvider) => !IsPremium(serviceProvider);

    /// <summary>
    /// How many downloads an account without premium has going at once.
    /// </summary>
    /// <remarks>
    /// Nexus Mods caps such an account's downloads as a whole rather than one by one, so every
    /// download running at the same time takes a share of the same few MB/s. A collection's
    /// download pages hand over dozens a minute, and with all of them running at once each one
    /// crawled along at a fraction of the cap, the slowest went quiet for long enough to be
    /// taken for stalled, and a large one could outlast the four hours its link is good for.
    /// A few at a time keep the cap just as full, and get each file in far sooner.
    /// </remarks>
    private const int MaxConcurrentDownloadsWithoutPremium = 4;

    private readonly SemaphoreSlim _downloadTurns = new(MaxConcurrentDownloadsWithoutPremium);

    /// <summary>
    /// Cancelled, and replaced, to drop every download still waiting for its turn.
    /// </summary>
    private CancellationTokenSource _waitingDownloads = new();

    /// <summary>
    /// Waits until a download may start, returning what to dispose of once its file has been
    /// transferred.
    /// </summary>
    /// <remarks>
    /// Only accounts without premium wait; see <see cref="MaxConcurrentDownloadsWithoutPremium"/>.
    /// Wait before asking for the file's download link: the link lasts four hours, while the
    /// key a download page hands over lasts two days.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled, or <see cref="DropWaitingDownloads"/> called, before the turn came.
    /// </exception>
    public async ValueTask<IDisposable> WaitForDownloadTurn(CancellationToken cancellationToken)
    {
        if (IsPremium(_serviceProvider)) return new DownloadTurn(turns: null);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Volatile.Read(ref _waitingDownloads).Token);
        await _downloadTurns.WaitAsync(cancellation.Token);
        return new DownloadTurn(_downloadTurns);
    }

    /// <summary>
    /// Drops every download that is waiting for its turn, leaving the ones under way alone.
    /// </summary>
    /// <remarks>
    /// A waiting download isn't a job yet, so nothing on the downloads page can stop it; this
    /// is what cancelling a run of downloads, such as a collection's, does with them instead.
    /// </remarks>
    public void DropWaitingDownloads()
    {
        // Not disposed: a waiter may be reading its token at this very moment.
        Interlocked.Exchange(ref _waitingDownloads, new CancellationTokenSource()).Cancel();
    }

    /// <summary>
    /// A download's place among the ones running, given back once.
    /// </summary>
    private sealed class DownloadTurn(SemaphoreSlim? turns) : IDisposable
    {
        private int _isReleased;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isReleased, 1) == 0) turns?.Release();
        }
    }

    /// <summary>
    /// Files with a download queued or under way that has been handed over by a download page.
    /// </summary>
    private readonly ConcurrentDictionary<FileUid, byte> _filesBeingDownloaded = new();

    /// <summary>
    /// The file an <c>nxm://</c> link is for.
    /// </summary>
    public FileUid GetFileUid(NXMModUrl url) => new(url.FileId, _mappingCache[GameDomain.From(url.Game)]);

    /// <summary>
    /// Whether a download of <paramref name="file"/> is queued or under way.
    /// </summary>
    /// <remarks>
    /// A file only counts as downloaded once it is in the library, and without premium that can
    /// be a long while after its download page handed it over. Going over the same collection
    /// again meanwhile -- pressing Download a second time, say -- would otherwise start every one
    /// of those files a second time, each copy taking a share of the same capped speed.
    /// </remarks>
    public bool IsBeingDownloaded(FileUid file) => _filesBeingDownloaded.ContainsKey(file);

    /// <summary>
    /// Marks <paramref name="file"/> as being downloaded, unless it already is.
    /// </summary>
    /// <returns>
    /// Null when a download of it is already queued or under way; otherwise what to dispose of
    /// once this download is over, however it ends.
    /// </returns>
    public IDisposable? TryClaimDownload(FileUid file)
    {
        return _filesBeingDownloaded.TryAdd(file, 0) ? new DownloadClaim(_filesBeingDownloaded, file) : null;
    }

    private sealed class DownloadClaim(ConcurrentDictionary<FileUid, byte> files, FileUid file) : IDisposable
    {
        private int _isReleased;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isReleased, 1) == 0) files.TryRemove(file, out _);
        }
    }

    public async Task<NexusModsModPageMetadata.ReadOnly> GetOrAddModPage(
        ModId modId,
        NexusModsGameId nexusModsGameId,
        CancellationToken cancellationToken = default)
    {
        var uid = new ModUid(modId, nexusModsGameId);
        var modPageEntities = NexusModsModPageMetadata.FindByUid(_connection.Db, uid);
        if (modPageEntities.TryGetFirst(x => x.Uid.GameId == nexusModsGameId, out var modPage)) return modPage;

        using var tx = _connection.BeginTransaction();

        var modResult = await _graphQlClient.QueryMod(uid.ModId, uid.GameId, cancellationToken);
        // TODO: handle errors
        var mod = modResult.AssertHasData();

        var modEntityId = mod.Resolve(_connection.Db, tx, setFilesTimestamp: true);
        await ResolveAllFilesInModPage(uid, tx, modEntityId, cancellationToken);

        var txResults = await tx.Commit();
        return NexusModsModPageMetadata.Load(txResults.Db, txResults[modEntityId]);
    }

    private async Task ResolveAllFilesInModPage(ModUid modUid, ITransaction tx, EntityId modPageId, CancellationToken cancellationToken)
    {
        // Note(sewer):
        // Make sure to also fetch all files on the mod page.
        // The update code refreshes file info only on changes of the mod page.
        // If our initial mod page item does not contain info on all the files,
        // then updates are not visible unless an actual change is made to the
        // mod page, this is somewhat undesireable.
        var result = await _graphQlClient.QueryModFiles(modUid.ModId, modUid.GameId, cancellationToken: cancellationToken);

        // TODO: handle errors
        var modFiles = result.AssertHasData();

        foreach (var modFile in modFiles)
        {
            modFile.Resolve(_connection.Db, tx, modPageId);
        }
    }

    public async Task<NexusModsFileMetadata.ReadOnly> GetOrAddFile(
        FileId fileId,
        NexusModsModPageMetadata.ReadOnly modPage,
        CancellationToken cancellationToken = default)
    {
        var uid = new FileUid(fileId, modPage.Uid.GameId);
        var fileEntities = NexusModsFileMetadata.FindByUid(_connection.Db, uid);
        if (fileEntities.TryGetFirst(x => x.ModPageId == modPage, out var file))
            return file;

        var modFileResult = await _graphQlClient.QueryModFile(fileId, modPage.Uid.GameId, cancellationToken);
        // TODO: handle errors
        var modFile = modFileResult.AssertHasData();

        using var tx = _connection.BeginTransaction();

        var size = Size.FromLong(long.Parse(modFile.SizeInBytes ?? "0"));
        var newFile = new NexusModsFileMetadata.New(tx)
        {
            Name = modFile.Name,
            Version = modFile.Version,
            ModPageId = modPage,
            Uid = uid,
            UploadedAt = DateTimeOffset.FromUnixTimeSeconds(modFile.Date).UtcDateTime,
            Size = size,
        };

        var txResults = await tx.Commit();
        return txResults.Remap(newFile);
    }

    /// <summary>
    /// Finds the file to download from a mod page when no particular one was asked for: the
    /// page's primary main file, else its newest main file, else its newest file that is still
    /// current. Null when the page has nothing current to download.
    /// </summary>
    public async Task<NexusModsFileMetadata.ReadOnly?> GetOrAddMainFile(
        NexusModsModPageMetadata.ReadOnly modPage,
        CancellationToken cancellationToken = default)
    {
        var result = await _graphQlClient.QueryModFileSummaries(modPage.Uid, cancellationToken);
        if (!result.TryGetData(out var files)) return null;

        var chosen = files
            .Where(static file => file.IsCurrent)
            .OrderByDescending(static file => file.IsMain)
            .ThenByDescending(static file => file.IsPrimary)
            .ThenByDescending(static file => file.UploadedAt)
            .FirstOrDefault();

        if (chosen is null) return null;
        return await GetOrAddFile(chosen.FileId, modPage, cancellationToken);
    }

    public async Task<Uri> GetDownloadUri(
        NexusModsFileMetadata.ReadOnly file,
        Optional<(NXMKey, DateTime)> nxmData,
        CancellationToken cancellationToken = default)
    {
        Abstractions.NexusWebApi.DTOs.Response<DownloadLink[]> links;

        if (nxmData.HasValue)
        {
            // NOTE(erri120): the key and expiration date are required for free users to be able to download anything
            var (key, expirationDate) = nxmData.Value;
            links = await _apiClient.DownloadLinksAsync(
                file.ModPage.GameDomain.ToString(),
                file.ModPage.Uid.ModId,
                file.Uid.FileId,
                key: key,
                expireTime: expirationDate,
                token: cancellationToken
            );
        }
        else
        {
            // NOTE(erri120): premium-only API
            links = await _apiClient.DownloadLinksAsync(
                file.ModPage.GameDomain.ToString(),
                file.ModPage.Uid.ModId,
                file.Uid.FileId,
                token: cancellationToken
            );
        }

        // NOTE(erri120): The first download link is the preferred download location as
        // set by the user in their settings. By default, this will be the CDN, which
        // is going to be the fastest location 99% of the time.
        return links.Data.First().Uri;
    }

    /// <summary>
    /// Checks whether the file has already been downloaded.
    /// </summary>
    public async ValueTask<(bool, NexusModsLibraryItem.ReadOnly[])> IsAlreadyDownloaded(NXMModUrl url, CancellationToken cancellationToken)
    {
        var gameId = _mappingCache[GameDomain.From(url.Game)];

        var modPage = await GetOrAddModPage(url.ModId, gameId, cancellationToken);
        var file = await GetOrAddFile(url.FileId, modPage, cancellationToken);

        var foundItems = NexusModsLibraryItem.FindByFileMetadata(file.Db, file);
        return (foundItems.Count != 0, foundItems.ToArray());
    }

    /// <summary>
    /// Parse a NXM URL and create a download job from the data
    /// </summary>
    public async Task<IJobTask<NexusModsDownloadJob, AbsolutePath>> CreateDownloadJob(
        AbsolutePath destination,
        NXMModUrl url,
        CancellationToken cancellationToken)
    {
        var nxmData = url.Key is not null && url.ExpireTime is not null ? (url.Key.Value, url.ExpireTime.Value) : Optional.None<(NXMKey, DateTime)>();
        var gameId = _mappingCache[GameDomain.From(url.Game)];
        return await CreateDownloadJob(destination, gameId, url.ModId, url.FileId, nxmData, cancellationToken);
    }

    /// <summary>
    /// Given a mod ID, file ID, and game domain, create a download job
    /// </summary>
    public async Task<IJobTask<NexusModsDownloadJob, AbsolutePath>> CreateDownloadJob(
        AbsolutePath destination,
        NexusModsGameId nexusModsGameId,
        ModId modId,
        FileId fileId,
        Optional<(NXMKey, DateTime)> nxmData = default,
        CancellationToken cancellationToken = default)
    {
        var modPage = await GetOrAddModPage(modId, nexusModsGameId, cancellationToken);
        var file = await GetOrAddFile(fileId, modPage, cancellationToken);

        var uri = await GetDownloadUri(file, nxmData, cancellationToken: cancellationToken);

        var domain = _mappingCache[nexusModsGameId];
        var httpJob = HttpDownloadJob.Create(_serviceProvider, uri, NexusModsUrlBuilder.GetModUri(domain, modId), destination, skipMetadataRequest: SkipsMetadataRequest(_serviceProvider));
        var nexusJob = NexusModsDownloadJob.Create(_serviceProvider, httpJob, file);

        return nexusJob;
    }

    public async Task<IJobTask<NexusModsDownloadJob, AbsolutePath>> CreateDownloadJob(
        AbsolutePath destination,
        NexusModsFileMetadata.ReadOnly fileMetadata,
        Optional<CollectionRevisionMetadata.ReadOnly> parentRevision = default,
        CancellationToken cancellationToken = default)
    {
        var uri = await GetDownloadUri(fileMetadata, Optional<(NXMKey, DateTime)>.None, cancellationToken: cancellationToken);

        var domain = _mappingCache[fileMetadata.Uid.GameId];
        var httpJob = HttpDownloadJob.Create(_serviceProvider, uri, NexusModsUrlBuilder.GetModUri(domain, fileMetadata.ModPage.Uid.ModId), destination, skipMetadataRequest: SkipsMetadataRequest(_serviceProvider));
        var nexusJob = NexusModsDownloadJob.Create(_serviceProvider, httpJob, fileMetadata, parentRevision: parentRevision);

        return nexusJob;
    }

    /// <summary>
    /// Create a job that downloads a collection
    /// </summary>
    /// <param name="destination">Download location</param>
    /// <param name="slug">The collection slug</param>
    /// <param name="revision">The revision of the collection download</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public IJobTask<NexusModsCollectionDownloadJob, AbsolutePath> CreateCollectionDownloadJob(
        AbsolutePath destination,
        CollectionSlug slug,
        RevisionNumber revision,
        CancellationToken cancellationToken)
    {
        return NexusModsCollectionDownloadJob.Create(_serviceProvider, slug, revision, destination);
    }
}
