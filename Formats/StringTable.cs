using Syroot.BinaryData;
using System.Text;

namespace GTDataSQLiteConverter
{
    public class StringsDataBase
    {
        public const uint ExpectedMagic = 0x42445453;
        public const uint HeaderSize = 0x10;

        public List<string> Strings = new();
        public short BytesPerCharacter { get; set; }

        public void Read(string filename)
        {
            using var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var bs = new BinaryStream(fs);

            var magic = bs.ReadUInt32();
            if (magic != ExpectedMagic)
            {
                throw new InvalidDataException("Input db_str is not a STDB string database.");
            }

            var numOfElements = bs.ReadUInt32();
            BytesPerCharacter = bs.ReadInt16();
            bs.ReadInt16(); // Padding/Empty

            Encoding encoding = Encoding.Default;
            if (BytesPerCharacter == -1)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                encoding = Encoding.GetEncoding("euc-jp");
            }
            else if (BytesPerCharacter == 2)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                encoding = Encoding.GetEncoding("utf-16");
            }
            else if (BytesPerCharacter != 0x0001)
            {
                throw new InvalidDataException("STDB contains unknown string encoding type.");
            }

            uint dataBaseSize = bs.ReadUInt32();
            if (fs.Length != dataBaseSize)
                Console.WriteLine("Warning: STDB has bad file length.");

            long basepos = bs.Position;
            for (int i = 0; i < numOfElements; i++)
            {
                bs.Position = basepos + (0x4 * i);
                uint strpos = bs.ReadUInt32();
                bs.Position = strpos;

                ushort stringDataLength = bs.ReadUInt16(); // game divides this by bytesPerCharacter
                byte[] stringBytes = bs.ReadBytes(stringDataLength);
                Strings.Add(encoding.GetString(stringBytes).TrimEnd('\0'));
            }
        }

        public void Write(Stream stream)
        {
            BinaryStream bs = new BinaryStream(stream, ByteConverter.Little);
            bs.WriteUInt32(ExpectedMagic);
            bs.WriteUInt32((uint)Strings.Count);
            bs.WriteInt16(BytesPerCharacter);
            bs.Position += 2;
            bs.Position += 4; // File size write at the end

            long offsetTablePos = bs.Position;

            Encoding encoding = Encoding.Default;
            if (BytesPerCharacter == -1)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                encoding = Encoding.GetEncoding("euc-jp");
            }

            bs.Position += Strings.Count * sizeof(uint);
            long lastStrOffset = bs.Position;
            for (int i = 0; i < Strings.Count; i++)
            {
                bs.Position = offsetTablePos + (i * 0x04);
                bs.WriteUInt32((uint)lastStrOffset);

                bs.Position = lastStrOffset;

                byte[] data = encoding.GetBytes(Strings[i]);
                bs.WriteUInt16((ushort)data.Length);
                bs.Write(data);
                bs.WriteByte(0);
                bs.Align(0x02, grow: true);

                lastStrOffset = bs.Position;
            }

            bs.Position = 0x0C;
            bs.WriteUInt32((uint)lastStrOffset);
        }

        public string? Get(ushort index)
        {
            if (index >= Strings.Count)
                return null;
            return Strings[index];
        }

        public ushort Add(string str)
        {
            int index = Strings.IndexOf(str);
            if (index != -1)
                return (ushort)index;

            Strings.Add(str);
            return (ushort)(Strings.Count - 1);
        }
    }
}
