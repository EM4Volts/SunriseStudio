using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Scene;

internal sealed class TagData(byte[] bytes)
{
    public byte[] Bytes { get; } = bytes;
    public int Length => Bytes.Length;
    public byte U8(int offset) => Bytes[offset];
    public ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(offset, 2));
    public short I16(int offset) => BinaryPrimitives.ReadInt16LittleEndian(Bytes.AsSpan(offset, 2));
    public uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(offset, 4));
    public int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(offset, 4));
    public ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(Bytes.AsSpan(offset, 8));
    public long I64(int offset) => BinaryPrimitives.ReadInt64LittleEndian(Bytes.AsSpan(offset, 8));
    public float F32(int offset) => BitConverter.UInt32BitsToSingle(U32(offset));
    public Vector3 V3(int offset) => new(F32(offset), F32(offset + 4), F32(offset + 8));
    public Quaternion Q4(int offset) => new(F32(offset), F32(offset + 4), F32(offset + 8), F32(offset + 12));
    public int Relative(int offset) => checked((int)(offset + I64(offset)));

    /** Tiger array links point to the repeated count, sixteen bytes before the rows. */
    public int[] Rows(int descriptor, uint expectedClass, int stride) => Rows(descriptor, (uint?)expectedClass, stride);

    /** Element class word of an array; zero when the array is empty. */
    public uint ArrayClass(int descriptor) =>
        I64(descriptor) == 0 ? 0 : U32(checked((int)(descriptor + 8 + I64(descriptor + 8))) + 8);

    /** Rows of an array whose element class is not checked. */
    public int[] RowsOfAnyClass(int descriptor, int stride) => Rows(descriptor, null, stride);

    private int[] Rows(int descriptor, uint? expectedClass, int stride)
    {
        var count = I64(descriptor);
        var relative = I64(descriptor + 8);
        if (count == 0 && relative == 0) return [];
        if (count < 0 || count > 1_000_000) throw new InvalidDataException("Array count is out of range.");
        var header = checked((int)(descriptor + 8 + relative));
        if (header < 4 || header + 16 > Length || U32(header - 4) != 0x80809FBD
            || I64(header) != count || expectedClass is { } expected && U32(header + 8) != expected
            || U32(header + 12) != 0 || count * stride > Length - header - 16)
            throw new InvalidDataException($"Array 0x{descriptor:X} does not match class 0x{expectedClass ?? 0:X8}.");
        return Enumerable.Range(0, (int)count).Select(i => header + 16 + i * stride).ToArray();
    }

    public bool Has(int offset, int count) => offset >= 0 && count >= 0 && offset <= Length - count;
}
