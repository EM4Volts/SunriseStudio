namespace SunriseStudio.Scene;

/** A sequence start condition; kind 0 is a channel range [Min, Max). */
internal sealed record SequenceCondition(byte Kind, float Min, float Max, int Channel);

/**
 * A flow or work node as stored; offsets are from the body, 16 bytes after its wrapper.
 * Only flow nodes carry children, a start condition and a gate range.
 */
internal sealed record SequenceNode(int Body, byte Flags, float DelayMin, float DurationMax, byte Type, byte Loop,
    (int Kind, int Index)[] Children, SequenceCondition? Start, float RangeMin, float RangeMax, int RangeChannel)
{
    // Flag bits 0x08 and 0x10 mean the node has no timer.
    private const byte NoTimer = 0x18;
    public bool Timed => (Flags & NoTimer) == 0;
}

/** The class-0x808084E9 sequence resource: its nodes, input channels and the first path to each node. */
internal sealed class SequenceResource
{
    public const uint ResourceClass = 0x808084E9;
    private const uint NodeRowClass = 0x808093E6, InputClass = 0x80809789;
    // Resource fields: flow and work rows, input channels, root, flags (bit 0 starts on spawn).
    public const int FlowRows = 0x158, WorkRows = 0x168, InputRows = 0x188, Root = 0x2D0, Flags = 0x2E0;
    private const int NodeStride = 24, InputStride = 40, InputName = 0x20;
    // Root and child references: kind 0 flow, 1 work.
    public const int FlowKind = 0, WorkKind = 1;

    public SequenceNode[] Flows { get; }
    public SequenceNode[] Works { get; }
    /** Input channel names, in the order gates and start conditions index them. */
    public uint[] Inputs { get; }
    public bool AutoStart { get; }
    /** The first path that reaches each node, as (flow, child position) steps from the root. */
    public Dictionary<(int Kind, int Index), (int Flow, int Position)[]> Paths { get; } = [];

    /** Throws InvalidDataException when a node or row leaves the data. */
    public SequenceResource(TagData data, int resource)
    {
        Flows = data.Rows(resource + FlowRows, NodeRowClass, NodeStride).Select(row => ReadNode(data, row, true)).ToArray();
        Works = data.Rows(resource + WorkRows, NodeRowClass, NodeStride).Select(row => ReadNode(data, row, false)).ToArray();
        Inputs = data.Rows(resource + InputRows, InputClass, InputStride).Select(row => data.U32(row + InputName)).ToArray();
        uint root = data.U32(resource + Root);
        AutoStart = (data.U8(resource + Flags) & 1) != 0;
        Visit((int)(root & 0xFF), (int)(root >> 16), []);
    }

    /** The first path that reaches each node wins; a revisit ends the walk there. */
    private void Visit(int kind, int index, (int Flow, int Position)[] path)
    {
        if (kind is not (FlowKind or WorkKind) || !Paths.TryAdd((kind, index), path)) return;
        if (kind != FlowKind || index >= Flows.Length) return;
        var children = Flows[index].Children;
        for (int position = 0; position < children.Length; position++)
            Visit(children[position].Kind, children[position].Index, [.. path, (index, position)]);
    }

    /** A row's relative pointer at +0x10 lands on the wrapper; the body is 16 bytes later. */
    private static SequenceNode ReadNode(TagData data, int row, bool flow)
    {
        int body = data.Relative(row + 0x10) + 16;
        if (!data.Has(body, flow ? 0x54 : 0x24)) throw new InvalidDataException($"Sequence node at 0x{row:X} leaves its config.");
        byte type = data.U8(body + 0x20);
        if (!flow)
            return new(body, data.U8(body + 8), data.F32(body + 0x0C), data.F32(body + 0x18), type,
                data.U8(body + 0x22), [], null, 0, 0, -1);
        var children = data.I64(body + 0x28) == 0 ? []
            : data.RowsOfAnyClass(body + 0x28, 4).Select(at => ((int)data.U8(at), (int)data.U16(at + 2))).ToArray();
        SequenceCondition? start = null;
        if (data.I64(body + 0x38) != 0)
        {
            int at = data.Relative(body + 0x38);
            if (!data.Has(at, 16)) throw new InvalidDataException($"Start condition of node 0x{body:X} leaves its config.");
            start = new(data.U8(at), data.F32(at + 4), data.F32(at + 8), data.I32(at + 12));
        }
        return new(body, data.U8(body + 8), data.F32(body + 0x0C), data.F32(body + 0x18), type,
            data.U8(body + 0x22), children, start, data.F32(body + 0x48), data.F32(body + 0x4C), data.I32(body + 0x50));
    }
}
