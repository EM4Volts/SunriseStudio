using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SunriseStudio.Formats;

/** Edits detach aliased constants and keep every unrelated property at its original value. */
public static partial class CuiPropertyEdits
{
    /**
     * Only authored properties with matching typed storage may change.
     * @param bytes Current resource bytes.
     * @param document Parsed source identity and value pools.
     * @param replacements Complete replacement value bytes per property.
     * @return Rewritten resource bytes; unchanged replacements retain the original representation.
     */
    public static byte[] Apply(ReadOnlySpan<byte> bytes, CuiDocument document,
        IReadOnlyDictionary<CuiProperty, ReadOnlyMemory<byte>> replacements)
    {
        if (document.SourceSha256 != Convert.ToHexString(SHA256.HashData(bytes)))
            throw new InvalidDataException("The CUI resource changed. Reload it before editing.");
        var all = document.Properties.ToArray();
        Dictionary<CuiProperty, ReadOnlyMemory<byte>> changed = [];
        foreach (var (property, value) in replacements)
        {
            if (!all.Contains(property)) throw new InvalidDataException("The property does not belong to this CUI resource.");
            var pool = document.Pools.Single(pool => pool.DescriptorOffset == property.PoolDescriptor);
            if (value.Length != pool.Stride || pool.ClassHash is not (0x8080468D or 0x8080468B or 0x80804694
                or 0x80804693 or 0x80804696 or 0x80804692 or 0x80804690 or 0x8080468F))
                throw new InvalidDataException("The property value has unsupported storage.");
            if (pool.ClassHash is 0x80804694 or 0x80804693)
                for (int i = 0; i < value.Length; i += sizeof(float))
                    if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(value.Span[i..])))
                        throw new InvalidDataException("Enter finite property values.");
            if (!bytes.Slice(property.ValueOffset, pool.Stride).SequenceEqual(value.Span)) changed.Add(property, value);
        }
        var output = bytes.ToArray();
        foreach (var group in changed.GroupBy(pair => pair.Key.PoolDescriptor))
        {
            var pool = document.Pools.Single(pool => pool.DescriptorOffset == group.Key);
            var shared = group.Where(pair => all.Count(p => p.ValueOffset == pair.Key.ValueOffset) > 1)
                .Select(pair => pair.Key).ToHashSet();
            if (shared.Count == 0)
            {
                foreach (var (property, value) in group) value.Span.CopyTo(output.AsSpan(property.ValueOffset));
                continue;
            }
            int header = checked((output.Length + 11) & ~7);
            int data = checked(header + 16), count = checked(pool.Count + shared.Count);
            Array.Resize(ref output, checked(data + count * pool.Stride));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(header - 4), 0x80809FBD);
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(header), count);
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(header + 8), pool.ClassHash);
            bytes.Slice(pool.DataOffset, pool.Count * pool.Stride).CopyTo(output.AsSpan(data));
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(pool.DescriptorOffset), count);
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(pool.DescriptorOffset + 8), header - pool.DescriptorOffset - 8);
            foreach (var property in all.Where(p => p.PoolDescriptor == pool.DescriptorOffset))
                BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(property.RecordOffset + 16),
                    data + property.ValueOffset - pool.DataOffset - property.RecordOffset - 16);
            int appended = pool.Count;
            foreach (var (property, value) in group)
            {
                bool detach = shared.Contains(property);
                int destination = detach ? data + appended++ * pool.Stride : data + property.ValueOffset - pool.DataOffset;
                value.Span.CopyTo(output.AsSpan(destination));
                if (detach) BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(property.RecordOffset + 16),
                    destination - property.RecordOffset - 16);
            }
            BinaryPrimitives.WriteInt64LittleEndian(output, output.Length);
        }
        return output;
    }
}
