using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.ModUpdates;
using NexusMods.Networking.ModUpdates.Mixins;
using NexusMods.Networking.ModUpdates.Traits;
using NexusMods.Networking.NexusWebApi.Extensions;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Networking.NexusWebApi;

/// <summary>
/// Utility class that encapsulates the logic for running the actual update check.
/// </summary>
public static class RunUpdateCheck
{
    /// <summary>
    /// Identifies all mod pages whose information needs refreshed.
    /// </summary>
    public static async Task<PerFeedCacheUpdaterResult<PageMetadataMixin>> CheckForModPagesWhichNeedUpdating(IDb db, INexusApiClient apiClient, IGameDomainToGameIdMappingCache mappingCache)
    {
        // Extract all GameDomain(s)
        var modPages = PageMetadataMixin.EnumerateDatabaseEntries(db).ToArray();
        var gameIds = modPages.Select(x => (x.GetModPageId().GameId)).Distinct().ToArray();
        
        // Note: The v1Timespan accounts for 1 month minus 5 minutes
        //  - We use 28 days because February is the shortest month at 28.
        //  - Serverside caches for 5 minutes, so we subtract that. 
        var v1Timespan = TimeSpan.FromDays(28).Subtract(TimeSpan.FromMinutes(5));
        var updater = new MultiFeedCacheUpdater<PageMetadataMixin>(modPages, v1Timespan);

        foreach (var gameId in gameIds)
        {
            // Note (sewer): We need to update to V2 stat.
            var gameDomain = mappingCache[gameId].Value;
            var modUpdates = await apiClient.ModUpdatesAsync(gameDomain, PastTime.Month);
            var updateResults = ModFeedItemUpdateMixin.FromUpdateResults(modUpdates.Data, gameId);
            updater.Update(updateResults);
        }
        
        return updater.BuildFlattened(v1Timespan);
    }

    /// <summary>
    /// Updates the metadata for mod pages returned from the <see cref="CheckForModPagesWhichNeedUpdating"/> API call.
    /// </summary>
    public static async Task UpdateModFilesForOutdatedPages(
        IDb db,
        ITransaction tx,
        ILogger logger,
        IGraphQlClient graphQlClient,
        PerFeedCacheUpdaterResult<PageMetadataMixin> result,
        CancellationToken cancellationToken)
    {
        // Note(sewer): Undetermined items may be removed items from the site; or
        // caused by programmer error, so we should log these whenever possible,
        // but they should not cause a critical error; in case it's simply the result
        // of mod removal such as DMCA takedown.

        // Note(CE): Every mod page a collection brings in starts out here, since a collection only
        // says which one of its files it uses: that is hundreds of pages at once, on the first
        // check after adding one. Pages are done in chunks that share their requests, and the
        // semaphore limits how many chunks are in transit at any time.
        using var semaphore = new SemaphoreSlim(MaxParallelChunks);
        var tasks = new List<Task>();
        var stopwatch = Stopwatch.StartNew();

        // Helper function to process a chunk of mixins with error handling
        async Task ProcessChunk(SemaphoreSlim sema, PageMetadataMixin[] mixins, bool isUndetermined)
        {
            var isTaken = false;
            try
            {
                isTaken = await sema.WaitAsync(Timeout.Infinite, cancellationToken);
                await UpdateModPages(db, tx,
                    logger,
                    graphQlClient,
                    mixins,
                    cancellationToken
                );
            }
            catch (OperationCanceledException)
            {
                // Ignored.
            }
            catch (Exception e)
            {
                if (isUndetermined)
                {
                    logger.LogError(e, "Failed to update metadata for {Count} mods, starting with {ModId}", mixins.Length, mixins[0].GetModPageId().ModId);
                }
                else
                {
                    // Rethrow for non-undetermined items as these should be truly exceptional
                    throw;
                }
            }
            finally
            {
                if (isTaken)
                    sema.Release();
            }
        }

        // Process undetermined items
        foreach (var mixins in result.UndeterminedItems.Chunk(ModsPerChunk))
        {
            tasks.Add(ProcessChunk(semaphore, mixins, true));
        }

        // Process out of date items
        foreach (var mixins in result.OutOfDateItems.Chunk(ModsPerChunk))
        {
            tasks.Add(ProcessChunk(semaphore, mixins, false));
        }

        // Wait for all tasks to complete
        await Task.WhenAll(tasks);

        logger.LogInformation("Refreshed {Undetermined} undetermined and {OutOfDate} out of date mod pages in {Elapsed}", result.UndeterminedItems.Count, result.OutOfDateItems.Count, stopwatch.Elapsed);
    }

