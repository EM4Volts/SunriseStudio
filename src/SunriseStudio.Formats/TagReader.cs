using System.Buffers.Binary;
using System.Globalization;

namespace SunriseStudio.Formats;

internal sealed class TagReader(ReadOnlyMemory<byte> memory)
{
    /** Typed-array writers share the reader's allocation bound. */
    internal const int MaximumArrayCount = 1_048_576;
    public int Length => memory.Length;
    public bool Has(int offset, int count) => offset >= 0 && count >= 0 && offset <= Length - count;
    public byte U8(int offset) => memory.Span[offset];
    public ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(memory.Span[offset..]);
    public uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(memory.Span[offset..]);
    public int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(memory.Span[offset..]);
    public ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(memory.Span[offset..]);
    public long I64(int offset) => BinaryPrimitives.ReadInt64LittleEndian(memory.Span[offset..]);

    /** Relative pointers are signed offsets from the pointer word itself. */
    public int Relative(int offset)
    {
        if (!Has(offset, 8)) throw new InvalidDataException("Truncated relative pointer.");
        long delta = I64(offset);
        if (delta == 0) return -1;
        long result = checked(offset + delta);
        if (result < 0 || result >= Length)
            throw new InvalidDataException($"Relative pointer at 0x{offset:X} leaves the asset.");
        return (int)result;
    }

    /** Typed arrays store their marker before the target header and elements after its 16 bytes. */
    public (int Count, int Data, uint Class) Array(int field, uint expectedClass = 0, int stride = 0)
    {
        if (!Has(field, 16)) throw new InvalidDataException("Truncated array descriptor.");
        ulong count = U64(field);
        if (count == 0) return (0, 0, expectedClass);
        int header = Relative(field + 8);
        if (count > MaximumArrayCount || header < 4 || !Has(header, 16)
            || U32(header - 4) != 0x80809FBD)
            throw new InvalidDataException("Invalid typed array header.");
        uint type = U32(header + 8);
        if (U64(header) != count) throw new InvalidDataException("Array counts disagree.");
        if (expectedClass != 0 && type != expectedClass)
            throw new InvalidDataException($"Unexpected array class {type:X8}.");
        int data = checked(header + 16);
        if (stride > 0 && !Has(data, checked((int)count * stride)))
            throw new InvalidDataException("Array elements leave the asset.");
        return ((int)count, data, type);
    }

    /** Optional extensions require a matching typed array before their bytes gain meaning. */
    public bool TryArray(int field, uint expectedClass, int stride,
        out (int Count, int Data, uint Class) result)
    {
        try { result = Array(field, expectedClass, stride); return true; }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        { result = default; return false; }
    }

    public AssetProperty UInt(string name, int offset, bool edit = false, bool hex = false) =>
        new(name, hex ? $"0x{U32(offset):X8}" : U32(offset).ToString(CultureInfo.InvariantCulture),
            offset, ScalarEncoding.UInt32, edit);
    public AssetProperty UShort(string name, int offset, bool edit = false) =>
        new(name, U16(offset).ToString(CultureInfo.InvariantCulture), offset, ScalarEncoding.UInt16, edit);
    public AssetProperty Byte(string name, int offset, bool edit = false) =>
        new(name, U8(offset).ToString(CultureInfo.InvariantCulture), offset, ScalarEncoding.UInt8, edit);
    public AssetProperty Int(string name, int offset, bool edit = false) =>
        new(name, I32(offset).ToString(CultureInfo.InvariantCulture), offset, ScalarEncoding.Int32, edit);
    public AssetProperty Float(string name, int offset, bool edit = false) =>
        new(name, BinaryPrimitives.ReadSingleLittleEndian(memory.Span[offset..]).ToString("R", CultureInfo.InvariantCulture),
            offset, ScalarEncoding.Float32, edit);
}
