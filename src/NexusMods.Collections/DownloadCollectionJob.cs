using System.Collections.Concurrent;
using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Jobs;

namespace NexusMods.Collections;

public class DownloadCollectionJob : IJobDefinitionWithStart<DownloadCollectionJob, R3.Unit>
{
    /// <summary>
    /// How many Nexus Mods files may fail at the start of a run before the rest of the
    /// collection is left alone.
    /// </summary>
    /// <remarks>
    /// Without premium each file is downloaded by driving its download page in a tab, and a
    /// page that hands nothing over takes a while to give up on. When that happens to the
    /// first few in a row it is almost never about those files -- a browser not signed in to
    /// Nexus Mods fails the same way for all of them -- and working through the whole
    /// collection like that would achieve nothing. The failed pages are left open, which is
    /// where the reason is on show.
    /// </remarks>
    private const int MaxFailuresBeforeGivingUp = 3;

    /// <summary>
    /// How many external files are downloaded at a time.
    /// </summary>
    /// <remarks>
    /// These are fetched directly rather than through a download page, so they run alongside
    /// the Nexus Mods files instead of in the same queue: there, one of them would hold up
    /// every download page behind it for as long as it takes to download. Few at a time, as
    /// they tend to come from the same few hosts.
    /// </remarks>
    private const int MaxParallelExternalDownloads = 4;

    /// <summary>
    /// Nexus Mods files asked for that started nothing.
    /// </summary>
    private int _failedCount;

    /// <summary>
    /// Nexus Mods files asked for that did start downloading. Once there is one of these the
    /// run always goes on to the end: whatever else fails is about those files, not about
    /// downloading being broken, and skipping the rest over it would leave files that would
    /// have downloaded fine unasked for -- every time, however often it is run again.
    /// </summary>
    private int _startedCount;

    /// <summary>
    /// Downloads that have been transferred and are still being added to the library.
    /// </summary>
    private readonly ConcurrentBag<Task> _adding = [];

    /// <summary>
    /// Nexus Mods files whose download page handed nothing over, given another go at the end.
    /// </summary>
    private readonly ConcurrentQueue<CollectionDownloadNexusMods.ReadOnly> _notStarted = new();

    public required ILogger<DownloadCollectionJob> Logger { get; init; }
    public required CollectionRevisionMetadata.ReadOnly RevisionMetadata { get; init; }
    public required CollectionDownloader.ItemType ItemType { get; init; }
    public required CollectionDownloader Downloader { get; init; }
    public required IDb Db { get; init; }
    public required int MaxDegreeOfParallelism { get; init; }

    /// <summary>
    /// Whether the run has stopped asking for Nexus Mods files.
    /// </summary>
    private bool HasGivenUp => Volatile.Read(ref _startedCount) == 0 && Volatile.Read(ref _failedCount) >= MaxFailuresBeforeGivingUp;

    public async ValueTask<R3.Unit> StartAsync(IJobContext<DownloadCollectionJob> context)
    {
        // Largest first. However the downloads are spread out, the run lasts at least as long
        // as its biggest file takes on its own; started last, that file would still be going
        // long after everything else had finished.
        var downloads = RevisionMetadata.Downloads
            .Where(download => CollectionDownloader.DownloadMatchesItemType(download, ItemType))
            .Where(download => !CollectionDownloader.GetStatus(download, Db).IsDownloaded())
            .OrderByDescending(GetExpectedSize)
            .ToArray();

        var nexusModsDownloads = new List<CollectionDownloadNexusMods.ReadOnly>();
        var externalDownloads = new List<CollectionDownloadExternal.ReadOnly>();

        foreach (var download in downloads)
        {
            if (download.TryGetAsCollectionDownloadNexusMods(out var nexusModsDownload)) nexusModsDownloads.Add(nexusModsDownload);
            else if (download.TryGetAsCollectionDownloadExternal(out var externalDownload)) externalDownloads.Add(externalDownload);
        }

        var maxDegreeOfParallelism = MaxDegreeOfParallelism == -1 ? Environment.ProcessorCount : MaxDegreeOfParallelism;

        // A place for every download in flight and every one still being added to the library,
        // so that a fast connection can't run so far ahead of the extracting and hashing that
        // temporary files pile up. A download that went through a download page is the app's
        // from the moment it is handed over, so its place comes straight back.
        var places = new SemaphoreSlim(maxDegreeOfParallelism + MaxParallelExternalDownloads + Environment.ProcessorCount);

        await Task.WhenAll(
            DownloadNexusModsFiles(nexusModsDownloads, maxDegreeOfParallelism, places, context.CancellationToken),
            DownloadExternalFiles(externalDownloads, places, context.CancellationToken)
        );

        // Each of these reports its own failure.
        await Task.WhenAll(_adding);

        return R3.Unit.Default;
    }

