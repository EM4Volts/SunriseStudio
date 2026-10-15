using System.Buffers.Binary;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Formats;

/** One per-damage-type scale row of a part; Offset is the float's byte offset in the config. */
public readonly record struct CombatantDamageTypeScale(int Index, int Offset, float Scale);

/**
 * One 312-byte damage part row. Health is the float at +0x14; its meaning is assumed.
 * Kind is the signed byte at +0x18; Pool indexes the payload's pool rows.
 */
public sealed record CombatantDamagePart(int Index, int Offset, uint NameHash, float Health, sbyte Kind, int Pool,
    float Scale, IReadOnlyList<CombatantDamageTypeScale> TypeScales)
{
    /** The part's name, its hash when the name is not known, or "(none)" for an empty hash. */
    public string Name => CollisionRegions.Name(NameHash) is { Length: > 0 } name ? name : "(none)";
}

/** One 80-byte damage pool row; Fraction is the initial fraction at +24. */
public readonly record struct CombatantDamagePool(int Index, int Offset, float Fraction);

/** The damage component payload (class 0x80804B8A) behind a damage config's +0x18 link. */
public sealed record CombatantDamage(uint ConfigTag, int Payload, IReadOnlyList<CombatantDamagePart> Parts,
    IReadOnlyList<CombatantDamagePool> Pools)
{
    public const uint PayloadClass = 0x80804B8A;
    private const uint PartClass = 0x80804C11, TypeScaleClass = 0x80804BBB, PoolClass = 0x80804C5F;
    private const int PartStride = 312, TypeScaleStride = 40, PoolStride = 80;
    private const int PartsField = 0x1B0, PoolsField = 0x368;

    /**
     * Reads the damage payload of one config.
     * @return Null when the config is not a damage config or any array does not match its class.
     */
    public static CombatantDamage? Read(uint configTag, ReadOnlyMemory<byte> config)
    {
        try
        {
            var reader = new TagReader(config);
            if (reader.Length < 0x20) return null;
            int payload = reader.Relative(0x18);
            // The payload is typed by the marker before it and names its own config first.
            if (payload < 4 || !reader.Has(payload, PoolsField + 16) || reader.U32(payload - 4) != PayloadClass
                || reader.U32(payload) != configTag) return null;
            var pools = reader.Array(payload + PoolsField, PoolClass, PoolStride);
            var poolRows = new CombatantDamagePool[pools.Count];
            for (int index = 0; index < pools.Count; index++)
            {
                int row = pools.Data + index * PoolStride;
                poolRows[index] = new(index, row + 24, Float(config, row + 24));
            }
            var parts = reader.Array(payload + PartsField, PartClass, PartStride);
            var partRows = new CombatantDamagePart[parts.Count];
            for (int index = 0; index < parts.Count; index++)
            {
                int row = parts.Data + index * PartStride;
                int pool = reader.I32(row + 0xD0);
                if (pool < 0 || pool >= pools.Count) return null;
                var types = reader.Array(row + 0xE8, TypeScaleClass, TypeScaleStride);
                var typeRows = new CombatantDamageTypeScale[types.Count];
                for (int type = 0; type < types.Count; type++)
                {
                    int at = types.Data + type * TypeScaleStride + 0x24;
                    typeRows[type] = new(type, at, Float(config, at));
                }
                partRows[index] = new(index, row, reader.U32(row + 0x10), Float(config, row + 0x14),
                    (sbyte)reader.U8(row + 0x18), pool, Float(config, row + 0xE4), typeRows);
            }
            return new(configTag, payload, partRows, poolRows);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException
            or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /** True when the config's +0x18 link is typed as a damage payload, readable or not. */
    public static bool IsDamageConfig(ReadOnlyMemory<byte> config)
    {
        var reader = new TagReader(config);
        if (reader.Length < 0x20) return false;
        long delta = reader.I64(0x18);
        if (delta == 0 || delta < int.MinValue || delta > int.MaxValue) return false;
        long payload = 0x18 + delta;
        return payload >= 4 && payload <= reader.Length - 4 && reader.U32((int)payload - 4) == PayloadClass;
    }

    /** Sets a part's damage scale at +0xE4. */
    public static byte[] SetPartScale(uint configTag, ReadOnlyMemory<byte> config, int part, float value) =>
        Write(config, value, Require(configTag, config).Parts.ElementAtOrDefault(part)?.Offset + 0xE4);

    /** Sets one per-damage-type scale float at +0x24 of a part's override row. */
    public static byte[] SetTypeScale(uint configTag, ReadOnlyMemory<byte> config, int part, int row, float value) =>
        Write(config, value, Require(configTag, config).Parts.ElementAtOrDefault(part)?.TypeScales
            .Where(scale => scale.Index == row).Select(scale => (int?)scale.Offset).FirstOrDefault());

    /** Sets a pool's initial fraction at +24. */
    public static byte[] SetPoolFraction(uint configTag, ReadOnlyMemory<byte> config, int pool, float value) =>
        Write(config, value, Require(configTag, config).Pools
            .Where(row => row.Index == pool).Select(row => (int?)row.Offset).FirstOrDefault());

    private static CombatantDamage Require(uint configTag, ReadOnlyMemory<byte> config) =>
        Read(configTag, config) ?? throw new InvalidDataException("The config has no damage payload.");

    private static byte[] Write(ReadOnlyMemory<byte> config, float value, int? offset)
    {
        if (offset is not { } at) throw new ArgumentOutOfRangeException(nameof(offset), "The damage row does not exist.");
        if (!float.IsFinite(value)) throw new ArgumentException("The value must be a finite number.", nameof(value));
        byte[] bytes = config.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
        return bytes;
    }

    private static float Float(ReadOnlyMemory<byte> bytes, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(bytes.Span[offset..]);
}
