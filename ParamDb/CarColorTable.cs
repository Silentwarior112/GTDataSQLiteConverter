using System.Buffers.Binary;

using Syroot.BinaryData;

namespace GTDataSQLiteConverter.ParamDb;

/// <summary>
/// One entry of the shared colour palette.
/// </summary>
public sealed class CarColor
{
    /// <summary>Identifier the per-car lists refer to. The palette is sorted by it.</summary>
    public uint ColorId { get; set; }

    /// <summary>Index into carcolor.sdb for the western name.</summary>
    public int LatinNameIndex { get; set; }

    /// <summary>Index into carcolor.sdb for the Japanese name. Often the same string as the western one.</summary>
    public int JapaneseNameIndex { get; set; }

    /// <summary>
    /// The swatch, stored as 0x00BBGGRR - the bytes sit in the file as R, G, B, 00. Reading it as
    /// RGB turns every red car blue.
    /// </summary>
    public uint PackedBgr { get; set; }

    public byte Red => (byte)(PackedBgr & 0xFF);
    public byte Green => (byte)((PackedBgr >> 8) & 0xFF);
    public byte Blue => (byte)((PackedBgr >> 16) & 0xFF);

    public string RgbHex => $"#{Red:X2}{Green:X2}{Blue:X2}";

    public static uint PackRgb(byte red, byte green, byte blue)
        => (uint)(red | (green << 8) | (blue << 16));
}

/// <summary>The colours available for one car, in the order the game offers them.</summary>
public sealed class CarColorSet
{
    public ulong CarHash { get; set; }
    public List<uint> ColorIds { get; } = new();

    /// <summary>
    /// Where this car's run sat in the pool when the file was read. The runs are not stored in car
    /// order, so keeping this is what lets an untouched file be written back byte for byte.
    /// </summary>
    public int SourcePoolOffset { get; set; } = -1;
}

/// <summary>
/// carcolor.db - the "GT2K" file listing which paint colours each car can be bought in.
///
/// Layout, derived from the retail file and verified by a byte-exact round trip:
///
///   +0x00  u32  magic "GT2K"
///   +0x04  u32  always zero in retail data
///   +0x08  u32  number of car records
///   +0x0C  u32  offset of the colour-id pool
///   +0x10  u32  offset of the colour palette
///   +0x14  u32  file size
///   +0x18       car records, 16 bytes each: { u64 labelHash, u32 colourCount, u32 poolOffset },
///               sorted by hash so the game can binary-search them
///   pool        [u32 count][count x u32 colour id]; a car's poolOffset is measured from the start
///               of the pool, so offset 4 is the first id and the count word is never pointed at
///   palette     [u32 count][count x { u32 colourId, u32 latinName, u32 japaneseName, u32 bgr }],
///               sorted by colour id
///
/// The names live alongside in carcolor.sdb, a normal STDB string table.
/// </summary>
public sealed class CarColorTable
{
    public const uint Magic = 0x4B325447; // "GT2K"
    private const int HeaderSize = 0x18;
    private const int CarRecordSize = 16;
    private const int ColorRecordSize = 16;

    /// <summary>The always-zero word at +0x04, preserved rather than assumed.</summary>
    public uint Reserved { get; set; }

    public List<CarColorSet> Cars { get; } = new();
    public List<CarColor> Palette { get; } = new();

    /// <summary>Colour names, from carcolor.sdb.</summary>
    public StringTable Names { get; set; } = new() { BytesPerCharacter = -1, LengthIncludesPadding = true };

    public string? GetLatinName(CarColor color) => Lookup(color.LatinNameIndex);
    public string? GetJapaneseName(CarColor color) => Lookup(color.JapaneseNameIndex);

    private string? Lookup(int index)
        => index >= 0 && index < Names.Strings.Count ? Names.Strings[index] : null;

    // ---------------------------------------------------------------- reading

    public static CarColorTable Read(string carColorDbPath, string? carColorSdbPath = null)
    {
        byte[] data = File.ReadAllBytes(carColorDbPath);
        CarColorTable table = Read(data);

        carColorSdbPath ??= Path.ChangeExtension(carColorDbPath, ".sdb");
        if (File.Exists(carColorSdbPath))
            table.Names = StringTableIO.Read(carColorSdbPath);

        return table;
    }

