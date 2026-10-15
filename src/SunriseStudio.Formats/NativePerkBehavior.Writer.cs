using System.Buffers.Binary;

namespace SunriseStudio.Formats;

public static partial class NativePerkBehavior
{
    /** Arrays and payload markers are appended so existing relative pointers keep their targets. */
    private sealed class PerkWriter
    {
        private byte[] bytes;
        public PerkWriter(byte[] initial) => bytes = (byte[])initial.Clone();
        /** The program's own tag, stored by damage-resist self references; 0 leaves them unchanged. */
        public uint ProgramTag { get; init; }

        public int Node(uint type, int size)
        {
            int start = checked((bytes.Length + 19) & ~15);
            System.Array.Resize(ref bytes, checked(start + size));
            U32(start - 4, type);
            return start;
        }

        public int Array(int field, uint type, int count, int stride)
        {
            if (count == 0) { U64(field, 0); I64(field + 8, 0); return 0; }
            int header = Node(ArrayMarker, checked(16 + count * stride));
            U64(field, (ulong)count); I64(field + 8, header - field - 8);
            U64(header, (ulong)count); U64(header + 8, type);
            return header + 16;
        }

        /** An inline string: u64 length with the terminator, then the bytes; pointers target the bytes. */
        public int String(string text)
        {
            if (strings.TryGetValue(text, out int known)) return known;
            byte[] data = Ascii(text);
            int header = Node(StringClass, 8 + data.Length);
            U64(header, (ulong)data.Length);
            Copy(header + 8, data);
            strings[text] = header + 8;
            return header + 8;
        }

        // One copy of each string per compile; label rows and sets all point at it.
        private readonly Dictionary<string, int> strings = new(StringComparer.Ordinal);

        public byte[] Finish() { U64(0, (ulong)bytes.Length); return bytes; }
        public ulong Read64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at));
        public void Copy(int at, byte[] data) => data.CopyTo(bytes, at);
        public void U8(int at, byte value) => bytes[at] = value;
        public void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        public void U64(int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), value);
        public void I64(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
        public void F32(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
    }
}
