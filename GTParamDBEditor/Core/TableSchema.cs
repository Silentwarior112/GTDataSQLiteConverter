using System.Buffers.Binary;
using System.Globalization;

using GTDataSQLiteConverter;
using GTDataSQLiteConverter.Entities;
using GTDataSQLiteConverter.Formats;

namespace GTParamDBEditor.Core;

public sealed class LiveColumn
{
    /// <summary>Name as it appears in SQLite and in the grid.</summary>
    public required string Name { get; init; }

    public required DBColumnType Type { get; init; }

    /// <summary>Byte offset of the field inside a row.</summary>
    public required int Offset { get; init; }

    /// <summary>Comment block preceding the column in its .headers file, if any.</summary>
    public string? Documentation { get; set; }

    public int Size => DBUtils.TypeToSize(Type);

    public string SqliteType => DBUtils.TypeToSQLiteTypeName(Type) ?? "BLOB";

    public Type ClrType => Type switch
    {
        DBColumnType.Id or DBColumnType.String or DBColumnType.Unicode => typeof(string),
        DBColumnType.Float or DBColumnType.Double => typeof(double),
        _ => typeof(long),
    };

    public bool IsText => ClrType == typeof(string);

    public override string ToString() => $"{Name} ({Type} @ 0x{Offset:X})";
}

public sealed class LiveTable
{
    public required string Name { get; init; }

    /// <summary>Position of the block in the archive. Written back in this order.</summary>
    public int FileIndex { get; init; }

    public short TableId { get; init; }
    public ushort Version { get; init; }

    /// <summary>Row size of the block as it was loaded.</summary>
    public ushort SourceElementSize { get; init; }

    public ushort SourceRowCount { get; init; }

    /// <summary>
    /// False for tables that are not blocks of the paramdb archive - the car colour tables, which
    /// live in their own file. The archive writer has to leave those alone.
    /// </summary>
    public bool IsArchiveTable { get; init; } = true;

    /// <summary>Original block bytes, kept so unmapped tables and unmapped trailing row bytes survive a save.</summary>
    public byte[] SourceData { get; set; } = Array.Empty<byte>();

    public List<LiveColumn> Columns { get; init; } = new();

    /// <summary>Row size implied by the .headers mapping.</summary>
    public int MappedRowSize { get; init; }

    /// <summary>False when there is no .headers file for the table; its bytes are passed through untouched.</summary>
    public bool IsMapped => Columns.Count > 0;

    /// <summary>True when the mapping does not describe every byte of a row.</summary>
    public bool HasSizeMismatch => IsMapped && MappedRowSize != SourceElementSize && SourceRowCount > 0;

    /// <summary>Never shrink rows: the extra bytes of an under-described row are preserved, not dropped.</summary>
    public int RowStride => Math.Max(MappedRowSize, SourceElementSize);

    /// <summary>Live row count, refreshed from SQLite.</summary>
    public int RowCount { get; set; }

    public LiveColumn? PrimaryKeyColumn
        => Columns.Count > 0 && Columns[0].Type == DBColumnType.Id && Columns[0].Offset == 0 ? Columns[0] : null;

    public override string ToString() => Name;
}

/// <summary>Interns row labels into the ID hash table and its string database.</summary>
public sealed class IdRegistry
{
    private readonly IDTable _table;
    private readonly StringPool _strings;

    public IdRegistry(IDTable table, StringTable strings)
    {
        _table = table;
        _strings = new StringPool(strings);
    }

    public ulong Register(string? label)
    {
        if (string.IsNullOrEmpty(label))
            return 0;

        // A placeholder came from a hash with no known text - put it back exactly as it was.
        if (GtHash.TryParsePlaceholder(label, out ulong rawHash))
            return rawHash;

        ulong hash = GtHash.Hash(label);
        _table.Add(hash, _strings.Intern(label));
        return hash;
    }
}

public sealed record CellIssue(string Table, int RowNumber, string Column, string Message)
{
    public override string ToString() => $"{Table}, row {RowNumber}, {Column}: {Message}";
}

/// <summary>Converts row bytes to and from the values held in the live SQLite database.</summary>
public static class CellCodec
{
    public static object? Read(ReadOnlySpan<byte> row, LiveColumn column, ParamDbFiles files)
    {
        ReadOnlySpan<byte> field = row[column.Offset..];

        switch (column.Type)
        {
            case DBColumnType.Id:
                return files.ResolveId(BinaryPrimitives.ReadUInt64LittleEndian(field));

            case DBColumnType.String:
                return LookUp(files.Strings, BinaryPrimitives.ReadUInt16LittleEndian(field));

            case DBColumnType.Unicode:
                return LookUp(files.UniStrings, BinaryPrimitives.ReadUInt16LittleEndian(field));

            case DBColumnType.Byte:
                return (long)field[0];

            case DBColumnType.Short:
                return (long)BinaryPrimitives.ReadUInt16LittleEndian(field);

            case DBColumnType.Int:
                return (long)BinaryPrimitives.ReadUInt32LittleEndian(field);

            case DBColumnType.Int64:
                // SQLite integers are signed; the raw 64 bit pattern is preserved either way.
                return BinaryPrimitives.ReadInt64LittleEndian(field);

            case DBColumnType.Float:
                return (double)BinaryPrimitives.ReadSingleLittleEndian(field);

            case DBColumnType.Double:
                return BinaryPrimitives.ReadDoubleLittleEndian(field);

            default:
                throw new InvalidDataException($"Column '{column.Name}' has unsupported type {column.Type}.");
        }
    }

