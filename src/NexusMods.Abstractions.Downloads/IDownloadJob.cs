using JetBrains.Annotations;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;

namespace NexusMods.Abstractions.Downloads;

/// <summary>
/// Represents the work associated with downloading a single file.
/// </summary>
[PublicAPI]
public interface IDownloadJob : IJobDefinition<AbsolutePath>
{
    /// <summary>
    /// Gets the destination path for the downloaded file.
    /// </summary>
    AbsolutePath Destination { get; }

    /// <summary>
    /// Adds metadata from the download to the library file
    /// </summary>
    ValueTask AddMetadata(ITransaction transaction, LibraryFile.New libraryFile);

    /// <summary>
    /// The hashes of the downloaded file, worked out as it was written so that adding it to
    /// the library doesn't have to read it all back in. Null when they aren't known.
    /// </summary>
    DownloadedFileHashes? KnownHashes => null;
}
