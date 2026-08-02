using Microsoft.VisualBasic;

using Syroot.BinaryData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GTDataSQLiteConverter.Formats
{
    public class IDTable
    {
        public const uint ExpectedMagic = 0x42444449;

        private readonly SortedDictionary<ulong, long> IDs = new();

        /// <summary>
        /// Hash -> index into the ID string database, sorted by hash (the game binary-searches this).
        /// </summary>
        public IReadOnlyDictionary<ulong, long> Entries => IDs;

        public void Read(string indexfn)
        {
            using var fs = new FileStream(indexfn, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var bs = new BinaryStream(fs);

            var magic = bs.ReadUInt32();
            if (magic != ExpectedMagic)
            {
                throw new InvalidDataException("Input db_str is not a STDB string database.");
            }

            var count = bs.ReadUInt32();
            for (int i = 0; i < count; i++)
            {
                ulong hash = bs.ReadUInt64();
                long strIndex = bs.ReadInt64();
                IDs.Add(hash, strIndex);
            }
        }

        public void Write(Stream stream)
        {
            BinaryStream bs = new BinaryStream(stream, ByteConverter.Little);
            bs.WriteUInt32(ExpectedMagic);
            bs.WriteUInt32((uint)IDs.Count);
            
            foreach (var id in IDs)
            {
                bs.WriteUInt64(id.Key); // hash
                bs.WriteInt64(id.Value); // str index
            }
        }

        public void Add(ulong hash, long strIndex)
        {
            if (!IDs.ContainsKey(hash))
                IDs.Add(hash, strIndex);
        }

        public long GetStringIndex(ulong hash)
        {
            if (IDs.TryGetValue(hash, out long index))
                return index;

            return -1;
        }
    }
}
