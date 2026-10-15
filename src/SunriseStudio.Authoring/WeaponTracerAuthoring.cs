using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/** Removals are added asset IDs of a tracer copy that is no longer used. */
public sealed record PreparedWeaponTracer(IReadOnlyList<ProjectAssetAddition> Assets,
    IReadOnlyDictionary<uint, byte[]> Changes, IReadOnlyList<string> Removals);

/** WIP, broken in game: recolours the tracer trail of a weapon built on Tommy's Matchbook. */
public static class WeaponTracerAuthoring
{
    // Tommy's projectile entity, its tracer sequence, the trail system and the trail emitter.
    public const uint StockProjectile = 0x81A6B4A6;
    public const uint StockSequence = 0x81A6B4A4, StockSystem = 0x81A6B499, StockEmitter = 0x81A6B498;
    // The sequence names the trail system here; the system names its emitter at byte 0.
    private const int TrailSystemAt = 0xFA0, EmitterAt = 0;
    // Emitter program constants c10 and c11: trail start and end colour as RGBA floats.
    private const int StartAt = 0x3B0, EndAt = 0x3C0;
    public static readonly Vector4 StockStart = new(1f, 0.545724f, 0.109462f, 1f);
    public static readonly Vector4 StockEnd = new(1f, 0.201556f, 0.038204f, 1f);
    private const int Slot = WeaponFiringRow.ProjectileSlot;

    /** Null when the base row's projectile is Tommy's layout; otherwise a short reason. */
    public static string? Refusal(PackageCatalog catalog, WeaponFiringRow row) =>
        Refusal(catalog, row.StockTag(catalog, Slot));

    /** Null only for Tommy's projectile with the installed trail records. */
    public static string? Refusal(PackageCatalog catalog, uint stockProjectile)
    {
        if (stockProjectile != StockProjectile) return "Needs Tommy's Matchbook as base.";
        byte[] sequence = catalog.ReadOriginal(StockSequence), system = catalog.ReadOriginal(StockSystem);
        byte[] emitter = catalog.ReadOriginal(StockEmitter);
        if (sequence.Length < TrailSystemAt + 4 || U32(sequence, TrailSystemAt) != StockSystem
            || system.Length < 4 || U32(system, EmitterAt) != StockEmitter
            || emitter.Length < EndAt + 16 || !Near(Colour(emitter, StartAt), StockStart))
            return "The installed tracer records changed.";
        return null;
    }

    /**
     * Gives the weapon's projectile slot a recoloured copy of Tommy's projectile.
     * A slot that already holds the copy keeps it; only the emitter colours change.
     */
    public static PreparedWeaponTracer Prepare(StudioProject project, PackageCatalog catalog, uint weaponTag,
        Vector4 start, Vector4 end, ushort packageId, int firstEntry)
    {
        if (!IsColour(start) || !IsColour(end)) throw new ArgumentException("Tracer colours must be finite and not negative.");
        var row = WeaponFiringRow.Find(project, catalog, catalog.Read, weaponTag);
        if (Refusal(catalog, row) is { } refusal) throw new InvalidDataException(refusal);
        byte[] hud = catalog.Read(row.Hud).ToArray();
        uint[] chain = Chain(Owned(project), catalog, U32(hud, row.Row + Slot));
        if (chain.Length == 4)
        {
            byte[] emitter = catalog.Read(chain[3]).ToArray();
            WriteColours(emitter, start, end);
            return new([], new Dictionary<uint, byte[]> { [chain[3]] = emitter }, []);
        }
        if (chain.Length != 0) throw new InvalidDataException("The tracer copy is incomplete. Reset the tracer colour first.");

        string name = project.AddedAssets.FirstOrDefault(asset => asset.Native is { } native
            && WeaponFiringRow.NativeTag(native) == weaponTag)?.Name.Split('.')[0] ?? "weapon";
        var copies = Copies(catalog, start, end, packageId, firstEntry, name + ".tracer");
        Put32(hud, row.Row + Slot, WeaponFiringRow.NativeTag(copies[^1].Native!));
        return new(copies, new Dictionary<uint, byte[]> { [row.Hud] = hud }, []);
    }

    /** Puts the base row's projectile back in the slot and drops the tracer copy. */
    public static PreparedWeaponTracer PrepareReset(StudioProject project, PackageCatalog catalog, uint weaponTag)
    {
        var row = WeaponFiringRow.Find(project, catalog, catalog.Read, weaponTag);
        var owned = Owned(project);
        byte[] hud = catalog.Read(row.Hud).ToArray();
        uint[] chain = Chain(owned, catalog, U32(hud, row.Row + Slot));
        if (chain.Length == 0) return new([], new Dictionary<uint, byte[]>(), []);
        Put32(hud, row.Row + Slot, row.StockTag(catalog, Slot));
        return new([], new Dictionary<uint, byte[]> { [row.Hud] = hud }, chain.Select(tag => owned[tag].Id).ToArray());
    }

