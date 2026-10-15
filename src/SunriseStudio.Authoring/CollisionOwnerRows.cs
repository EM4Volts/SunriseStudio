using System.Buffers.Binary;

namespace SunriseStudio.Authoring;

/**
 * A class-0x80808A15 collision owner. A hit on instance user data 0x10000 | N with leaf value v
 * returns Materials[Slots[RunStarts[N] + v]]; import `iN` resolves to Shapes[N].
 */
public sealed class CollisionOwnerRows
{
    // Tag data array blocks: u32 marker, i64 count, u64 element class, then the elements.
    private const uint ArrayMarker = 0x80809FBD;
    private const int HeaderSize = 0x60, CompoundField = 0x58, HashField = 0x5C;
    public const uint MaterialClass = 0x80808BCB;
    // Element classes of the five arrays, as installed owners declare them.
    private static readonly (int Field, int Size, uint Class)[] Fields =
        [(0x08, 4, 0x80808BC5), (0x18, 4, 0x808092D9), (0x28, 2, 0x80800006), (0x38, 2, 0x80800006), (0x48, 4, 0x80800014)];

    /** +0x08 material tags, each a class-0x80808BCB record. */
    public List<uint> Materials { get; } = [];
    /** +0x18 material map tags; import `tN` and user data 0 | N resolve through them. */
    public List<uint> MaterialMaps { get; } = [];
    /** +0x28 material slots; each indexes Materials. */
    public List<short> Slots { get; } = [];
    /** +0x38 run starts, one per shape: the first Slots entry of that shape, or -1. */
    public List<short> RunStarts { get; } = [];
    /** +0x48 per-mesh shape packfile tags. */
    public List<uint> Shapes { get; } = [];
    public uint Compound { get; set; }
    public uint Hash { get; set; }

    private readonly Dictionary<int, uint> classes = Fields.ToDictionary(x => x.Field, x => x.Class);
    private int[] order = Fields.Select(x => x.Field).ToArray();
    private byte[] head = new byte[HeaderSize];

    /**
     * Reads an owner and checks that Write gives its bytes back: each block at the next 16-byte
     * boundary after its marker, zero gaps, and the size at +0 equal to the length.
     */
    public static CollisionOwnerRows Read(byte[] bytes)
    {
        if (bytes.Length < HeaderSize || BinaryPrimitives.ReadInt64LittleEndian(bytes) != bytes.Length)
            throw new InvalidDataException("Collision owner size field does not match its length.");
        var owner = new CollisionOwnerRows { Compound = U32(bytes, CompoundField), Hash = U32(bytes, HashField), head = bytes[..HeaderSize] };
        var blocks = new List<(int Header, int Field)>();
        foreach (var (field, size, _) in Fields)
        {
            long count = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field)), relative = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field + 8));
            if (count == 0 && relative == 0) continue;
            long header = field + 8 + relative;
            if (count is < 0 or > 1_000_000 || header < HeaderSize + 4 || header + 16 + size * count > bytes.Length
                || U32(bytes, (int)header - 4) != ArrayMarker || BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan((int)header)) != count)
                throw new InvalidDataException($"Collision owner array +0x{field:X2} is invalid.");
            owner.classes[field] = U32(bytes, (int)header + 8);
            blocks.Add(((int)header, field));
            for (int i = 0; i < count; i++)
            {
                int at = (int)header + 16 + size * i;
                switch (field)
                {
                    case 0x08: owner.Materials.Add(U32(bytes, at)); break;
                    case 0x18: owner.MaterialMaps.Add(U32(bytes, at)); break;
                    case 0x28: owner.Slots.Add(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at))); break;
                    case 0x38: owner.RunStarts.Add(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at))); break;
                    default: owner.Shapes.Add(U32(bytes, at)); break;
                }
            }
        }
        var present = blocks.OrderBy(x => x.Header).Select(x => x.Field).ToList();
        owner.order = present.Concat(Fields.Select(x => x.Field).Where(field => !present.Contains(field))).ToArray();
        if (!owner.Write().AsSpan().SequenceEqual(bytes))
            throw new NotSupportedException("The collision owner has a layout the writer does not reproduce.");
        return owner;
    }

    /** The owner's bytes. An empty array keeps count and offset 0; blocks follow in their read order. */
    public byte[] Write()
    {
        var output = new List<byte>(head);
        var data = new Dictionary<int, byte[]>
        {
            [0x08] = Words(Materials), [0x18] = Words(MaterialMaps), [0x28] = Halves(Slots), [0x38] = Halves(RunStarts), [0x48] = Words(Shapes)
        };
        var counts = new Dictionary<int, int>
        {
            [0x08] = Materials.Count, [0x18] = MaterialMaps.Count, [0x28] = Slots.Count, [0x38] = RunStarts.Count, [0x48] = Shapes.Count
        };
        var headers = new Dictionary<int, int>();
        foreach (int field in order)
        {
            if (counts[field] == 0) continue;
            // The marker sits 4 bytes before a 16-byte aligned block header.
            int header = (output.Count + 4 + 15) & ~15;
            while (output.Count < header) output.Add(0);
            headers[field] = header;
            var block = new byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(block, counts[field]);
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(8), classes[field]);
            output.AddRange(block);
            output.AddRange(data[field]);
            BinaryPrimitives.WriteUInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(output)[(header - 4)..], ArrayMarker);
        }
        var bytes = output.ToArray();
        BinaryPrimitives.WriteInt64LittleEndian(bytes, bytes.Length);
        foreach (var (field, _, _) in Fields)
        {
            bool has = headers.TryGetValue(field, out int header);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(field), has ? counts[field] : 0);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(field + 8), has ? header - field - 8 : 0);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(CompoundField), Compound);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(HashField), Hash);
        return bytes;
    }

    /**
     * The material a leaf value gives on instance user data 0x10000 | shape, or null when the run
     * start is -1, the value is -1 or a slot leaves its array.
     */
    public uint? Material(int shape, long value)
    {
        if (shape < 0 || shape >= RunStarts.Count || RunStarts[shape] < 0 || value < 0) return null;
        long slot = RunStarts[shape] + value;
        if (slot >= Slots.Count || Slots[(int)slot] < 0 || Slots[(int)slot] >= Materials.Count) return null;
        return Materials[Slots[(int)slot]];
    }

    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));

    private static byte[] Words(List<uint> values)
    {
        var bytes = new byte[4 * values.Count];
        for (int i = 0; i < values.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 * i), values[i]);
        return bytes;
    }

    private static byte[] Halves(List<short> values)
    {
        var bytes = new byte[2 * values.Count];
        for (int i = 0; i < values.Count; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2 * i), values[i]);
        return bytes;
    }
}
