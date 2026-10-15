using System.Buffers.Binary;
using SunriseStudio.Packages;

namespace SunriseStudio.Authoring;

/** A firing row slot entity is the stock slot entity with only its particle sequence replaced. */
public static class WeaponFiringSlotEntity
{
    public const uint EntityClass = 0x80809C0F, VariableStoreClass = 0x80809790;
    private const uint ConstructionRow = 0x80809C04, JoinRow = 0x80809BC9,
        InterfaceRow = 0x80809C22, InterfaceConfigRow = 0x80809C20;
    // Entity tables: construction +0x10, variable joins +0x20 and +0x30, interface rows +0x58, their configs +0x68.
    private const int Construction = 0x10, Interfaces = 0x58, InterfaceConfigs = 0x68;
    private static readonly int[] Joins = [0x20, 0x30];
    private const int ConstructionStride = 12, JoinStride = 72, InterfaceStride = 40, InterfaceConfigStride = 24;

    /**
     * Copies the stock entity onto the authored sequence. Its variable joins into the stock sequence
     * are dropped; the authored sequence reads no named variables.
     * @param sequence Bytes of the authored sequence, a clone of the checked particle profile.
     * @param bursts The sequence's burst count, which picks its profile.
     * @return The new entity bytes.
     */
    public static byte[] Compose(PackageCatalog catalog, uint stockEntity, uint sequenceTag, byte[] sequence, int bursts = 1)
    {
        if (!catalog.TryGetEntry(stockEntity, out var entry) || entry.Reference != EntityClass)
            throw new InvalidDataException($"Firing slot entity {stockEntity:X8} is not an entity.");
        byte[] stock = catalog.Read(stockEntity);
        uint stockSequence = RequirePerShot(catalog.Read, stock, $"The stock muzzle {stockEntity:X8}");
        int stockResource = WeaponParticleBinding.SequenceResource(catalog.Read(stockSequence));
        int resource = WeaponParticleBinding.SequenceResource(sequence);
        if (resource < 0) throw new InvalidDataException("The authored firing sequence is not a sequence config.");

        // The checked profile entity names every interface of the authored sequence layout.
        uint profileConfig = WeaponParticleBinding.ConfigTemplateFor(bursts);
        byte[] profile = catalog.Read(WeaponParticleBinding.EntityTemplateFor(bursts));
        int profileResource = WeaponParticleBinding.SequenceResource(catalog.Read(profileConfig));
        int[] profileRows = NativeRows.Rows(profile, Interfaces, InterfaceRow, InterfaceStride);
        int[] profileConfigs = NativeRows.Rows(profile, InterfaceConfigs, InterfaceConfigRow, InterfaceConfigStride);
        var layout = new Dictionary<(uint Component, uint Map), (int Row, int Config)>();
        for (int index = 0; index < profileRows.Length; index++)
            if (U32(profile, profileRows[index] + 16) == profileConfig
                && !layout.TryAdd((U32(profile, profileRows[index] + 12), U32(profile, profileRows[index] + 32)),
                    (profileRows[index], profileConfigs[index])))
                throw new InvalidDataException("The particle profile names one sequence interface twice.");

        byte[] result = stock.ToArray();
        foreach (int row in NativeRows.Rows(result, Construction, ConstructionRow, ConstructionStride))
            if (U32(result, row) == stockSequence) Put32(result, row, sequenceTag);
        int[] rows = NativeRows.Rows(result, Interfaces, InterfaceRow, InterfaceStride);
        int[] configs = NativeRows.Rows(result, InterfaceConfigs, InterfaceConfigRow, InterfaceConfigStride);
        if (rows.Length != configs.Length) throw new InvalidDataException("Entity interface tables differ in length.");
        var matched = new HashSet<(uint, uint)>();
        for (int index = 0; index < rows.Length; index++)
        {
            int row = rows[index], config = configs[index];
            if (U32(result, row + 16) != stockSequence) continue;
            var key = (U32(result, row + 12), U32(result, row + 32));
            if (!layout.TryGetValue(key, out var source) || !matched.Add(key) || U32(result, config) != stockSequence
                || !SameFields(result, row, profile, source.Row, [12, 20, 32])
                || U32(result, config + 4) != U32(profile, source.Config + 4) || I64(result, row + 24) - stockResource
                    != I64(profile, source.Row + 24) - profileResource)
                throw new InvalidDataException($"Stock sequence interface {key.Item2:X8} has no matching authored row.");
            // The stock row keeps its ordering fields; only the config and its interface offset change.
            Put32(result, row + 16, sequenceTag);
            Put64(result, row + 24, I64(profile, source.Row + 24) - profileResource + resource);
            Put32(result, config, sequenceTag);
            Put64(result, config + 8, I64(profile, source.Config + 8));
        }
        if (matched.Count != layout.Count) throw new InvalidDataException("The stock sequence lacks an authored interface.");
        foreach (int field in Joins)
            DropRows(result, field, JoinStride, row => U32(result, row + 8) == stockSequence || U32(result, row + 40) == stockSequence);
        for (int at = 0; at + 4 <= result.Length; at += 4)
            if (U32(result, at) == stockSequence)
                throw new InvalidDataException("The stock firing entity names its sequence outside its typed tables.");
        return result;
    }

