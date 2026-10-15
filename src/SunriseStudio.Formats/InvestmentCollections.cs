using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** One presentation node: a page of the Collections screen or one of its groups. */
public sealed record InvestmentCollectionNode(int Index, uint Hash, string Name, IReadOnlyList<int> Collectibles);

/** Collectibles and the presentation nodes that list them; a collectible names its item by index. */
public static class InvestmentCollections
{
    // Collectible rows: parent node rows at +24 (u16 elements), hash at +40, u16 item index at +44,
    // and the arrays each row owns.
    public const int CollectibleStride = 184, CollectibleParents = 24, CollectibleHash = 40, CollectibleItem = 44;
    private const uint ParentListClass = 0x80803962;
    public static readonly IReadOnlyList<int> CollectibleArrays = [24, 48, 64, 80, 96, 112, 136, 168];
    // Collectible display rows pair row for row: hash at +0, u32 icon row at +4, name {u32 bank, u32 hash} at +8.
    public const int DisplayStride = 96, DisplayHash = 0, DisplayIcon = 4, DisplayName = 8;
    // Flavor and source lines are {u32 bank, u32 hash} pairs too.
    public const int DisplayFlavor = 16, DisplaySource = 24;
    public static readonly IReadOnlyList<int> DisplayArrays = [72];
    // Node rows: hash at +40; child nodes at +104 (24-byte rows, u16 node at +0);
    // child collectibles at +120 (4-byte rows, u16 collectible then a u16 the game sets to 0x7FFF).
    public const int NodeStride = 168, NodeHash = 40, NodeChildren = 104, NodeCollectibles = 120;
    private const int ChildStride = 24, CollectibleChildStride = 4, TableArray = 8;
    private const ushort CollectibleChildTail = 0x7FFF;
    // Node display rows pair row for row: name {u32 bank, u32 hash} at +8.
    public const int NodeDisplayStride = 44, NodeDisplayName = 8;
    /** The Collections root node; badge and triumph nodes sit under other roots. */
    public const uint CollectionsRoot = 0xE1EA9713;

    /** @return The collectible row that names item `itemIndex`, or -1. */
    public static int FindCollectible(ReadOnlyMemory<byte> collectibles, int itemIndex)
    {
        var reader = new TagReader(collectibles);
        var rows = reader.Array(TableArray, stride: CollectibleStride);
        for (int row = 0; row < rows.Count; row++)
            if (BinaryPrimitives.ReadUInt16LittleEndian(collectibles.Span[(rows.Data + row * CollectibleStride + CollectibleItem)..]) == itemIndex)
                return row;
        return -1;
    }

    /**
     * Reads every node under the Collections root that lists collectibles.
     * @param name Resolves a {bank, hash} string pair from the node display table.
     */
    public static IReadOnlyList<InvestmentCollectionNode> ReadNodes(ReadOnlyMemory<byte> nodes, ReadOnlyMemory<byte> nodeDisplays,
        Func<uint, uint, string> name)
    {
        var reader = new TagReader(nodes);
        var rows = reader.Array(TableArray, stride: NodeStride);
        var displays = new TagReader(nodeDisplays).Array(TableArray, stride: NodeDisplayStride);
        if (displays.Count != rows.Count) throw new InvalidDataException("The node and node display tables differ in length.");
        var parents = new Dictionary<int, int>();
        for (int row = 0; row < rows.Count; row++)
            foreach (int child in Children(reader, rows.Data + row * NodeStride + NodeChildren, ChildStride))
                parents.TryAdd(child, row);
        bool UnderRoot(int row)
        {
            for (int depth = 0; depth < rows.Count; depth++)
            {
                if (reader.U32(rows.Data + row * NodeStride + NodeHash) == CollectionsRoot) return true;
                if (!parents.TryGetValue(row, out row)) return false;
            }
            return false;
        }
        var result = new List<InvestmentCollectionNode>();
        for (int row = 0; row < rows.Count; row++)
        {
            var collectibles = Children(reader, rows.Data + row * NodeStride + NodeCollectibles, CollectibleChildStride);
            if (collectibles.Count == 0 || !UnderRoot(row)) continue;
            int display = displays.Data + row * NodeDisplayStride + NodeDisplayName;
            string label = name(BinaryPrimitives.ReadUInt32LittleEndian(nodeDisplays.Span[display..]),
                BinaryPrimitives.ReadUInt32LittleEndian(nodeDisplays.Span[(display + 4)..]));
            result.Add(new(row, reader.U32(rows.Data + row * NodeStride + NodeHash), label, collectibles));
        }
        return result;
    }

