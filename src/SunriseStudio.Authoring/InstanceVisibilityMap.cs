namespace SunriseStudio.Authoring;

/** Tome lookups follow the stored probe chain; unused keys and negative values are not visibility. */
internal sealed class InstanceVisibilityMap
{
    private readonly NativeRows data;
    private readonly int[] buckets, values;
    internal IReadOnlyCollection<ulong> Keys { get; }

    internal InstanceVisibilityMap(byte[] bytes)
    {
        data = new(bytes);
        buckets = data.Rows(0x20, 0x8080733E, 16);
        values = data.Rows(0x30, 0x80800007, 4);
        if (buckets.Length == 0 || (buckets.Length & (buckets.Length - 1)) != 0)
            throw new InvalidDataException("The visibility hash table has an invalid bucket count.");
        Keys = buckets.Select(row => data.U64(row)).ToHashSet();
    }

    /** Bit 30 admits a value slot; bit 31 continues an unequal-key probe. */
    internal bool Contains(ulong id)
    {
        uint mask = checked((uint)buckets.Length - 1), index = Hash(id, data.U32(0x18)) & mask;
        for (int count = 0; count < buckets.Length; count++)
        {
            int row = buckets[index];
            uint control = data.U32(row + 8);
            if (data.U64(row) == id)
            {
                if ((control & 0x40000000) == 0) return false;
                uint slot = control & 0x3FFFFFFF;
                if (slot >= values.Length) throw new InvalidDataException("The visibility value slot leaves its array.");
                int value = data.I32(values[slot]);
                if (value < 0) return false;
                if ((uint)value >> 3 >= ushort.MaxValue)
                    throw new InvalidDataException("The visibility bit index exceeds its native storage.");
                return true;
            }
            if ((control & 0x80000000) == 0) return false;
            index = (index + 1) & mask;
        }
        throw new InvalidDataException("The visibility hash probe does not terminate.");
    }

    private static uint Hash(ulong id, uint seed)
    {
        unchecked
        {
            ulong wideSeed = seed | ((ulong)seed << 32);
            ulong x = (id << 18) + (~id ^ wideSeed);
            ulong y = 21 * (x ^ (x >> 31));
            ulong z = 65 * (y ^ (y >> 11));
            return (uint)z ^ (uint)(z >> 22);
        }
    }
}
