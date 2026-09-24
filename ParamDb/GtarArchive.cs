using Syroot.BinaryData;

namespace GTDataSQLiteConverter.ParamDb;

/// <summary>
/// The "GTAR" container that paramdb.db is. It preserves the alignment mask and the end-of-data
/// index convention found in the source file, so a load/save round trip with no edits reproduces
/// the original bytes.
/// </summary>
public sealed class GtarArchive
{
    public const uint Magic = 0x52415447; // "GTAR"
    private const uint BlockMagic = 0x54445447; // "GTDT"

    /// <summary>Blocks start on a (AlignMask + 1) byte boundary. Retail GT3 files use 7.</summary>
    public uint AlignMask { get; set; } = 7;

    /// <summary>
    /// The trailing index entry marks the end of the data. Retail files store it relative to the
    /// index size like every other entry; this flag exists so an oddly built file stays odd.
    /// </summary>
    public bool LastIndexIsAbsolute { get; set; }

    public List<DataBlock> Blocks { get; } = new();

    public static GtarArchive Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Read(fs);
    }

    public static GtarArchive Read(Stream stream)
    {
        var bs = new BinaryStream(stream, ByteConverter.Little);

        uint magic = bs.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException("Not a GTAR archive - this does not look like a paramdb file.");

        uint numFiles = bs.ReadUInt32();
        uint indexSize = bs.ReadUInt32();
        uint alignMask = bs.ReadUInt32();

        if (numFiles > 4096)
            throw new InvalidDataException($"GTAR archive claims {numFiles} entries, which is not plausible.");

        uint[] indices = bs.ReadUInt32s((int)numFiles + 1);

        var archive = new GtarArchive { AlignMask = alignMask };

        long length = stream.Length;
        uint endIndex = indices[numFiles];
        archive.LastIndexIsAbsolute = endIndex != length - indexSize && endIndex == length;

        for (int i = 0; i < numFiles; i++)
        {
            bs.Position = indexSize + indices[i];

            var block = new DataBlock();
            block.Read(bs);
            archive.Blocks.Add(block);
        }

        return archive;
    }

    public void Write(Stream stream)
    {
        var bs = new BinaryStream(stream, ByteConverter.Little);

        int alignment = checked((int)AlignMask) + 1;
        if (alignment < 1)
            alignment = 1;

        bs.WriteUInt32(Magic);
        bs.WriteUInt32((uint)Blocks.Count);
        bs.WriteUInt32(0); // index size - patched once known
        bs.WriteUInt32(AlignMask);

        long indicesOffset = bs.Position;
        bs.Position += (Blocks.Count + 1) * sizeof(uint);
        Pad(bs, alignment);

        long indexSize = bs.Position;

        var offsets = new uint[Blocks.Count + 1];
        for (int i = 0; i < Blocks.Count; i++)
        {
            offsets[i] = (uint)(bs.Position - indexSize);
            WriteBlock(bs, Blocks[i]);
            Pad(bs, alignment);
        }

        long endOfData = bs.Position;
        offsets[Blocks.Count] = LastIndexIsAbsolute ? (uint)endOfData : (uint)(endOfData - indexSize);

        bs.Position = indicesOffset;
        for (int i = 0; i < offsets.Length; i++)
            bs.WriteUInt32(offsets[i]);

        bs.Position = 0x08;
        bs.WriteUInt32((uint)indexSize);

        bs.Position = endOfData;
    }

    private static void WriteBlock(BinaryStream bs, DataBlock block)
    {
        int dataSize = block.NumOfElements * block.ElementSize;

        bs.WriteUInt32(BlockMagic);
        bs.WriteUInt16(block.Version);
        bs.WriteInt16(block.TableID);
        bs.WriteUInt16(block.NumOfElements);
        bs.WriteUInt16(block.ElementSize);
        bs.WriteUInt32((uint)(DataBlock.HeaderSize + dataSize));

        if (dataSize == 0)
            return;

        byte[] buffer = block.Buffer ?? Array.Empty<byte>();
        if (buffer.Length < dataSize)
            throw new InvalidDataException($"Block {block.TableID} buffer is {buffer.Length} bytes but the header describes {dataSize}.");

        bs.Write(buffer, 0, dataSize);
    }

    private static void Pad(BinaryStream bs, int alignment)
    {
        long remainder = bs.Position % alignment;
        if (remainder == 0)
            return;

        bs.Write(new byte[alignment - remainder]);
    }
}
