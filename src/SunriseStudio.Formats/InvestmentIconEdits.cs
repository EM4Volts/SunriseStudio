using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** Custom icons keep shared source bytes and retarget only the owning item row. */
public static class InvestmentIconEdits
{
    /** The primary bitmap tag is the only changed field in the icon container. */
    public static byte[] SetPrimaryBitmap(ReadOnlyMemory<byte> container, uint bitmapTag)
    {
        if (container.Length < 128) throw new InvalidDataException("The icon container is truncated.");
        byte[] result = container.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20), bitmapTag);
        return result;
    }

    /** A static icon must have exactly one bitmap group containing one texture frame. */
    public static byte[] SetStaticTexture(ReadOnlyMemory<byte> bitmap, uint textureTag)
    {
        if (CuiBitmap.ReadGroups(bitmap).Count != 1)
            throw new NotSupportedException("Custom icons need one static bitmap frame.");
        return SetGroupTextures(bitmap, [textureTag]);
    }

    /** Each bitmap group holds one static frame; group `i` receives `textureTags[i]`. */
    public static byte[] SetGroupTextures(ReadOnlyMemory<byte> bitmap, IReadOnlyList<uint> textureTags)
    {
        var groups = CuiBitmap.ReadGroups(bitmap);
        if (groups.Count != textureTags.Count || groups.Any(group => group.Count != 1))
            throw new NotSupportedException("Custom icons need one static frame in each bitmap group.");
        var reader = new TagReader(bitmap);
        int descriptor = reader.Relative(16);
        uint groupClass = reader.U32(descriptor - 4) == 0x80804A67 ? 0x80804A6C : 0x80804A6A;
        uint frameClass = groupClass == 0x80804A6C ? 0x80804A6F : 0x80804A6E;
        var groupRows = reader.Array(descriptor, groupClass, 16);
        byte[] result = bitmap.ToArray();
        for (int group = 0; group < groups.Count; group++)
        {
            var frames = reader.Array(groupRows.Data + group * 16, frameClass, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(frames.Data), textureTags[group]);
        }
        return result;
    }

    /** Hash lookups and direct indices must select the same unique item-owned icon row. */
    public static (byte[] Table, ushort Index) SetItemRow(ReadOnlyMemory<byte> icons,
        uint itemHash, uint containerTag, int installedCount)
    {
        var matches = InvestmentItemClone.ReadIndexRows(icons).Where(row => row.Hash == itemHash).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("The item has duplicate icon hash rows.");
        if (matches.Length == 0)
        {
            var added = InvestmentItemClone.AppendIndexRow(icons, itemHash, containerTag);
            return (added.Table, checked((ushort)added.Index));
        }
        int index = matches[0].Index;
        if (index < installedCount || index >= InvestmentItemClone.MaximumIndexRows)
            throw new InvalidDataException("A shared installed icon row cannot be retargeted.");
        var rows = new TagReader(icons).Array(8, stride: InvestmentItemClone.IndexStride);
        byte[] result = icons.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(rows.Data
            + index * InvestmentItemClone.IndexStride + InvestmentItemClone.IndexTag), containerTag);
        return (result, checked((ushort)index));
    }

    /** Every collectible naming this item keeps the same icon index as its item display. */
    public static byte[] SetCollectibleIcons(ReadOnlyMemory<byte> collectibles,
        ReadOnlyMemory<byte> displays, int itemIndex, ushort iconIndex)
    {
        var source = new TagReader(collectibles);
        var sourceRows = source.Array(8, stride: InvestmentCollections.CollectibleStride);
        var target = new TagReader(displays);
        var targetRows = target.Array(8, stride: InvestmentCollections.DisplayStride);
        if (sourceRows.Count != targetRows.Count)
            throw new InvalidDataException("Collectible and display rows do not pair.");
        byte[] result = displays.ToArray();
        for (int index = 0; index < sourceRows.Count; index++)
        {
            int row = sourceRows.Data + index * InvestmentCollections.CollectibleStride;
            if (source.U16(row + InvestmentCollections.CollectibleItem) != itemIndex) continue;
            int display = targetRows.Data + index * InvestmentCollections.DisplayStride;
            if (source.U32(row + InvestmentCollections.CollectibleHash)
                != target.U32(display + InvestmentCollections.DisplayHash))
                throw new InvalidDataException("The collectible display belongs to another item.");
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(display
                + InvestmentCollections.DisplayIcon), iconIndex);
        }
        return result;
    }
}