    /// <summary>
    /// How many mod pages are handled together. One request reads the mods themselves, and
    /// another per <see cref="IGraphQlClient.MaxModsPerFilesRequest"/> of them reads their files.
    /// </summary>
    internal const int ModsPerChunk = 60;

    /// <summary>
    /// How many chunks of mod pages are in transit at any time. Each sends one request at a time.
    /// </summary>
    private const int MaxParallelChunks = 4;

    private static async Task UpdateModPages(
        IDb db,
        ITransaction tx,
        ILogger logger,
        IGraphQlClient graphQlClient,
        PageMetadataMixin[] mixins,
        CancellationToken cancellationToken)
    {
        var modsResult = await graphQlClient.QueryMods(mixins.Select(static mixin => mixin.GetModPageId()).ToArray(), cancellationToken);
        // TODO: handle errors
        var mods = modsResult.AssertHasData().ToDictionary(static mod => ModUid.FromV2Api(mod.Uid));

        // A page the server didn't send back is one that has been taken down. It keeps what it
        // has, and is left without a new timestamp so that it is looked at again next time.
        var found = new List<PageMetadataMixin>(mixins.Length);
        foreach (var mixin in mixins)
        {
            if (mods.ContainsKey(mixin.GetModPageId())) found.Add(mixin);
            else logger.LogWarning("Mod (GameID: {Page}, ModId: {ModId}) no longer exists on the site", mixin.GetModPageId().GameId, mixin.GetModPageId().ModId);
        }

        var files = new List<(PageMetadataMixin Mixin, IModFile[] Files)>(found.Count);
        foreach (var chunk in found.Chunk(IGraphQlClient.MaxModsPerFilesRequest))
        {
            var filesOfChunk = await QueryModFiles(logger, graphQlClient, chunk, cancellationToken);
            files.AddRange(chunk.Zip(filesOfChunk));
        }

        // Written only once every answer is in: a page marked as up to date without its files
        // would stay without them until the mod next changes.
        foreach (var (mixin, modFiles) in files)
        {
            mods[mixin.GetModPageId()].Resolve(db, tx, setFilesTimestamp: true);

            var pageEntityId = mixin.GetModPageEntityId();
            foreach (var modFile in modFiles)
            {
                modFile.Resolve(db, tx, pageEntityId);
            }
        }
    }

    /// <summary>
    /// Reads the files of each of <paramref name="mixins"/>, in one request if the server will
    /// take that and otherwise in one for each.
    /// </summary>
    private static async ValueTask<IModFile[][]> QueryModFiles(
        ILogger logger,
        IGraphQlClient graphQlClient,
        PageMetadataMixin[] mixins,
        CancellationToken cancellationToken)
    {
        var batchResult = await graphQlClient.QueryModFilesOfMods(mixins.Select(static mixin => mixin.GetModPageId()).ToArray(), cancellationToken);
        if (batchResult.TryGetData(out var batch)) return batch;

        logger.LogWarning("Unable to read the files of {Count} mods in one request, reading them one at a time: {Result}", mixins.Length, batchResult);

        var files = new IModFile[mixins.Length][];
        for (var i = 0; i < mixins.Length; i++)
        {
            var uid = mixins[i].GetModPageId();
            var result = await graphQlClient.QueryModFiles(uid.ModId, uid.GameId, cancellationToken);
            // TODO: handle errors
            files[i] = result.AssertHasData();
        }

        return files;
    }