    private static string LookUp(StringTable table, ushort index)
        => index < table.Strings.Count ? table.Strings[index] : "";

    /// <summary>
    /// Writes one cell into a row buffer. Returns false and appends an issue when the value cannot be
    /// represented, so the save can be aborted before anything reaches disk.
    /// </summary>
    public static bool Write(
        Span<byte> row,
        LiveColumn column,
        object? value,
        WriteContext context,
        string tableName,
        int rowNumber,
        IList<CellIssue> issues)
    {
        Span<byte> field = row[column.Offset..];

        try
        {
            switch (column.Type)
            {
                case DBColumnType.Id:
                    BinaryPrimitives.WriteUInt64LittleEndian(field, context.Ids.Register(AsString(value)));
                    return true;

                case DBColumnType.String:
                    return WriteStringIndex(field, context.Strings, AsString(value), column, tableName, rowNumber, issues);

                case DBColumnType.Unicode:
                    return WriteStringIndex(field, context.UniStrings, AsString(value), column, tableName, rowNumber, issues);

                case DBColumnType.Byte:
                {
                    if (!TryRange(value, sbyte.MinValue, byte.MaxValue, column, tableName, rowNumber, issues, out long v))
                        return false;
                    field[0] = unchecked((byte)v);
                    return true;
                }

                case DBColumnType.Short:
                {
                    if (!TryRange(value, short.MinValue, ushort.MaxValue, column, tableName, rowNumber, issues, out long v))
                        return false;
                    BinaryPrimitives.WriteUInt16LittleEndian(field, unchecked((ushort)v));
                    return true;
                }

                case DBColumnType.Int:
                {
                    if (!TryRange(value, int.MinValue, uint.MaxValue, column, tableName, rowNumber, issues, out long v))
                        return false;
                    BinaryPrimitives.WriteUInt32LittleEndian(field, unchecked((uint)v));
                    return true;
                }

                case DBColumnType.Int64:
                {
                    if (!TryRange(value, long.MinValue, long.MaxValue, column, tableName, rowNumber, issues, out long v))
                        return false;
                    BinaryPrimitives.WriteInt64LittleEndian(field, v);
                    return true;
                }

                case DBColumnType.Float:
                {
                    if (!TryReal(value, column, tableName, rowNumber, issues, out double d))
                        return false;
                    BinaryPrimitives.WriteSingleLittleEndian(field, (float)d);
                    return true;
                }

                case DBColumnType.Double:
                {
                    if (!TryReal(value, column, tableName, rowNumber, issues, out double d))
                        return false;
                    BinaryPrimitives.WriteDoubleLittleEndian(field, d);
                    return true;
                }

                default:
                    issues.Add(new CellIssue(tableName, rowNumber, column.Name, $"unsupported column type {column.Type}"));
                    return false;
            }
        }
        catch (System.Text.EncoderFallbackException)
        {
            issues.Add(new CellIssue(tableName, rowNumber, column.Name,
                $"'{AsString(value)}' contains characters the game's text encoding cannot store"));
            return false;
        }
    }

    private static bool WriteStringIndex(
        Span<byte> field,
        StringPool pool,
        string? value,
        LiveColumn column,
        string tableName,
        int rowNumber,
        IList<CellIssue> issues)
    {
        int index = pool.Intern(value);
        if (index > ushort.MaxValue)
        {
            issues.Add(new CellIssue(tableName, rowNumber, column.Name, "the string table is full (65536 entries)"));
            return false;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(field, (ushort)index);
        return true;
    }

    private static string? AsString(object? value)
        => value is null or DBNull ? null : value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);

    private static bool TryRange(
        object? value, long min, long max, LiveColumn column,
        string tableName, int rowNumber, IList<CellIssue> issues, out long result)
    {
        result = 0;

        switch (value)
        {
            case null or DBNull:
                return true; // empty cell -> leave the template/zero bytes in place
            case long l:
                result = l;
                break;
            case int i:
                result = i;
                break;
            case double d when d == Math.Floor(d) && !double.IsInfinity(d):
                result = (long)d;
                break;
            case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed):
                result = parsed;
                break;
            default:
                issues.Add(new CellIssue(tableName, rowNumber, column.Name, $"'{value}' is not a whole number"));
                return false;
        }

        if (result < min || result > max)
        {
            issues.Add(new CellIssue(tableName, rowNumber, column.Name,
                $"{result} is outside the range of {column.Type} ({min} to {max})"));
            return false;
        }

        return true;
    }

    private static bool TryReal(
        object? value, LiveColumn column,
        string tableName, int rowNumber, IList<CellIssue> issues, out double result)
    {
        result = 0;

        switch (value)
        {
            case null or DBNull:
                return true;
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case long l:
                result = l;
                return true;
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed):
                result = parsed;
                return true;
            default:
                issues.Add(new CellIssue(tableName, rowNumber, column.Name, $"'{value}' is not a number"));
                return false;
        }
    }
}

public sealed class WriteContext
{
    public required StringPool Strings { get; init; }
    public required StringPool UniStrings { get; init; }
    public required IdRegistry Ids { get; init; }
}
