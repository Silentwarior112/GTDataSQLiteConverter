using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace GTDataSQLiteConverter;

public class StringHasher
{
    public static ulong HashString(string str)
    {
        ushort sum = 0;
        for (int i = 0; i < str.Length; i++)
            sum += (byte)str[i];

        ulong hash = sum;
        for (int i = 0; i < str.Length; i++)
            hash = BitOperations.RotateLeft(hash, 7) + (byte)str[i];

        return hash;
    }
}
