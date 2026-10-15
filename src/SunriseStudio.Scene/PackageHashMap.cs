using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/** Hash -> value maps such as 0x80EC3F60 and 0x80EC3F61: 8-byte {u32 hash, u32 value} rows at +8. */
public static class PackageHashMap
{
    /** The first row of a hash wins. Throws InvalidDataException when the map is missing or malformed. */
    public static Dictionary<uint, uint> Read(PackageCatalog catalog, uint tag, uint rowClass)
    {
        var map = ReadStructured(catalog, tag);
        int[] rows = map.Rows(8, rowClass, 8);
        var result = new Dictionary<uint, uint>(rows.Length);
        foreach (int row in rows) result.TryAdd(map.U32(row), map.U32(row + 4));
        return result;
    }

    /** A structured table; it starts with its own u64 size. */
    internal static TagData ReadStructured(PackageCatalog catalog, uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Type is not (8 or 16))
            throw new InvalidDataException($"Table 0x{tag:X8} is missing or not structured.");
        var data = new TagData(catalog.Read(tag));
        if (data.Length < 8 || data.U64(0) != (ulong)data.Length) throw new InvalidDataException($"Table 0x{tag:X8} has a bad size header.");
        return data;
    }
}
