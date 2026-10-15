using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/** Not tested in game: the Reload burst plays on the gun from firing row slot +0x90. */
public static class WeaponReloadEffectAuthoring
{
    // Stock gated particle entity; its sequence is one looping type-6 gate over one timed particle node.
    public const uint EntityTemplate = 0x80C0D769, SequenceTemplate = 0x80FC61D9;
    // The copy keeps sharing the common config and the variable store.
    private static readonly uint[] TemplateConfigs = [0x80C70B84, SequenceTemplate, 0x815B375D];
    // FNV-1 of "reloading", the channel the weapon publishes while it reloads.
    public const uint ReloadingChannel = 0xECDE609E;
    // Kind 3 with flags 0x22 places the point on the first-person pose.
    private const byte PointLocation = 3, PointFlags = 0x22;
    // Starts on spawn and retires when done, like the stock gated slot sequences.
    private const byte Lifecycle = 0x0B;
    private const uint FlowClass = 0x808093E6, NodeWrapperClass = 0x80806CC6;
    // Row classes of the template sequence's typed arrays.
    private const uint ChannelRow = 0x80809789, LocationRow = 0x80808617, SourceRow = 0x80808615, NameRow = 0x80808614,
        FlagRow = 0x80808618, SystemRow = 0x80806CC8, SlotRow = 0x80800009;
    // Array fields of the sequence resource.
    private const int Flows = 0x158, Works = 0x168, Channels = 0x188, Locations = 0x1B0, Sources = 0x1C0, Names = 0x1D0;
    // The per-location flag array is anchored before the sequence resource.
    private const int LocationFlags = 0x220;
    private const byte GateNode = 6, ParticleNode = 4, PointSource = 2;

    /**
     * The reload burst, its sequence and its entity, in entry order; the entity is last.
     * @param pointHash Name hash of the authored reload point.
     */
    public static IReadOnlyList<ProjectAssetAddition> Prepare(PackageCatalog catalog, WeaponSourceEmitter emitter,
        uint pointHash, ushort packageId, int firstEntry, string name)
    {
        if (!float.IsFinite(emitter.StartSeconds) || emitter.StartSeconds is < 0 or > 60)
            throw new InvalidDataException("The Reload emitter must start within its clip.");
        if (firstEntry < 0 || firstEntry + 3 > 8191) throw new InvalidDataException("The reload effect needs 4 free package entries.");
        var particle = WeaponAssetParticleCompiler.Prepare(catalog, WeaponAssetParticleCompiler.BurstTemplate,
            WeaponAssetParticleCompiler.BurstChannels(emitter), packageId, firstEntry, name);
        int entry = firstEntry + particle.Assets.Count;
        uint sequenceTag = Tag(packageId, entry);
        if (catalog.ContainsTag(sequenceTag) || catalog.ContainsTag(Tag(packageId, entry + 1)))
            throw new InvalidDataException("The reload effect destination is already used.");
        if (!catalog.TryGetEntry(SequenceTemplate, out var sequenceEntry) || !catalog.TryGetEntry(EntityTemplate, out var entityEntry)
            || entityEntry.Reference != WeaponFiringSlotEntity.EntityClass)
            throw new InvalidDataException("The reload effect template is missing.");
        byte[] stockEntity = catalog.Read(EntityTemplate);
        if (!WeaponConfigEdits.ConstructionConfigs(stockEntity).SequenceEqual(TemplateConfigs))
            throw new InvalidDataException("The reload effect template entity changed.");
        byte[] sequence = Sequence(catalog.Read(SequenceTemplate), sequenceTag, particle.SystemTag, pointHash,
            emitter.StartSeconds, WeaponAssetParticleCompiler.BurstSeconds(emitter));
        byte[] entity = WeaponConfigEdits.RetagRoot(stockEntity, SequenceTemplate, sequenceTag);
        return [.. particle.Assets,
            new(name + ".sequence", sequence, new(packageId, sequenceEntry.Reference, sequenceEntry.TypeInfo,
                name + ".sequence", entry), SequenceTemplate),
            new(name + ".effect", entity, new(packageId, entityEntry.Reference, entityEntry.TypeInfo, name, entry + 1), EntityTemplate)];
    }

    /** The template sequence gated on reloading, playing the system once on the point after the delay. */
    private static byte[] Sequence(byte[] template, uint destination, uint system, uint pointHash, float delay, float duration)
    {
        byte[] bytes = WeaponConfigEdits.RetagConfig(template, SequenceTemplate, destination).Bytes;
        int resource = WeaponParticleBinding.SequenceResource(bytes);
        if (resource < 0) throw new InvalidDataException("The reload effect template is not a sequence.");
        int[] gates = NativeRows.Rows(bytes, resource + Flows, FlowClass, 24).Select(row => Body(bytes, row))
            .Where(body => bytes[body + 0x20] == GateNode).ToArray();
        int[] works = NativeRows.Rows(bytes, resource + Works, FlowClass, 24);
        int[] channels = NativeRows.Rows(bytes, resource + Channels, ChannelRow, 40);
        int[] locations = NativeRows.Rows(bytes, resource + Locations, LocationRow, 6);
        int[] sources = NativeRows.Rows(bytes, resource + Sources, SourceRow, 16);
        int[] names = NativeRows.Rows(bytes, resource + Names, NameRow, 4);
        int[] flags = NativeRows.Rows(bytes, LocationFlags, FlagRow, 2);
        if (gates.Length != 1 || works.Length != 1 || channels.Length != 1 || locations.Length != 1
            || sources.Length != 1 || names.Length != 1 || flags.Length != 1 || bytes[sources[0]] != PointSource)
            throw new InvalidDataException("The reload effect template is no longer one gate over one point burst.");
        int node = Body(bytes, works[0]);
        int[] systems = NativeRows.Rows(bytes, node + 0x30, SystemRow, 24);
        if (U32(bytes, node - 20) != NodeWrapperClass || bytes[node + 0x20] != ParticleNode || systems.Length != 1
            || NativeRows.Rows(bytes, systems[0], SlotRow, 1) is not [var slot] || bytes[slot] != 0)
            throw new InvalidDataException("The reload effect template node is no longer one timed particle system.");

        Put32(bytes, channels[0] + 0x20, ReloadingChannel);
        Put32(bytes, systems[0] + 16, system);
        // The location kind and flags are written in its source row and in both side arrays.
        int source = sources[0];
        bytes[source] = PointLocation;
        Put32(bytes, source + 4, PointFlags);
        bytes[locations[0] + 4] = PointFlags;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(flags[0]), PointFlags);
        Put32(bytes, source + 8, pointHash);
        Put32(bytes, names[0], pointHash);
        PutFloat(bytes, node + 0x0C, delay); PutFloat(bytes, node + 0x10, delay);
        PutFloat(bytes, node + 0x14, duration); PutFloat(bytes, node + 0x18, duration);
        bytes[resource + WeaponParticleBinding.LifecycleOffset] = Lifecycle;
        return bytes;
    }

    /** The node body of a flow or work row: 16 bytes into the wrapper the row points at. */
    private static int Body(byte[] bytes, int row) => Pointer(bytes, row + 16) + 16;

    private static uint Tag(ushort package, int entry) => checked(0x80800000u + ((uint)package << 13) + (uint)entry);
    private static int Pointer(byte[] bytes, int at) => checked(at + (int)I64(bytes, at));
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static long I64(byte[] bytes, int at) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void PutFloat(byte[] bytes, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
}