    /**
     * True when the stock entity's variable stores feed only its particle sequence.
     * Such a slot uses the profile entity; a copy would keep stores that nothing reads.
     */
    public static bool FeedsOnlyItsSequence(PackageCatalog catalog, uint stockEntity)
    {
        byte[] stock = catalog.Read(stockEntity), profile = catalog.Read(WeaponParticleBinding.EntityTemplate);
        uint sequence = RequirePerShot(catalog.Read, stock, $"The stock entity {stockEntity:X8}");
        var common = NativeRows.Rows(profile, Construction, ConstructionRow, ConstructionStride).Select(row => U32(profile, row))
            .Where(tag => tag != WeaponParticleBinding.ConfigTemplate).ToHashSet();
        bool joinsFeedSequence = Joins.SelectMany(field => NativeRows.Rows(stock, field, JoinRow, JoinStride))
            .All(row => U32(stock, row + 8) == sequence || U32(stock, row + 40) == sequence);
        bool otherConfigs = NativeRows.Rows(stock, Construction, ConstructionRow, ConstructionStride).Select(row => U32(stock, row))
            .Where(tag => tag != sequence).All(tag => common.Contains(tag) || ResourceClass(catalog.Read(tag)) == VariableStoreClass);
        return joinsFeedSequence && otherConfigs;
    }

    /**
     * Refuses an entity the firing row cannot restart on every shot.
     * @param read Reads a config by tag, including authored ones.
     * @param subject Names the entity in the refusal.
     * @return The tag of the entity's one particle sequence.
     */
    public static uint RequirePerShot(Func<uint, byte[]> read, byte[] entity, string subject)
    {
        var configs = NativeRows.Rows(entity, Construction, ConstructionRow, ConstructionStride).Select(row => U32(entity, row)).ToArray();
        var sequences = configs.Where(tag => WeaponParticleBinding.SequenceResource(read(tag)) >= 0).ToArray();
        if (sequences.Length != 1)
            throw new InvalidDataException($"{subject} needs exactly one particle sequence; it has {sequences.Length}.");
        byte[] sequence = read(sequences[0]);
        byte lifecycle = sequence[WeaponParticleBinding.SequenceResource(sequence) + WeaponParticleBinding.LifecycleOffset];
        if ((lifecycle & (WeaponParticleBinding.StartOnSpawn | WeaponParticleBinding.RetireOnEnd)) != 0)
            throw new InvalidDataException($"{subject} has a sequence that starts on spawn or retires its node "
                + $"(lifecycle {lifecycle:X2}); a firing slot is restarted on every shot.");
        return sequences[0];
    }

    private static uint ResourceClass(byte[] config)
    {
        if (config.Length < 32) return 0;
        long resource = 24 + I64(config, 24);
        return resource >= 4 && resource <= config.Length ? U32(config, (int)resource - 4) : 0;
    }

    private static bool SameFields(byte[] left, int leftRow, byte[] right, int rightRow, int[] fields) =>
        fields.All(field => U32(left, leftRow + field) == U32(right, rightRow + field));

    /** Keeps the rows the predicate does not select, in order; an emptied table has no array. */
    private static void DropRows(byte[] bytes, int field, int stride, Func<int, bool> drop)
    {
        int[] rows = NativeRows.Rows(bytes, field, JoinRow, stride);
        int[] kept = rows.Where(row => !drop(row)).ToArray();
        if (kept.Length == rows.Length) return;
        byte[][] copies = kept.Select(row => bytes.AsSpan(row, stride).ToArray()).ToArray();
        for (int index = 0; index < rows.Length; index++)
            if (index < copies.Length) copies[index].CopyTo(bytes, rows[index]);
            else bytes.AsSpan(rows[index], stride).Clear();
        Put64(bytes, Pointer(bytes, field + 8), kept.Length);
        Put64(bytes, field, kept.Length);
        if (kept.Length == 0) Put64(bytes, field + 8, 0);
    }

    private static int Pointer(byte[] bytes, int at) => checked(at + (int)I64(bytes, at));
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static long I64(byte[] bytes, int at) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void Put64(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
}
