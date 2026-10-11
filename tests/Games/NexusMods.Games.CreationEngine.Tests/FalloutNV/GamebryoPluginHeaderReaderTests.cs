using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NexusMods.Games.CreationEngine.Gamebryo;

namespace NexusMods.Games.CreationEngine.Tests.FalloutNV;

/// <summary>
/// Fallout: New Vegas plugins are read by hand rather than by Mutagen, which has no
/// Fallout 3 / New Vegas support, so the reader is covered here against headers built to
/// the engine's own layout.
/// </summary>
public class GamebryoPluginHeaderReaderTests
{
    [Fact]
    public async Task ReadsMastersInOrder()
    {
        var bytes = BuildPlugin(masters: ["FalloutNV.esm", "DeadMoney.esm", "SomeMod.esp"]);

        var header = await GamebryoPluginHeaderReader.ReadAsync(new MemoryStream(bytes), "MyMod.esp");

        header.Should().NotBeNull();
        header!.Value.ModKey.Should().Be(ModKey.FromFileName("MyMod.esp"));
        header.Value.Masters.Select(m => m.FileName.ToString())
            .Should().Equal("FalloutNV.esm", "DeadMoney.esm", "SomeMod.esp");
    }

    [Fact]
    public async Task ReadsAPluginWithNoMasters()
    {
        var bytes = BuildPlugin(masters: []);

        var header = await GamebryoPluginHeaderReader.ReadAsync(new MemoryStream(bytes), "FalloutNV.esm");

        header.Should().NotBeNull();
        header!.Value.ModKey.Type.Should().Be(ModType.Master);
        header.Value.Masters.Should().BeEmpty();
    }

    /// <summary>
    /// Masters are preceded by the author and description subrecords and followed by the
    /// override list, so the reader has to walk past subrecords it does not care about.
    /// </summary>
    [Fact]
    public async Task SkipsSubrecordsAroundTheMasters()
    {
        var bytes = BuildPlugin(masters: ["FalloutNV.esm"], author: "Obsidian", description: "a plugin", overrideListLength: 64);

        var header = await GamebryoPluginHeaderReader.ReadAsync(new MemoryStream(bytes), "MyMod.esp");

        header!.Value.Masters.Select(m => m.FileName.ToString()).Should().Equal("FalloutNV.esm");
    }

    [Fact]
    public async Task ReadsMasterNamesAsWindows1252()
    {
        // The engine stores these names in the Windows codepage, not UTF-8.
        var bytes = BuildPlugin(masters: ["Désert.esp"]);

        var header = await GamebryoPluginHeaderReader.ReadAsync(new MemoryStream(bytes), "MyMod.esp");

        header!.Value.Masters.Select(m => m.FileName.ToString()).Should().Equal("Désert.esp");
    }

    [Fact]
    public async Task ReturnsNullForAFileThatIsNotAPlugin()
    {
        var bytes = Encoding.ASCII.GetBytes("not a plugin, just some bytes in a file");

        var header = await GamebryoPluginHeaderReader.ReadAsync(new MemoryStream(bytes), "MyMod.esp");

        header.Should().BeNull();
    }

    [Fact]
    public async Task ReturnsNullForAnEmptyFile()
    {
        var header = await GamebryoPluginHeaderReader.ReadAsync(new MemoryStream([]), "MyMod.esp");

        header.Should().BeNull();
    }

    [Fact]
    public async Task ReturnsNullWhenTheFileNameIsNotAPluginName()
    {
        var bytes = BuildPlugin(masters: []);

        var header = await GamebryoPluginHeaderReader.ReadAsync(new MemoryStream(bytes), "readme.txt");

        header.Should().BeNull();
    }

    /// <summary>
    /// A half-downloaded or truncated plugin should give up what it has rather than throw.
    /// </summary>
    [Fact]
    public async Task HandlesATruncatedHeader()
    {
        var bytes = BuildPlugin(masters: ["FalloutNV.esm", "DeadMoney.esm"]);

        // Cut four characters into the second master's name, so that subrecord is left
        // incomplete while the first master is still whole.
        var truncated = bytes.AsSpan().LastIndexOf("MAST"u8) + 6 + 4;

        var header = await GamebryoPluginHeaderReader.ReadAsync(new MemoryStream(bytes[..truncated]), "MyMod.esp");

        header.Should().NotBeNull();
        header!.Value.Masters.Select(m => m.FileName.ToString()).Should().Equal("FalloutNV.esm");
    }

    /// <summary>
    /// Builds a TES4 record the way the Gamebryo engines write it: a 20 byte record header,
    /// then subrecords of a 4 byte type, a 16 bit length and the data.
    /// </summary>
    private static byte[] BuildPlugin(string[] masters, string? author = null, string? description = null, int overrideListLength = 0)
    {
        var body = new MemoryStream();

        // HEDR: version, record count, next object id.
        var hedr = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(hedr, 1.34f);
        WriteSubrecord(body, "HEDR", hedr);

        if (author is not null) WriteSubrecord(body, "CNAM", ZString(author));
        if (description is not null) WriteSubrecord(body, "SNAM", ZString(description));

        foreach (var master in masters)
        {
            WriteSubrecord(body, "MAST", ZString(master));
            // Each master is followed by a file size the games ignore.
            WriteSubrecord(body, "DATA", new byte[8]);
        }

        // ONAM, the list of overridden form ids.
        if (overrideListLength > 0) WriteSubrecord(body, "ONAM", new byte[overrideListLength]);

        var data = body.ToArray();

        var file = new MemoryStream();
        file.Write("TES4"u8);
        file.Write(UInt32((uint)data.Length));
        file.Write(UInt32(0));  // flags
        file.Write(UInt32(0));  // form id
        file.Write(UInt32(0));  // version control info
        file.Write(data);
        return file.ToArray();
    }

    private static void WriteSubrecord(Stream stream, string type, byte[] data)
    {
        stream.Write(Encoding.ASCII.GetBytes(type));
        var size = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(size, (ushort)data.Length);
        stream.Write(size);
        stream.Write(data);
    }

    private static byte[] ZString(string value) => [..Encoding.Latin1.GetBytes(value), 0];

    private static byte[] UInt32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }
}
