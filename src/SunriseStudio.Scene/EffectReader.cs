using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/** Reads particle-system nodes, particle systems, emitters and lens flares from typed package data. */
public sealed class EffectReader(PackageCatalog catalog)
{
    public const uint SystemClass = 0x80806E28;
    public const uint EmitterClass = 0x80806E2C;
    public const uint MaterialClass = 0x808071E8;
    public const uint FlareResourceClass = 0x80806CBF;
    public const uint FlareClass = 0x80806F68;
    // An embedded node is a {node class, owner config, link class, relative} header followed by its body.
    private const uint NodeClass = 0x80806CC6;
    private const uint NodeLinkClass = 0x80806CC5;
    private const uint NodeRowClass = 0x80806CC8;
    // The sequence resource holds a {config, peer class, peer offset} location table at +0x1A0.
    private const uint SequenceClass = 0x808084E9;
    private const uint LocationPairWord = 0x8080860C;
    // An animation clip lists name hashes at +0x90 as a u32 array of this class.
    private const uint ClipClass = 0x80808F49;
    private const uint ClipChannelClass = 0x80800070;

    /** Names of the 55 emitter channels, in the order of the native table at 0x7FF743CA0AA0. */
    public static readonly string[] ChannelNames =
    [
        "particle_age", "particle_random", "particle_system_age", "particle_system_random", "particle_emit_time",
        "particle_lifetime", "particle_position", "particle_velocity", "initial_particle_position",
        "initial_particle_velocity", "emission_rate", "emitter_shape_scale", "emitter_angle", "emitter_radius",
        "emitter_length", "initial_speed", "initial_particle_count", "particle_max_count", "acceleration0_direction",
        "acceleration0_strength", "acceleration1_direction", "acceleration1_strength", "air_friction",
        "angular_velocity", "initial_rotation_rate", "rotational_acceleration", "rotational_friction",
        "angular_position", "next_primitive_position", "chain_distance", "seconds_elapsed", "gravity_strength",
        "system_lod", "particle_volume_length", "particle_volume_density", "segment_distance",
        "inherit_parent_velocity", "spawn_z_axis", "emit_time", "light_probe_uvs", "ao_dc", "fluid_density",
        "particle_age_cpu", "particle_spawn_id", "grid_cell_size", "grid_view_offset", "light_sample_index",
        "spawn_count_this_frame", "spline_prediction", "distance_emission", "emitted_minimum",
        "system_camera_distance", "particle_camera_distance", "particle_camera_planar_distance", "emitter_grid_count"
    ];

    private readonly Dictionary<uint, IReadOnlyList<EffectDescription>> entities = [];
    private readonly Dictionary<uint, EffectSystem?> systems = [];
    private readonly Dictionary<uint, EffectEmitter?> emitters = [];
    private readonly Dictionary<uint, IReadOnlyList<EffectFlareElement>> flares = [];
    private readonly Dictionary<uint, IReadOnlyDictionary<int, EffectActivation>> sequences = [];
    private readonly Dictionary<uint, IReadOnlyList<uint>> clipChannels = [];

    private uint Class(uint tag) => catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0;
    private TagData Read(uint tag) => new(catalog.Read(tag));

    /** Every particle-system node in the entity's component configs; the transform is left at identity. */
    public IReadOnlyList<EffectDescription> ReadEntity(uint entity)
    {
        if (entities.TryGetValue(entity, out var cached)) return cached;
        var result = new List<EffectDescription>();
        if (Class(entity) == 0x80809C0F)
        {
            var definition = Read(entity);
            foreach (var build in definition.Rows(0x10, 0x80809C04, 12))
            {
                uint config = definition.U32(build);
                if (Class(config) != 0x80809C36) continue;
                ReadConfigNodes(entity, config, Read(config), result);
            }
        }
        return entities[entity] = result;
    }

