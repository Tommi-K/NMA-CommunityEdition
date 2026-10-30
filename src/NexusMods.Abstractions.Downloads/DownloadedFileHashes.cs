using JetBrains.Annotations;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Hashes;

namespace NexusMods.Abstractions.Downloads;

/// <summary>
/// The hashes of a downloaded file, worked out while it was being written.
/// </summary>
/// <param name="Hash">The xxHash3 the library keys the file by.</param>
/// <param name="Md5">The file's MD5.</param>
/// <param name="Size">How much of the file the hashes cover, which has to be all of it.</param>
[PublicAPI]
public readonly record struct DownloadedFileHashes(Hash Hash, Md5Value Md5, Size Size);
