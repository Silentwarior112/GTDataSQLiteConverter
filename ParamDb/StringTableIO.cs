using System.Text;

using Syroot.BinaryData;

namespace GTDataSQLiteConverter.ParamDb;

/// <summary>
/// Reader/writer for the "STDB" string databases (paramstr / paramunistr / .id_db_str).
/// Layout, verified against retail GT3 files: [magic][count][bytesPerChar:i16][pad:i16][fileSize]
/// [count * u32 offsets][ (u16 byteLength, bytes, 0x00, pad to 2) ... ].
///
/// The stored length means different things in different files: paramstr and .id_db_str store the
/// bare string length, paramunistr stores the padded length including the terminator. Whichever a
/// file uses is detected on load and reproduced on save - see <see cref="StringTable.LengthIncludesPadding"/>.
/// </summary>
public static class StringTableIO
{
    public const uint Magic = 0x42445453; // "STDB"

    /// <summary>
    /// Single byte tables are decoded as Latin-1 rather than UTF-8: it maps every possible byte to a
    /// character and back, so a load/save round trip can never corrupt one. Retail GT3 paramstr and
    /// .id_db_str data is pure ASCII, where the two agree.
    /// </summary>
    private const int SingleByteCodePage = 28591;

    public static Encoding GetEncoding(short bytesPerCharacter, bool throwOnUnmappable = false)
    {
        int codePage = bytesPerCharacter switch
        {
            -1 => 51932,    // euc-jp
            2 => 1200,      // utf-16
            _ => SingleByteCodePage,
        };

        return throwOnUnmappable
            ? Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback)
            : Encoding.GetEncoding(codePage);
    }

    /// <summary>
    /// Encodes a string for storage, throwing <see cref="EncoderFallbackException"/> when a character
    /// has no representation in the table's encoding (e.g. typing Cyrillic into a euc-jp column).
    /// </summary>
    public static byte[] Encode(string? value, short bytesPerCharacter)
        => GetEncoding(bytesPerCharacter, throwOnUnmappable: true).GetBytes(value ?? "");

    public static StringTable Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bs = new BinaryStream(fs, ByteConverter.Little);

        uint magic = bs.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not an STDB string database.");

        uint count = bs.ReadUInt32();
        short bytesPerCharacter = bs.ReadInt16();
        bs.ReadInt16(); // padding

        uint declaredSize = bs.ReadUInt32();
        if (declaredSize > fs.Length)
            throw new InvalidDataException($"'{Path.GetFileName(path)}' declares a size of {declaredSize} bytes but is only {fs.Length}.");

        Encoding encoding = GetEncoding(bytesPerCharacter);
        var table = new StringTable { BytesPerCharacter = bytesPerCharacter };

        int endsWithNull = 0;
        int endsWithText = 0;

        long offsetTablePos = bs.Position;
        for (int i = 0; i < count; i++)
        {
            bs.Position = offsetTablePos + (sizeof(uint) * i);
            bs.Position = bs.ReadUInt32();

            ushort byteLength = bs.ReadUInt16();
            byte[] data = bs.ReadBytes(byteLength);

            if (byteLength > 0)
            {
                if (data[^1] == 0)
                    endsWithNull++;
                else
                    endsWithText++;
            }

            // Trailing nulls are the terminator and alignment, not part of the text.
            int textLength = data.Length;
            while (textLength > 0 && data[textLength - 1] == 0)
                textLength--;

            table.Strings.Add(encoding.GetString(data, 0, textLength));
            table.RawData.Add(data[..textLength]);
        }

        // A length that runs past the last character of the text has to be covering the terminator.
        if (endsWithNull + endsWithText > 0)
            table.LengthIncludesPadding = endsWithNull > endsWithText;

        return table;
    }

    public static void Write(Stream stream, StringTable table)
    {
        var bs = new BinaryStream(stream, ByteConverter.Little);

        List<string> strings = table.Strings;

        bs.WriteUInt32(Magic);
        bs.WriteUInt32((uint)strings.Count);
        bs.WriteInt16(table.BytesPerCharacter);
        bs.WriteInt16(0);
        bs.WriteUInt32(0); // file size - patched at the end

        long offsetTablePos = bs.Position;
        bs.Position += strings.Count * sizeof(uint);

        var offsets = new uint[strings.Count];
        for (int i = 0; i < strings.Count; i++)
        {
            offsets[i] = (uint)bs.Position;

            // Retail paramunistr holds a few byte sequences that are not valid euc-jp, so decoding
            // them is lossy. Strings that came from the file are written back as the exact bytes
            // that were read; only strings added since are encoded from text.
            byte[] data = table.GetRawData(i) ?? Encode(strings[i], table.BytesPerCharacter);

            // Terminator, then round the entry up to an even length.
            int padded = data.Length + 1;
            if (padded % 2 != 0)
                padded++;

            int storedLength = table.LengthIncludesPadding ? padded : data.Length;
            if (storedLength > ushort.MaxValue)
                throw new InvalidDataException($"String #{i} encodes to {storedLength} bytes, over the 65535 byte limit.");

            bs.WriteUInt16((ushort)storedLength);
            bs.Write(data);
            bs.Write(new byte[padded - data.Length]);
        }

        long fileSize = bs.Position;

        bs.Position = offsetTablePos;
        for (int i = 0; i < offsets.Length; i++)
            bs.WriteUInt32(offsets[i]);

        bs.Position = 0x0C;
        bs.WriteUInt32((uint)fileSize);

        bs.Position = fileSize;
    }
}

public sealed class StringTable
{
    public short BytesPerCharacter { get; set; } = 1;

    /// <summary>
    /// Whether the u16 in front of each string counts the terminator and alignment padding as well
    /// as the text. paramunistr does, paramstr and .id_db_str do not.
    /// </summary>
    public bool LengthIncludesPadding { get; set; }

    public List<string> Strings { get; } = new();

    /// <summary>
    /// The bytes each string was read as, parallel to <see cref="Strings"/>. Entries added since
    /// have none and get encoded from their text instead.
    /// </summary>
    public List<byte[]?> RawData { get; } = new();

    public byte[]? GetRawData(int index) => index < RawData.Count ? RawData[index] : null;
}

/// <summary>
/// Interns strings into a <see cref="StringTable"/>, seeded with the table as it was loaded so that
/// existing indices - including those referenced by table data no .headers file describes -
/// keep pointing at the same string.
/// </summary>
public sealed class StringPool
{
    private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);

    public StringPool(StringTable table)
    {
        Table = table;

        for (int i = 0; i < table.Strings.Count; i++)
            _indices.TryAdd(table.Strings[i], i);
    }

    public StringTable Table { get; }

    public int Intern(string? value)
    {
        value ??= "";

        if (_indices.TryGetValue(value, out int existing))
            return existing;

        // Validate the encoding here so the failure can be reported against the cell being saved.
        StringTableIO.Encode(value, Table.BytesPerCharacter);

        int index = Table.Strings.Count;
        Table.Strings.Add(value);

        while (Table.RawData.Count < index)
            Table.RawData.Add(null);
        Table.RawData.Add(null);

        _indices[value] = index;
        return index;
    }
}