    /**
     * Adds a collectible for `itemIndex`, copied from row `baseRow`: it shares that row's unlock
     * expressions, so it shows as acquired whenever the base collectible does. Its parent list
     * names only `parentNode`. It shows the item's flavor text and no source line.
     * @param stockLength Installed collectible table length, so moved rows leave no dead copy.
     * @return Both grown tables and the new collectible row.
     */
    public static (byte[] Collectibles, byte[] Displays, int Row) AddCollectible(ReadOnlyMemory<byte> collectibles,
        ReadOnlyMemory<byte> displays, int baseRow, int itemIndex, uint hash, uint nameBank, uint nameHash, int parentNode,
        int stockLength, ushort? icon = null, (uint Bank, uint Hash)? flavor = null)
    {
        if (parentNode is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(parentNode));
        var rows = new TagReader(collectibles).Array(TableArray, stride: CollectibleStride);
        var displayRows = new TagReader(displays).Array(TableArray, stride: DisplayStride);
        if (rows.Count != displayRows.Count) throw new InvalidDataException("The collectible and display tables differ in length.");
        if (baseRow < 0 || baseRow >= rows.Count) throw new ArgumentOutOfRangeException(nameof(baseRow));
        if (itemIndex is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(itemIndex));
        if (rows.Count + 1 > InvestmentItemClone.MaximumIndexRows) throw new InvalidDataException("The collectible table has no row left.");
        int source = rows.Data + baseRow * CollectibleStride, displaySource = displayRows.Data + baseRow * DisplayStride;
        byte[] row = collectibles.Span.Slice(source, CollectibleStride).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(CollectibleHash), hash);
        BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(CollectibleItem), (ushort)itemIndex);
        byte[] display = displays.Span.Slice(displaySource, DisplayStride).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(display.AsSpan(DisplayHash), hash);
        BinaryPrimitives.WriteUInt32LittleEndian(display.AsSpan(DisplayName), nameBank);
        BinaryPrimitives.WriteUInt32LittleEndian(display.AsSpan(DisplayName + 4), nameHash);
        if (icon is { } iconRow) BinaryPrimitives.WriteUInt32LittleEndian(display.AsSpan(DisplayIcon), iconRow);
        if (flavor is { } text)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(display.AsSpan(DisplayFlavor), text.Bank);
            BinaryPrimitives.WriteUInt32LittleEndian(display.AsSpan(DisplayFlavor + 4), text.Hash);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(display.AsSpan(DisplaySource + 4), InvestmentItemClone.EmptyStringHash);
        byte[] parentClass = new byte[8], parents = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt32LittleEndian(parentClass, ParentListClass);
        BinaryPrimitives.WriteUInt16LittleEndian(parents, (ushort)parentNode);
        // Block before rows: the rows stay last, so the next append reuses their bytes and leaves no dead copy.
        var (grown, blocks) = InvestmentItemClone.AppendRows(collectibles,
            [new(TableArray, CollectibleStride, [row]) { RowArrays = CollectibleArrays, RowSources = [source] }],
            [InvestmentItemClone.TypedBlock(1, parentClass, parents)], stockLength);
        int field = new TagReader(grown).Array(TableArray, stride: CollectibleStride).Data
            + rows.Count * CollectibleStride + CollectibleParents;
        BinaryPrimitives.WriteInt64LittleEndian(grown.AsSpan(field), 1);
        BinaryPrimitives.WriteInt64LittleEndian(grown.AsSpan(field + 8), blocks[0] + InvestmentItemClone.TypedBlockHeader - field - 8);
        return (grown,
            InvestmentItemClone.AppendRow(displays, TableArray, DisplayStride, display, DisplayArrays, displaySource), rows.Count);
    }

    /** Sets one {bank, hash} text line of every collectible display that names `itemIndex`. */
    public static byte[] SetDisplayText(ReadOnlyMemory<byte> collectibles, ReadOnlyMemory<byte> displays, int itemIndex,
        int field, uint bank, uint hash)
    {
        if (field is not (DisplayName or DisplayFlavor or DisplaySource)) throw new ArgumentOutOfRangeException(nameof(field));
        var source = new TagReader(collectibles);
        var sourceRows = source.Array(TableArray, stride: CollectibleStride);
        var targetRows = new TagReader(displays).Array(TableArray, stride: DisplayStride);
        if (sourceRows.Count != targetRows.Count) throw new InvalidDataException("Collectible and display rows do not pair.");
        byte[] result = displays.ToArray();
        for (int index = 0; index < sourceRows.Count; index++)
        {
            if (source.U16(sourceRows.Data + index * CollectibleStride + CollectibleItem) != itemIndex) continue;
            int at = targetRows.Data + index * DisplayStride + field;
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at), bank);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at + 4), hash);
        }
        return result;
    }

    /** @return Every collectible hash in the table, for picking an unused one. */
    public static IReadOnlySet<uint> CollectibleHashes(ReadOnlyMemory<byte> collectibles)
    {
        var reader = new TagReader(collectibles);
        var rows = reader.Array(TableArray, stride: CollectibleStride);
        return Enumerable.Range(0, rows.Count).Select(row => reader.U32(rows.Data + row * CollectibleStride + CollectibleHash)).ToHashSet();
    }

    /** Adds collectible `collectible` to the end of node `node`'s collectible list. */
    public static byte[] AddToNode(ReadOnlyMemory<byte> nodes, int node, int collectible)
    {
        var rows = new TagReader(nodes).Array(TableArray, stride: NodeStride);
        if (node < 0 || node >= rows.Count) throw new ArgumentOutOfRangeException(nameof(node));
        if (collectible is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(collectible));
        byte[] child = new byte[CollectibleChildStride];
        BinaryPrimitives.WriteUInt16LittleEndian(child, (ushort)collectible);
        BinaryPrimitives.WriteUInt16LittleEndian(child.AsSpan(2), CollectibleChildTail);
        return InvestmentItemClone.AppendRow(nodes, rows.Data + node * NodeStride + NodeCollectibles, CollectibleChildStride, child);
    }

    private static List<int> Children(TagReader reader, int field, int stride)
    {
        var children = reader.Array(field, stride: stride);
        return Enumerable.Range(0, children.Count).Select(index => (int)reader.U16(children.Data + index * stride)).ToList();
    }
}
