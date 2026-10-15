using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** A weapon config cloned for one weapon is retagged inside itself and in that weapon's gameplay root. */
public static class WeaponConfigEdits
{
    /**
     * Retags the reciprocal owner pairs that name the config itself.
     * @return The retagged bytes and the number of owner pairs found.
     */
    public static (byte[] Bytes, int Pairs) RetagConfig(ReadOnlyMemory<byte> config, uint source, uint target)
    {
        byte[] bytes = config.ToArray();
        HashSet<int> owners = [];
        int pairs = 0;
        for (int at = 0; at + 16 <= bytes.Length; at += 4)
        {
            if (U32(bytes, at) != source) continue;
            long peer = I64(bytes, at + 8);
            uint type = U32(bytes, at + 4);
            if (peer < 0 || peer > bytes.Length - 16 || type < 0x80800000 || type >= 0x84000000) continue;
            if (U32(bytes, (int)peer) != source || I64(bytes, (int)peer + 8) != at) continue;
            owners.Add(at); owners.Add((int)peer);
            if (at < peer) pairs++;
        }
        // A typed one-way link names an owner-pair side and carries that pair's peer class.
        HashSet<int> links = [];
        for (int at = 0; at + 16 <= bytes.Length; at += 4)
        {
            if (U32(bytes, at) != source || owners.Contains(at)) continue;
            long side = I64(bytes, at + 8);
            if (side < 0 || side > bytes.Length - 16 || !owners.Contains((int)side)) continue;
            long partner = I64(bytes, (int)side + 8);
            if (partner >= 0 && partner <= bytes.Length - 8 && U32(bytes, (int)partner + 4) == U32(bytes, at + 4)) links.Add(at);
        }
        for (int at = 0; at + 4 <= bytes.Length; at += 4)
            if (U32(bytes, at) == source && !owners.Contains(at) && !links.Contains(at))
                throw new InvalidDataException("A weapon config has an unclassified owner reference.");
        foreach (int at in owners.Concat(links)) Put32(bytes, at, target);
        return (bytes, pairs);
    }

    /** The configs a gameplay root constructs, in its construction array at `+16`. */
    public static IReadOnlyList<uint> ConstructionConfigs(ReadOnlyMemory<byte> root)
    {
        var reader = new TagReader(root);
        var rows = reader.Array(16, 0x80809C04, 12);
        return Enumerable.Range(0, rows.Count).Select(index => reader.U32(rows.Data + index * 12)).ToArray();
    }

    /** Typed construction and binding links must all select the same owned config. */
    public static byte[] RetagRoot(ReadOnlyMemory<byte> root, uint source, uint target)
    {
        if (ConstructionConfigs(root).Count(tag => tag == source) != 1)
            throw new InvalidDataException("The weapon root needs one matching config.");
        byte[] result = root.ToArray();
        var reader = new TagReader(root);
        foreach (var layout in new[] { (16, 0x80809C04u, 12, new[] { 0 }),
            (32, 0x80809BC9u, 72, new[] { 8, 40 }), (48, 0x80809BC9u, 72, new[] { 8, 40 }),
            (88, 0x80809C22u, 40, new[] { 16 }), (104, 0x80809C20u, 24, new[] { 0 }) })
        {
            var rows = reader.Array(layout.Item1, layout.Item2, layout.Item3);
            for (int index = 0; index < rows.Count; index++)
                foreach (int offset in layout.Item4)
                {
                    int at = rows.Data + index * layout.Item3 + offset;
                    if (reader.U32(at) == source) Put32(result, at, target);
                }
        }
        for (int at = 0; at + 4 <= result.Length; at += 4)
            if (U32(result, at) == source)
                throw new InvalidDataException("The weapon root has an unclassified config reference.");
        return result;
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static long I64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