    /** Nodes are found by their typed header; the owner word must name the config itself. */
    private void ReadConfigNodes(uint entity, uint config, TagData data, List<EffectDescription> result)
    {
        List<uint>? variants = null;
        EffectLocation[]? table = null;
        IReadOnlyDictionary<int, EffectActivation>? activations = null;
        for (int marker = 8; marker + 0x50 <= data.Length; marker += 4)
        {
            if (data.U32(marker) != NodeLinkClass || data.U32(marker - 4) != config || data.U32(marker - 8) != NodeClass)
                continue;
            int node = marker + 0xC;
            var list = new List<EffectSystem>();
            var locations = new List<IReadOnlyList<EffectLocation>>();
            bool resolved = true;
            foreach (int row in data.Rows(node + 0x30, NodeRowClass, 0x18))
            {
                uint tag = data.U32(row + 0x10);
                EffectSystem? system;
                if (Class(tag) == SystemClass) system = ReadSystem(tag);
                else
                {
                    variants ??= VariantSystems(data);
                    system = variants.Count == 0 ? null : ReadSystem(variants[0]);
                    if (system is not null) resolved = false;
                }
                if (system is null) continue;
                list.Add(system);
                table ??= LocationTable(config, data);
                locations.Add(RowLocations(data, row, table));
            }
            if (list.Count == 0) continue;
            activations ??= Activations(config, data);
            result.Add(new EffectDescription
            {
                Kind = EffectKind.ParticleSystem,
                EntityTag = entity,
                SourceTag = config,
                SourceOffset = node,
                NodeName = data.U32(node),
                StartMin = data.F32(node + 0x0C),
                StartMax = data.F32(node + 0x10),
                DurationMin = data.F32(node + 0x14),
                DurationMax = data.F32(node + 0x18),
                Systems = list,
                Locations = locations,
                Activation = activations.GetValueOrDefault(node),
                VariantResolved = resolved
            });
        }
    }

    /** Parsed once per config; a sequence that does not decode gives no activation. */
    private IReadOnlyDictionary<int, EffectActivation> Activations(uint config, TagData data)
    {
        if (sequences.TryGetValue(config, out var cached)) return cached;
        IReadOnlyDictionary<int, EffectActivation> result;
        try { result = EffectSequence.Read(config, data); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException
                                        or IndexOutOfRangeException)
        { result = new Dictionary<int, EffectActivation>(); }
        return sequences[config] = result;
    }

