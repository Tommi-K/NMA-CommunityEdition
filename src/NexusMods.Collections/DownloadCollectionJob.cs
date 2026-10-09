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
    /// page that hands nothing over takes the better part of a minute to give up on. When
    /// that happens to the first few in a row it is almost never about those files -- a
    /// browser not signed in to Nexus Mods fails the same way for all of them -- and working
    /// through the whole collection at a minute each would achieve nothing. The failed pages
    /// are left open, which is where the reason is on show.
    /// </remarks>
    private const int MaxFailuresBeforeGivingUp = 3;

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
        var downloads = RevisionMetadata.Downloads.ToArray();
        
        await Parallel.ForAsync(fromInclusive: 0, toExclusive: downloads.Length, parallelOptions: new ParallelOptions
        {
            CancellationToken = context.CancellationToken,
            MaxDegreeOfParallelism = MaxDegreeOfParallelism == -1 ? Environment.ProcessorCount : MaxDegreeOfParallelism,
        }, body: async (index, token) =>
        {
            var download = downloads[index];
            if (!CollectionDownloader.DownloadMatchesItemType(download, ItemType)) return;
            if (CollectionDownloader.GetStatus(download, Db).IsDownloaded()) return;

            try
            {
                if (download.TryGetAsCollectionDownloadNexusMods(out var nexusModsDownload))
                {
                    if (HasGivenUp) return;

                    if (await Downloader.Download(nexusModsDownload, token))
                    {
                        Interlocked.Increment(ref _startedCount);
                    }
                    else if (Interlocked.Increment(ref _failedCount) >= MaxFailuresBeforeGivingUp && HasGivenUp)
                    {
                        Logger.LogWarning("Giving up on `{CollectionName}/{RevisionNumber}`: its first {Count} files were asked for and none of them started downloading", download.CollectionRevision.Collection.Slug, download.CollectionRevision.RevisionNumber, MaxFailuresBeforeGivingUp);
                    }
                }
                else if (download.TryGetAsCollectionDownloadExternal(out var externalDownload))
                {
                    await Downloader.Download(externalDownload, token);
                }
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Exception while downloading `{DownloadName}` from `{CollectionName}/{RevisionNumber}`", download.Name, download.CollectionRevision.Collection.Slug, download.CollectionRevision.RevisionNumber);
            }
        });

        return R3.Unit.Default;
    }
}
