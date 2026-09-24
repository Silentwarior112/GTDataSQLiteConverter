using System.Globalization;
using System.Numerics;

namespace GTDataSQLiteConverter.ParamDb;

/// <summary>
/// The label hash the game uses as the primary key of every table row.
/// </summary>
public static class GtHash
{
    public static ulong Hash(string label)
    {
        ushort sum = 0;
        for (int i = 0; i < label.Length; i++)
            sum += (byte)label[i];

        ulong hash = sum;
        for (int i = 0; i < label.Length; i++)
            hash = BitOperations.RotateLeft(hash, 7) + (byte)label[i];

        return hash;
    }

    /// <summary>
    /// Placeholder shown for a hash with no entry in the ID string table. Kept parseable so it can be
    /// written back unchanged instead of being re-hashed into a different (wrong) value.
    /// </summary>
    public static string ToPlaceholder(ulong hash) => "_" + hash.ToString("X16", CultureInfo.InvariantCulture);

    public static bool TryParsePlaceholder(string? value, out ulong hash)
    {
        hash = 0;

        if (value is null || value.Length != 17 || value[0] != '_')
            return false;

        return ulong.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash);
    }
}
