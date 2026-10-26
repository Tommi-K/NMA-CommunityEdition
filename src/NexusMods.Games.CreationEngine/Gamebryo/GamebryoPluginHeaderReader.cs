using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Mutagen.Bethesda.Plugins;
using NexusMods.Games.CreationEngine.Abstractions;

namespace NexusMods.Games.CreationEngine.Gamebryo;

/// <summary>
/// Reads the <c>TES4</c> header of a plugin written for the Gamebryo engines of
/// Fallout 3 and Fallout: New Vegas.
/// </summary>
/// <remarks>
/// Mutagen is used for the games it has record definitions for, but it has no
/// <c>GameRelease</c> for Fallout 3 or New Vegas, so there is nothing to hand those files
/// to. Only the masters are needed (for load order sorting and the missing-master
/// diagnostic), and they sit in the one record at the very start of the file, so reading
/// it directly is both small and cheap.
///
/// The record header is 24 bytes, followed by <c>dataSize</c> bytes of subrecords.
/// Oblivion's is 20 bytes, without the last two fields, so its plugins can't be read here.
///
/// <code>
/// record header   "TES4" | uint32 dataSize | uint32 flags | uint32 formId | uint32 vcInfo | uint16 formVersion | uint16 vcInfo2
/// subrecord       char[4] type | uint16 size | byte[size] data
/// </code>
///
/// Masters are a run of <c>MAST</c> (the filename, null terminated) and <c>DATA</c> (a
/// file size the games ignore) pairs. A subrecord too large for its 16 bit size, like a
/// long <c>ONAM</c> override list, is preceded by an <c>XXXX</c> subrecord holding its
/// real size as a uint32, and its own size is then 0.
/// </remarks>
public static class GamebryoPluginHeaderReader
{
    private const int RecordHeaderSize = 24;
    private const int SubrecordHeaderSize = 6;

    /// <summary>
    /// A header larger than this is not something these games produced; bail rather than
    /// allocate on a corrupt or mis-detected file. Real headers are a few KB at most, the
    /// bulk of it being the <c>ONAM</c> override list.
    /// </summary>
    private const uint MaxHeaderDataSize = 16 * 1024 * 1024;

    /// <summary>
    /// The engine reads these files as Windows-1252. Latin1 is the built-in encoding that
    /// matches it over the range plugin names realistically use.
    /// </summary>
    private static readonly Encoding NameEncoding = Encoding.Latin1;

    private static ReadOnlySpan<byte> Tes4 => "TES4"u8;
    private static ReadOnlySpan<byte> Mast => "MAST"u8;
    private static ReadOnlySpan<byte> Xxxx => "XXXX"u8;

    /// <summary>
    /// Reads the header of the plugin on <paramref name="stream"/>, named
    /// <paramref name="fileName"/>. Returns <c>null</c> when the stream does not hold a
    /// plugin this engine could load, rather than throwing: a loadout can legitimately
    /// contain a file that is truncated, ghosted or simply not a plugin, and a bad file
    /// should not take the sync down with it.
    /// </summary>
    public static async ValueTask<PluginHeader?> ReadAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        if (!ModKey.TryFromNameAndExtension(fileName, out var modKey))
            return null;

        var header = ArrayPool<byte>.Shared.Rent(RecordHeaderSize);
        byte[]? data = null;
        try
        {
            if (!await ReadExactlyAsync(stream, header.AsMemory(0, RecordHeaderSize), cancellationToken))
                return null;

            if (!header.AsSpan(0, 4).SequenceEqual(Tes4))
                return null;

            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            if (dataSize is 0 or > MaxHeaderDataSize)
                return new PluginHeader(modKey, []);

            data = ArrayPool<byte>.Shared.Rent((int)dataSize);
            var body = data.AsMemory(0, (int)dataSize);

            // A truncated header is still worth reading: whatever masters survived in the
            // bytes we did get are better than nothing for the diagnostic.
            var read = await ReadUpToAsync(stream, body, cancellationToken);

            return new PluginHeader(modKey, ReadMasters(data.AsSpan(0, read)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
            if (data is not null) ArrayPool<byte>.Shared.Return(data);
        }
    }

    private static List<ModKey> ReadMasters(ReadOnlySpan<byte> body)
    {
        var masters = new List<ModKey>();
        var offset = 0;
        uint? largeSize = null;

        while (offset + SubrecordHeaderSize <= body.Length)
        {
            var type = body.Slice(offset, 4);
            var size = (uint)BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(offset + 4, 2));
            offset += SubrecordHeaderSize;

            // The size an XXXX subrecord announced replaces the next subrecord's own.
            if (largeSize is not null)
            {
                size = largeSize.Value;
                largeSize = null;
            }
            else if (type.SequenceEqual(Xxxx) && size == 4 && offset + 4 <= body.Length)
            {
                largeSize = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(offset, 4));
                offset += 4;
                continue;
            }

            // The last subrecord can be cut short by a truncated read; take what is there.
            var available = (int)Math.Min(size, (uint)(body.Length - offset));
            if (available <= 0)
            {
                if (size > 0) break;
                continue;
            }

            if (type.SequenceEqual(Mast))
            {
                var name = body.Slice(offset, available);

                // Stored null terminated; the terminator is not part of the name.
                var end = name.IndexOf((byte)0);
                if (end >= 0) name = name[..end];

                if (!name.IsEmpty && ModKey.TryFromNameAndExtension(NameEncoding.GetString(name), out var master))
                    masters.Add(master);
            }

            offset += available;
        }

        return masters;
    }

    /// <summary>
    /// Fills <paramref name="buffer"/>, returning false if the stream ended first.
    /// </summary>
    private static async ValueTask<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        return await ReadUpToAsync(stream, buffer, cancellationToken) == buffer.Length;
    }

    /// <summary>
    /// Reads until <paramref name="buffer"/> is full or the stream ends, returning how many
    /// bytes were read.
    /// </summary>
    private static async ValueTask<int> ReadUpToAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0) break;
            total += read;
        }

        return total;
    }
}
