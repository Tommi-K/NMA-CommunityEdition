using System.IO.Hashing;
using System.Security.Cryptography;
using NexusMods.Abstractions.Downloads;
using NexusMods.Paths;
using NexusMods.Sdk.Hashes;

namespace NexusMods.Networking.HttpDownloader;

/// <summary>
/// Hashes a download as it is written, the same way the library hashes a finished file, so
/// that adding the file to the library doesn't have to read it all back in.
/// </summary>
/// <remarks>
/// Only of use while it has seen every byte of the file, in order. A download that carries on
/// from anywhere but where the hashing got to puts it out of use for good, and the library then
/// hashes the file itself, as it always did.
/// </remarks>
internal sealed class DownloadHasher
{
    private XxHash3 _xxHash3 = Xx3Hasher.Initialize();
    private IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    private long _hashedLength;
    private bool _inUse = true;

    /// <summary>
    /// Gets ready for the bytes about to be written at <paramref name="position"/>.
    /// </summary>
    public void PrepareForWriteAt(long position)
    {
        if (position == 0)
        {
            _xxHash3 = Xx3Hasher.Initialize();
            _md5.Dispose();
            _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            _hashedLength = 0;
            _inUse = true;
        }
        else if (position != _hashedLength)
        {
            _inUse = false;
        }
    }

    /// <summary>
    /// Takes in bytes that have just been written, straight after the ones before them.
    /// </summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (!_inUse) return;

        _xxHash3 = Xx3Hasher.Update(_xxHash3, data);
        _md5.AppendData(data);
        _hashedLength += data.Length;
    }

    /// <summary>
    /// The hashes of the finished file, or null unless every one of its
    /// <paramref name="size"/> bytes went past here.
    /// </summary>
    public DownloadedFileHashes? Finish(Size size)
    {
        if (!_inUse || _hashedLength != (long)size.Value) return null;
        _inUse = false;

        var md5 = Md5Value.From(_md5.GetHashAndReset());
        _md5.Dispose();

        return new DownloadedFileHashes(Xx3Hasher.Finish(_xxHash3), md5, size);
    }
}

/// <summary>
/// Passes writes on to <paramref name="inner"/>, and each one that succeeds on to
/// <paramref name="hasher"/>.
/// </summary>
internal sealed class HashingWriteStream(Stream inner, DownloadHasher hasher) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        hasher.Append(buffer);
    }

    public override void WriteByte(byte value)
    {
        Span<byte> single = [value];
        Write(single);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        hasher.Append(buffer.Span);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
