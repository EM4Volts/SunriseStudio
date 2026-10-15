using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/** Text fields are investment string-bank pairs; the caller resolves them with its string source. */
public sealed record SandboxPerkDisplay(int Index, uint Hash, uint NameBank, uint NameHash,
    uint DescriptionBank, uint DescriptionHash, ushort IconIndex)
{
    /** Row +4; the runtime name map turns it into the perk's gameplay program tag. */
    public uint GameplayHash { get; init; }
}

/** Each sandbox perk row points at a display row that carries its authored name and description. */
public static class SandboxPerkDisplays
{
    public const uint Tag = 0x81613D28, TableClass = 0x80805C97, RowClass = 0x80805C9D, DisplayClass = 0x80805C9F;
    // Perk rows are 24 bytes; display rows are 28 bytes.
    private const int RowStride = 24, DisplayStride = 28;

    /**
     * Reads every sandbox perk with its display strings.
     * @param catalog Installation that holds the perk table.
     * @param tag Perk table tag.
     * @return One entry per perk index; empty when the table is absent.
     */
    public static IReadOnlyList<SandboxPerkDisplay> Read(PackageCatalog catalog, uint tag = Tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry)) return [];
        if (entry.Reference != TableClass) throw new InvalidDataException("Sandbox perk table has an unexpected class.");
        var data = new TagData(catalog.Read(tag));
        var rows = data.Rows(8, RowClass, RowStride);
        // A table whose rows point at no display row carries no display array.
        var displays = rows.Any(row => data.I64(row + 8) != 0) ? data.Rows(24, DisplayClass, DisplayStride) : [];
        int first = displays.Length == 0 ? 0 : displays[0];
        var result = new SandboxPerkDisplay[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            int row = rows[i];
            uint hash = data.U32(row), gameplay = data.U32(row + 4);
            if (data.I64(row + 8) == 0)
            {
                result[i] = new(i, hash, 0, 0x811C9DC5, 0, 0x811C9DC5, ushort.MaxValue) { GameplayHash = gameplay };
                continue;
            }
            int display = data.Relative(row + 8);
            if (display < first || (display - first) % DisplayStride != 0 || (display - first) / DisplayStride >= displays.Length)
                throw new InvalidDataException($"Sandbox perk {i} points outside its display rows.");
            result[i] = new(i, hash, data.U32(display), data.U32(display + 4), data.U32(display + 8),
                data.U32(display + 12), data.U16(display + 16)) { GameplayHash = gameplay };
        }
        return result;
    }
}