    /// <summary>
    /// Returns all files which have a 'newer' date than the current one.
    /// </summary>
    /// <remarks>
    ///     The returned items are returned in descending order, from newest to oldest.
    /// </remarks>
    public static IEnumerable<NexusModsFileMetadata.ReadOnly> GetNewerFilesForExistingFile(IDb db, FileUid fileUid)
    {
        var metadata = NexusModsFileMetadata.FindByUid(db, fileUid).First();
        return GetNewerFilesForExistingFile(metadata);
    } 
    
    /// <summary>
    /// Returns all files which have a 'newer' date than the current one.
    /// </summary>
    /// <remarks>
    ///     The returned items are returned in descending order, from newest to oldest.
    /// </remarks>
    public static IEnumerable<NexusModsFileMetadata.ReadOnly> GetNewerFilesForExistingFile(NexusModsFileMetadata.ReadOnly file) 
        => GetNewerFilesForExistingFile(new ModFileMetadataMixin(file)).Select(x => ((ModFileMetadataMixin)x).Metadata);

    /// <summary>
    /// Returns all files which have a 'newer' date than the current one,
    /// using fuzzy name matching to handle variations in file naming.
    /// </summary>
    /// <remarks>
    ///     The returned items are returned in descending order, from newest to oldest.
    /// </remarks>
    public static IEnumerable<IAmAModFile> GetNewerFilesForExistingFile(IAmAModFile file)
    {
        // Get the normalized name for the current file
        var normalizedName = FuzzySearch.NormalizeFileName(file.Name, file.Version);
        
        // Get all other files from the same mod page
        return file.OtherFilesInSameModPage
            .Where(otherFile => 
                // Must be uploaded later
                // Note(sewer):
                //
                //     In future we might check version too, but this may
                //     be a bit unreliable in cases where versions have
                //     a suffix and non-suffix variants. So these items would
                //     need to be put into a different group/pool to match from.
                //
                //     Because, when mapped to SemVer, the suffix would be
                //     interpreted as a pre-release, and an item without a suffix
                //     may be assumed as a more recent version.
                otherFile.UploadedAt > file.UploadedAt &&
                // Must have matching normalized name
                FuzzySearch.NormalizeFileName(otherFile.Name, otherFile.Version)
                    .Equals(normalizedName, StringComparison.OrdinalIgnoreCase)
            )
            .OrderByDescending(x => x.UploadedAt);
    }

    /// <summary>
    /// Returns all file versions of a given file, including:
    /// - Current (itself) 
    /// - All older versions
    /// - All newer versions
    /// 
    /// Using fuzzy name matching.
    /// </summary>
    public static IEnumerable<NexusModsFileMetadata.ReadOnly> GetAllVersionsForExistingFile(NexusModsFileMetadata.ReadOnly file)
    {
        var mixin = new ModFileMetadataMixin(file);
        var normalizedName = FuzzySearch.NormalizeFileName(file.Name, file.Version);
        // Collect matching versions, include current file, then sort by upload date
        return mixin.OtherFilesInSameModPage
            .Where(f => FuzzySearch.NormalizeFileName(f.Name, f.Version)
                .Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
            .Select(f => ((ModFileMetadataMixin)f).Metadata)
            .Append(file)
            .OrderBy(f => f.UploadedAt);
    }

    /// <summary>
    /// Returns all file versions for a file identified by UID; including itself.
    /// </summary>
    public static IEnumerable<NexusModsFileMetadata.ReadOnly> GetAllVersionsForExistingFile(IDb db, FileUid fileUid)
    {
        var metadata = NexusModsFileMetadata.FindByUid(db, fileUid).First();
        return GetAllVersionsForExistingFile(metadata);
    }
}