    /** Name hashes a clip lists at +0x90; that a clip drives these channels is assumed. */
    public IReadOnlyList<uint> ReadClipChannels(uint clip)
    {
        if (clipChannels.TryGetValue(clip, out var cached)) return cached;
        uint[] result = [];
        try
        {
            if (Class(clip) == ClipClass)
            {
                var data = Read(clip);
                if (data.Has(0x90, 16)) result = data.Rows(0x90, ClipChannelClass, 4).Select(data.U32).ToArray();
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or KeyNotFoundException
                                        or ArgumentException or OverflowException) { }
        return clipChannels[clip] = result;
    }

    /** Each byte of a node row indexes the location table, one byte per emitter frame slot. */
    private static EffectLocation[] RowLocations(TagData data, int row, EffectLocation[] table) =>
        table.Length == 0 ? [] : data.Rows(row, 0x80800009, 1).Select(at => data.U8(at) < table.Length
            ? table[data.U8(at)]
            : throw new InvalidDataException($"Particle row 0x{row:X} names location {data.U8(at)} of {table.Length}."))
            .ToArray();

    /**
     * The location table of the sequence resource at config +0x18; none for another resource.
     * A location row is {slot, count, first source, source count, flags}; it joins all its sources.
     */
    private static EffectLocation[] LocationTable(uint config, TagData data)
    {
        int resource = data.Relative(0x18);
        int table = resource + 0x1A0;
        if (!data.Has(resource - 4, 0x1E4) || data.U32(resource - 4) != SequenceClass
            || data.U32(table) != config || data.U32(table + 4) != LocationPairWord) return [];
        var sources = data.Rows(table + 0x20, 0x80808615, 16);
        var names = data.Rows(table + 0x30, 0x80808614, 4);
        return data.Rows(table + 0x10, 0x80808617, 6).Select((at, index) =>
        {
            int first = data.U8(at + 2), count = data.U8(at + 3);
            if (count == 0 || first >= sources.Length)
                throw new InvalidDataException($"Location {index} of 0x{config:X8} has no source.");
            var list = sources.Skip(first).Take(count).Select(source => new EffectSource(data.U8(source),
                data.U8(source + 2), data.U8(source + 4), data.U32(source + 8), data.U8(source + 12))).ToArray();
            return new EffectLocation(index, index < names.Length ? data.U32(names[index]) : 0, data.U8(at + 1),
                data.U8(at + 4), list);
        }).ToArray();
    }

    /** Particle systems listed in the config's variant tag arrays, in authored order. */
    private List<uint> VariantSystems(TagData data)
    {
        var found = new List<uint>();
        for (int header = 4; header + 16 <= data.Length; header += 4)
        {
            if (data.U32(header - 4) != 0x80809FBD || data.U32(header + 8) != 0x80800014) continue;
            long count = data.I64(header);
            if (count <= 0 || header + 16 + count * 4 > data.Length) continue;
            for (int i = 0; i < count; i++)
            {
                uint tag = data.U32(header + 16 + i * 4);
                if (Class(tag) == SystemClass && !found.Contains(tag)) found.Add(tag);
            }
        }
        return found;
    }

    /** Offsets +4, +8 and +0x10 hold compute techniques only on GPU-simulated systems. */
    public EffectSystem? ReadSystem(uint tag)
    {
        if (systems.TryGetValue(tag, out var cached)) return cached;
        EffectSystem? system = null;
        if (Class(tag) == SystemClass)
        {
            var data = Read(tag);
            if (data.Length >= 0x20)
            {
                var compute = new[] { 4, 8, 0x10 }.Select(data.U32).Where(t => Class(t) == MaterialClass).ToArray();
                uint emitter = data.U32(0);
                uint raster = data.U32(0x14);
                system = new EffectSystem(tag, emitter, Class(raster) == MaterialClass ? raster : 0,
                    data.U32(0x18), compute, Class(emitter) == EmitterClass ? ReadEmitter(emitter) : null);
            }
        }
        return systems[tag] = system;
    }

    /** Channel kind 6 selects one float of the emitter's constant vector array at +0x08. */
    public EffectEmitter? ReadEmitter(uint tag)
    {
        if (emitters.TryGetValue(tag, out var cached)) return cached;
        EffectEmitter? emitter = null;
        var data = Read(tag);
        if (data.Length >= 0x80 + 2 * ChannelNames.Length)
        {
            var rows = data.Rows(0x08, 0x80800090, 16);
            float Float(int index) => data.F32(rows[index / 4] + index % 4 * 4);
            var channels = new List<EffectChannel>();
            for (int i = 0; i < ChannelNames.Length; i++)
            {
                byte kind = data.U8(0x80 + 2 * i), slot = data.U8(0x81 + 2 * i);
                if (kind == 0xFF) continue;
                Vector4? constant = null;
                if (kind == 6 && slot < rows.Length * 4)
                    constant = new Vector4(Float(slot), slot + 1 < rows.Length * 4 ? Float(slot + 1) : 0,
                        slot + 2 < rows.Length * 4 ? Float(slot + 2) : 0, slot + 3 < rows.Length * 4 ? Float(slot + 3) : 0);
                channels.Add(new EffectChannel(i, ChannelNames[i], kind, slot, constant));
            }
            var constants = data.Rows(0x60, 0x80800090, 16);
            emitter = new EffectEmitter(tag, channels)
            {
                Program = data.Rows(0x50, 0x80800009, 1).Select(data.U8).ToArray(),
                StageLengths = Enumerable.Range(0, 8).Select(i => data.U16(0x70 + 2 * i)).ToArray(),
                ProgramConstants = constants.Select(row => Vector(data, row)).ToArray(),
                ChannelConstants = rows.Select(row => Vector(data, row)).ToArray(),
                Shape = data.U8(0x115),
                Direction = data.U8(0x116),
                SpawnSlot = data.U8(0x40),
                EmitterSlot = data.U8(0x41),
                SlotList = data.RowsOfAnyClass(0x18, 1).Select(data.U8).ToArray(),
                SlotGroups = data.RowsOfAnyClass(0x28, 2).Select(at => new EffectSlotGroup(data.U8(at), data.U8(at + 1))).ToArray(),
                SpawnGroup = new(data.U8(0x38), data.U8(0x39)),
                SimulationGroup = new(data.U8(0x3A), data.U8(0x3B)),
                SlotCount = data.U8(0x47),
                LocalAcceleration0 = data.U8(0x149) != 0,
                LocalAcceleration1 = data.U8(0x14A) != 0
            };
            if (emitter.StageLengths.Sum(length => length) != emitter.Program.Length)
                throw new InvalidDataException($"Emitter 0x{tag:X8} stage lengths disagree with its program.");
        }
        return emitters[tag] = emitter;
    }

    private static Vector4 Vector(TagData data, int at) =>
        new(data.F32(at), data.F32(at + 4), data.F32(at + 8), data.F32(at + 12));

    /** Lens-flare rows are twelve bytes: material, parameter block, flag. */
    public IReadOnlyList<EffectFlareElement> ReadFlare(uint tag)
    {
        if (flares.TryGetValue(tag, out var cached)) return cached;
        IReadOnlyList<EffectFlareElement> result = [];
        if (Class(tag) == FlareClass)
        {
            var data = Read(tag);
            result = data.Rows(0x20, 0x80806F6D, 12)
                .Select(row => new EffectFlareElement(data.U32(row), data.U32(row + 4)))
                .Where(e => Class(e.Material) == MaterialClass).ToArray();
        }
        return flares[tag] = result;
    }
}
