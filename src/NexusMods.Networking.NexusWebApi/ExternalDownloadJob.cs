using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Collections;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.HttpDownloader;
using NexusMods.Paths;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;

namespace NexusMods.Networking.NexusWebApi;

/// <summary>
/// Job for external collection downloads.
/// </summary>
public record ExternalDownloadJob : HttpDownloadJob
{
    /// <summary>
    /// The expected MD5 hash value of the downloaded file.
    /// </summary>
    public required Md5Value ExpectedMd5 { get; init; }

    /// <summary>
    /// The user-friendly name of the file.
    /// </summary>
    public required string LogicalFileName { get; init; }

    /// <summary>
    /// File name from the Content-Disposition header.
    /// </summary>
    public required Optional<RelativePath> FileName { get; init; }

    /// <summary>
    /// Create a new download job for the given URL, the job will fail if the downloaded file does not
    /// match the expected MD5 hash.
    /// </summary>
    public static IJobTask<ExternalDownloadJob, AbsolutePath> Create(IServiceProvider provider, Uri uri,
        Md5Value expectedMd5, string logicalFileName, Optional<RelativePath> fileName = default)
    {
        var monitor = provider.GetRequiredService<IJobMonitor>();
        var tempFileManager = provider.GetRequiredService<TemporaryFileManager>();

        var job = new ExternalDownloadJob
        {
            Logger = provider.GetRequiredService<ILogger<ExternalDownloadJob>>(),
            ExpectedMd5 = expectedMd5,
            LogicalFileName = logicalFileName,
            FileName = fileName,
            DownloadPageUri = uri,
            Destination = tempFileManager.CreateFile(),
            Uri = uri,
            Client = provider.GetRequiredService<HttpClient>(),
            ProxyPool = provider.GetService<ProxyPool>(),
            SkipMetadataRequest = NexusModsLibrary.SkipsMetadataRequest(provider),
        };

        return monitor.Begin<ExternalDownloadJob, AbsolutePath>(job);
    }


    /// <inheritdoc />
    public override ValueTask AddMetadata(ITransaction tx, LibraryFile.New libraryFile)
    {
        // Adding the file to the library has just worked out its MD5, from the download or
        // from the file itself, so there is no need to read it all again for this.
        var md5Actual = libraryFile.Md5;
        if (md5Actual != ExpectedMd5)
            throw new InvalidOperationException($"MD5 hash mismatch. Expected: {ExpectedMd5}, Actual: {md5Actual}");

        _ = new DirectDownloadLibraryFile.New(tx, libraryFile.Id)
        {
            LocalFile = new LocalFile.New(tx, libraryFile.Id)
            {
                LibraryFile = libraryFile,
                OriginalPath = FileName.HasValue ? FileName.Value.ToString() : Destination.ToString(),
            },
            LogicalFileName = LogicalFileName,
        };

        if (FileName.HasValue)
        {
            libraryFile.FileName = FileName.Value;
            libraryFile.GetLibraryItem(tx).Name = FileName.Value;
        }
        else
        {
            libraryFile.GetLibraryItem(tx).Name = LogicalFileName;
        }

        return ValueTask.CompletedTask;
    }
}