    public static CarColorTable Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException("carcolor.db is too small to hold a header.");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic != Magic)
            throw new InvalidDataException("Not a GT2K file - this does not look like carcolor.db.");

        var table = new CarColorTable { Reserved = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) };

        int carCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        int poolOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        int paletteOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        int declaredSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);

        if (declaredSize != data.Length)
            throw new InvalidDataException($"carcolor.db declares {declaredSize} bytes but is {data.Length}.");

        if (poolOffset < HeaderSize || paletteOffset < poolOffset || paletteOffset > data.Length)
            throw new InvalidDataException("carcolor.db has section offsets that do not make sense.");

        // Palette first: the pool refers to it.
        int paletteCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[paletteOffset..]);
        if (paletteOffset + 4 + (paletteCount * ColorRecordSize) > data.Length)
            throw new InvalidDataException("carcolor.db palette runs past the end of the file.");

        for (int i = 0; i < paletteCount; i++)
        {
            ReadOnlySpan<byte> record = data[(paletteOffset + 4 + (i * ColorRecordSize))..];
            table.Palette.Add(new CarColor
            {
                ColorId = BinaryPrimitives.ReadUInt32LittleEndian(record),
                LatinNameIndex = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[4..]),
                JapaneseNameIndex = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[8..]),
                PackedBgr = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]),
            });
        }

        int poolCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[poolOffset..]);
        if (poolOffset + 4 + (poolCount * 4) > data.Length)
            throw new InvalidDataException("carcolor.db colour pool runs past the end of the file.");

        for (int i = 0; i < carCount; i++)
        {
            ReadOnlySpan<byte> record = data[(HeaderSize + (i * CarRecordSize))..];

            var set = new CarColorSet
            {
                CarHash = BinaryPrimitives.ReadUInt64LittleEndian(record),
                SourcePoolOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[12..]),
            };

            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
            for (int k = 0; k < count; k++)
            {
                int at = poolOffset + set.SourcePoolOffset + (k * 4);
                if (at + 4 > data.Length)
                    throw new InvalidDataException($"Car #{i} in carcolor.db points outside the colour pool.");

                set.ColorIds.Add(BinaryPrimitives.ReadUInt32LittleEndian(data[at..]));
            }

            table.Cars.Add(set);
        }

        return table;
    }

    // ---------------------------------------------------------------- writing

    public byte[] Write()
    {
        // The game binary-searches both of these.
        Cars.Sort(static (a, b) => a.CarHash.CompareTo(b.CarHash));
        Palette.Sort(static (a, b) => a.ColorId.CompareTo(b.ColorId));

        int poolSlots = Cars.Sum(c => c.ColorIds.Count);
        int[] offsets = LayOutPool(poolSlots);

        int poolOffset = HeaderSize + (Cars.Count * CarRecordSize);
        int paletteOffset = poolOffset + 4 + (poolSlots * 4);
        int totalSize = paletteOffset + 4 + (Palette.Count * ColorRecordSize);

        var buffer = new byte[totalSize];
        Span<byte> data = buffer;

        BinaryPrimitives.WriteUInt32LittleEndian(data, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..], Reserved);
        BinaryPrimitives.WriteUInt32LittleEndian(data[8..], (uint)Cars.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(data[12..], (uint)poolOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data[16..], (uint)paletteOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data[20..], (uint)totalSize);

        BinaryPrimitives.WriteUInt32LittleEndian(data[poolOffset..], (uint)poolSlots);

        for (int i = 0; i < Cars.Count; i++)
        {
            CarColorSet set = Cars[i];
            Span<byte> record = data[(HeaderSize + (i * CarRecordSize))..];

            BinaryPrimitives.WriteUInt64LittleEndian(record, set.CarHash);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], (uint)set.ColorIds.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], (uint)offsets[i]);

            for (int k = 0; k < set.ColorIds.Count; k++)
                BinaryPrimitives.WriteUInt32LittleEndian(data[(poolOffset + offsets[i] + (k * 4))..], set.ColorIds[k]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(data[paletteOffset..], (uint)Palette.Count);

        for (int i = 0; i < Palette.Count; i++)
        {
            CarColor color = Palette[i];
            Span<byte> record = data[(paletteOffset + 4 + (i * ColorRecordSize))..];

            BinaryPrimitives.WriteUInt32LittleEndian(record, color.ColorId);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], (uint)color.LatinNameIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], (uint)color.JapaneseNameIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], color.PackedBgr);
        }

        return buffer;
    }

    /// <summary>
    /// Decides where each car's run of colour ids goes in the pool.
    ///
    /// Retail data does not store the runs in car order, and there is no rule to re-derive - so the
    /// offsets the file was read with are kept whenever they still tile the pool exactly. Once the
    /// lists have been edited they no longer will, and everything is laid out afresh in car order.
    /// </summary>
    private int[] LayOutPool(int poolSlots)
    {
        var offsets = new int[Cars.Count];

        if (TryKeepSourceLayout(poolSlots, offsets))
            return offsets;

        int next = 4;
        for (int i = 0; i < Cars.Count; i++)
        {
            offsets[i] = next;
            next += Cars[i].ColorIds.Count * 4;
        }

        return offsets;
    }

    private bool TryKeepSourceLayout(int poolSlots, int[] offsets)
    {
        var used = new bool[poolSlots + 1];

        for (int i = 0; i < Cars.Count; i++)
        {
            CarColorSet set = Cars[i];

            if (set.SourcePoolOffset < 4 || set.SourcePoolOffset % 4 != 0)
                return false;

            int start = set.SourcePoolOffset / 4;
            if (start + set.ColorIds.Count - 1 > poolSlots)
                return false;

            for (int k = 0; k < set.ColorIds.Count; k++)
            {
                if (used[start + k])
                    return false;

                used[start + k] = true;
            }

            offsets[i] = set.SourcePoolOffset;
        }

        // Every slot has to be spoken for, or the rebuilt pool would be a different size.
        for (int i = 1; i <= poolSlots; i++)
        {
            if (!used[i])
                return false;
        }

        return true;
    }

    public void Save(string carColorDbPath, string? carColorSdbPath = null)
    {
        File.WriteAllBytes(carColorDbPath, Write());

        carColorSdbPath ??= Path.ChangeExtension(carColorDbPath, ".sdb");

        using var stream = new FileStream(carColorSdbPath, FileMode.Create, FileAccess.Write, FileShare.None);
        StringTableIO.Write(stream, Names);
    }
}