    private async Task DownloadNexusModsFiles(
        IReadOnlyList<CollectionDownloadNexusMods.ReadOnly> downloads,
        int maxDegreeOfParallelism,
        SemaphoreSlim places,
        CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(downloads, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
        }, async (download, token) =>
        {
            if (HasGivenUp) return;
            if (Downloader.IsBeingDownloaded(download)) return;

            await Downloader.WaitForRoomInDownloadQueue(token);

            await StartInPlace(places, download.AsCollectionDownload(), async () =>
            {
                var start = await Downloader.BeginDownload(download, token);
                if (start.Started)
                {
                    Interlocked.Increment(ref _startedCount);
                }
                else
                {
                    _notStarted.Enqueue(download);

                    if (Interlocked.Increment(ref _failedCount) >= MaxFailuresBeforeGivingUp && HasGivenUp)
                    {
                        Logger.LogWarning("Giving up on `{CollectionName}/{RevisionNumber}`: its first {Count} files were asked for and none of them started downloading", RevisionMetadata.Collection.Slug, RevisionMetadata.RevisionNumber, MaxFailuresBeforeGivingUp);
                    }
                }

                return start.Adding;
            }, token);
        });

        await RetryNotStarted(cancellationToken);
    }

    private async Task DownloadExternalFiles(IReadOnlyList<CollectionDownloadExternal.ReadOnly> downloads, SemaphoreSlim places, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(downloads, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = MaxParallelExternalDownloads,
        }, (download, token) => StartInPlace(places, download.AsCollectionDownload(), () => Downloader.BeginDownload(download, token), token));
    }

    /// <summary>
    /// Starts a download once one of <paramref name="places"/> is free, and holds it until the
    /// download has been added to the library.
    /// </summary>
    /// <param name="places">The places to take one of.</param>
    /// <param name="download">The download, for reporting.</param>
    /// <param name="begin">Starts the download, returning once it is over but for being added to the library.</param>
    /// <param name="cancellationToken">Gives up waiting for a place.</param>
    private async ValueTask StartInPlace(SemaphoreSlim places, CollectionDownload.ReadOnly download, Func<ValueTask<Task>> begin, CancellationToken cancellationToken)
    {
        try
        {
            await places.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var placeHandedOn = false;

        try
        {
            var adding = await begin();
            _adding.Add(FinishAdding(adding, download, places));
            placeHandedOn = true;
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception e)
        {
            LogFailure(e, download);
        }
        finally
        {
            if (!placeHandedOn) places.Release();
        }
    }

    /// <summary>
    /// Gives the download pages that handed nothing over a second go, now that everything
    /// else is on its way: a page that got stuck loading once usually loads the next time.
    /// </summary>
    private async Task RetryNotStarted(CancellationToken cancellationToken)
    {
        if (_notStarted.IsEmpty || HasGivenUp) return;

        Logger.LogInformation("Trying the {Count} download pages of `{CollectionName}/{RevisionNumber}` that handed nothing over once more", _notStarted.Count, RevisionMetadata.Collection.Slug, RevisionMetadata.RevisionNumber);

        while (_notStarted.TryDequeue(out var download))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Downloader.IsDownloaded(download.AsCollectionDownload()) || Downloader.IsBeingDownloaded(download)) continue;

            try
            {
                var start = await Downloader.BeginDownload(download, cancellationToken);
                _adding.Add(FinishAdding(start.Adding, download.AsCollectionDownload(), places: null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                LogFailure(e, download.AsCollectionDownload());
            }
        }
    }

    /// <summary>
    /// Waits for a download to reach the library, reporting it if it doesn't, and gives back
    /// its place in the queue.
    /// </summary>
    private async Task FinishAdding(Task adding, CollectionDownload.ReadOnly download, SemaphoreSlim? places)
    {
        try
        {
            await adding;
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception e)
        {
            LogFailure(e, download);
        }
        finally
        {
            places?.Release();
        }
    }

    private void LogFailure(Exception e, CollectionDownload.ReadOnly download)
    {
        Logger.LogError(e, "Exception while downloading `{DownloadName}` from `{CollectionName}/{RevisionNumber}`", download.Name, download.CollectionRevision.Collection.Slug, download.CollectionRevision.RevisionNumber);
    }

    /// <summary>
    /// The size of <paramref name="download"/>, zero when it isn't known.
    /// </summary>
    private static ulong GetExpectedSize(CollectionDownload.ReadOnly download)
    {
        if (download.TryGetAsCollectionDownloadNexusMods(out var nexusModsDownload)) return nexusModsDownload.FileMetadata.Size.ValueOrDefault().Value;
        if (download.TryGetAsCollectionDownloadExternal(out var externalDownload)) return externalDownload.Size.Value;
        return 0;
    }
}