    /** The trail colours of the slot's tracer copy, or the stock colours when the slot has none. */
    public static (Vector4 Start, Vector4 End, bool Custom) Read(StudioProject project, PackageCatalog catalog, WeaponFiringRow row)
    {
        uint[] chain = Chain(Owned(project), catalog, U32(catalog.Read(row.Hud), row.Row + Slot));
        bool custom = chain.Length == 4;
        byte[] emitter = custom ? catalog.Read(chain[3]) : catalog.ReadOriginal(StockEmitter);
        return (Colour(emitter, StartAt), Colour(emitter, EndAt), custom);
    }

    /**
     * Byte copies of the emitter, system, sequence and projectile entity, in that entry order.
     * Only the colours, the links between the copies and the sequence's own tag change.
     */
    public static IReadOnlyList<ProjectAssetAddition> Copies(PackageCatalog catalog, Vector4 start, Vector4 end,
        ushort packageId, int firstEntry, string name)
    {
        uint Tag(int entry) => checked(0x80800000u + ((uint)packageId << 13) + (uint)entry);
        // A package holds entries 0 to 8191.
        if (firstEntry < 0 || firstEntry + 3 > 8191
            || Enumerable.Range(firstEntry, 4).Any(entry => catalog.ContainsTag(Tag(entry))))
            throw new InvalidDataException("The tracer copy needs 4 free package entries.");
        uint emitterTag = Tag(firstEntry), systemTag = Tag(firstEntry + 1), sequenceTag = Tag(firstEntry + 2);

        byte[] emitter = catalog.ReadOriginal(StockEmitter).ToArray();
        WriteColours(emitter, start, end);
        byte[] system = catalog.ReadOriginal(StockSystem).ToArray();
        Put32(system, EmitterAt, emitterTag);
        // The entity holds typed links and offsets into the sequence, so the sequence keeps its layout.
        byte[] sequence = Retag(catalog.ReadOriginal(StockSequence), StockSequence, sequenceTag);
        Put32(sequence, TrailSystemAt, systemTag);
        byte[] entity = Retag(catalog.ReadOriginal(StockProjectile), StockSequence, sequenceTag);

        return [Addition(StockEmitter, emitter, ".trail.emitter", 0), Addition(StockSystem, system, ".trail", 1),
            Addition(StockSequence, sequence, ".sequence", 2), Addition(StockProjectile, entity, "", 3)];

        ProjectAssetAddition Addition(uint stock, byte[] data, string suffix, int offset)
        {
            catalog.TryGetEntry(stock, out var entry);
            return new(name + suffix, data, new(packageId, entry.Reference, entry.TypeInfo, name + suffix, firstEntry + offset), stock);
        }
    }

    /** The owned copy in the slot as entity, sequence, system, emitter; shorter when a link is missing. */
    private static uint[] Chain(IReadOnlyDictionary<uint, AddedProjectAsset> owned, PackageCatalog catalog, uint entity)
    {
        bool IsCopy(uint tag, uint stock) => owned.TryGetValue(tag, out var asset) && asset.BaseTagHash == stock;
        if (!IsCopy(entity, StockProjectile)) return [];
        byte[] entityBytes = catalog.Read(entity);
        uint sequence = Enumerable.Range(0, entityBytes.Length / 4).Select(index => U32(entityBytes, index * 4))
            .FirstOrDefault(tag => IsCopy(tag, StockSequence));
        if (sequence == 0) return [entity];
        byte[] sequenceBytes = catalog.Read(sequence);
        uint system = sequenceBytes.Length >= TrailSystemAt + 4 ? U32(sequenceBytes, TrailSystemAt) : 0;
        if (!IsCopy(system, StockSystem)) return [entity, sequence];
        uint emitter = U32(catalog.Read(system), EmitterAt);
        return IsCopy(emitter, StockEmitter) ? [entity, sequence, system, emitter] : [entity, sequence, system];
    }

    private static Dictionary<uint, AddedProjectAsset> Owned(StudioProject project) => project.AddedAssets
        .Where(asset => asset.Native is not null).ToDictionary(asset => WeaponFiringRow.NativeTag(asset.Native!));

    /** Replaces every aligned word that holds the tag. */
    private static byte[] Retag(byte[] source, uint from, uint to)
    {
        byte[] copy = source.ToArray();
        for (int at = 0; at + 4 <= copy.Length; at += 4)
            if (U32(copy, at) == from) Put32(copy, at, to);
        return copy;
    }

    private static void WriteColours(byte[] emitter, Vector4 start, Vector4 end)
    {
        for (int k = 0; k < 4; k++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(emitter.AsSpan(StartAt + k * 4), start[k]);
            BinaryPrimitives.WriteSingleLittleEndian(emitter.AsSpan(EndAt + k * 4), end[k]);
        }
    }

    private static Vector4 Colour(byte[] emitter, int at) => new(Single(emitter, at), Single(emitter, at + 4),
        Single(emitter, at + 8), Single(emitter, at + 12));

    private static bool IsColour(Vector4 colour) =>
        float.IsFinite(colour.X + colour.Y + colour.Z + colour.W) && Vector4.Min(colour, Vector4.Zero) == Vector4.Zero;
    private static bool Near(Vector4 a, Vector4 b) => Vector4.DistanceSquared(a, b) < 1e-8f;
    private static float Single(byte[] bytes, int at) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at));
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
}
