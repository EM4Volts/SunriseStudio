using System.Buffers.Binary;

namespace SunriseStudio.Packages;

/** A class-0x80809EF9 manifest: the entries a root tag loads with it, grouped by package. */
public sealed class PackageManifest
{
    public const uint ClassId = 0x80809EF9;
    // The group array and its 40-byte rows; offsets are relative to the field that holds them.
    private const int GroupCountField = 16, GroupArrayField = 24, KeyCountField = 32, KeyArrayField = 40;
    private const int GroupSize = 40, ArrayHeaderSize = 16;
    // Group row: package id, bitmap word count and offset, entry list count and offset.
    private const int BitmapCountField = 8, BitmapField = 16, ListCountField = 24, ListField = 32;
    // Element classes the game writes in array headers: bitmap words and u16 entry lists.
    private const uint BitmapClass = 0x8080000B, EntryListClass = 0x8080000A;
    private const uint ArrayMarker = 0x80809FBD, GroupClass = 0x80809EFB;

    /** One package's listed entries, from its bitmap and its entry list. */
    public sealed record Group(ushort PackageId, IReadOnlySet<int> Entries);

    public IReadOnlyList<Group> Groups { get; }

    private PackageManifest(IReadOnlyList<Group> groups) => Groups = groups;

    /** @return Whether a group lists entry `tag`. */
    public bool Lists(uint tag)
    {
        var (package, entry) = Split(tag);
        return Groups.Any(group => group.PackageId == package && group.Entries.Contains(entry));
    }

    /**
     * Reads every group; each offset must land inside the data.
     * @throws InvalidDataException when an offset or a count leaves the data.
     */
    public static PackageManifest Read(byte[] data)
    {
        if (data.Length < KeyArrayField + 8) throw new InvalidDataException("The manifest is too short.");
        long count = I64(data, GroupCountField);
        if (count == 0) return new([]);
        int array = Target(data, GroupArrayField, ArrayHeaderSize);
        if (count < 0 || count > (data.Length - array - ArrayHeaderSize) / GroupSize)
            throw new InvalidDataException("The manifest group array is malformed.");
        RequireArrayHeader(data, array, count, GroupClass);
        var groups = new List<Group>();
        for (int g = 0; g < count; g++)
        {
            int row = array + ArrayHeaderSize + g * GroupSize;
            var entries = new SortedSet<int>();
            uint words = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(row + BitmapCountField));
            if (I64(data, row + BitmapField) != 0)
            {
                int header = Target(data, row + BitmapField, ArrayHeaderSize + words * 4L);
                RequireArrayHeader(data, header, words, BitmapClass);
                int bitmap = header + ArrayHeaderSize;
                for (int w = 0; w < words; w++)
                {
                    uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(bitmap + 4 * w));
                    for (int bit = 0; bit < 32; bit++) if ((bits & (1u << bit)) != 0) entries.Add(w * 32 + bit);
                }
            }
            else if (words != 0) throw new InvalidDataException("A manifest bitmap has no data pointer.");
            long listed = I64(data, row + ListCountField);
            if (listed < 0 || listed > PackageDependencyTable.MaximumEntry + 1)
                throw new InvalidDataException("A manifest entry count is invalid.");
            if (I64(data, row + ListField) != 0)
            {
                int header = Target(data, row + ListField, ArrayHeaderSize + listed * 2);
                RequireArrayHeader(data, header, listed, EntryListClass);
                int list = header + ArrayHeaderSize;
                for (int i = 0; i < listed; i++)
                    entries.Add(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(list + 2 * i)) & PackageDependencyTable.MaximumEntry);
            }
            else if (listed != 0) throw new InvalidDataException("A manifest entry list has no data pointer.");
            groups.Add(new(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(row)), entries));
        }
        return new(groups);
    }

    /**
     * Adds a group for a package the manifest does not list yet, kept in package order.
     * Every offset that crosses the new row is moved; the entry list is appended at the end.
     * @return The new manifest bytes.
     */
    public static byte[] AddGroup(byte[] data, ushort packageId, IEnumerable<int> entries)
    {
        var manifest = Read(data);
        if (manifest.Groups.Any(group => group.PackageId == packageId))
            throw new NotSupportedException($"The manifest already lists package {packageId:X4}.");
        ushort[] list = entries.Distinct().Order().Select(entry => checked((ushort)entry)).ToArray();
        if (list.Length == 0 || list[^1] > PackageDependencyTable.MaximumEntry)
            throw new ArgumentOutOfRangeException(nameof(entries), "A manifest group needs entries from 0 to 8191.");
        long count = I64(data, GroupCountField);
        int array = count == 0 ? -1 : Target(data, GroupArrayField, ArrayHeaderSize);
        if (array < 0) throw new NotSupportedException("A manifest with no groups has no group array to grow.");
        int index = manifest.Groups.TakeWhile(group => group.PackageId < packageId).Count();
        int insert = array + ArrayHeaderSize + index * GroupSize;

        // Every offset field the reader follows, as (field, target) before the insert.
        var fields = new List<(int Field, int Target)> { (GroupArrayField, array) };
        if (I64(data, KeyCountField) != 0 && I64(data, KeyArrayField) != 0)
            fields.Add((KeyArrayField, KeyArrayField + (int)I64(data, KeyArrayField)));
        for (int g = 0; g < count; g++)
        {
            int row = array + ArrayHeaderSize + g * GroupSize;
            foreach (int field in new[] { row + BitmapField, row + ListField })
                if (I64(data, field) != 0) fields.Add((field, field + (int)I64(data, field)));
        }
        int Shift(int offset) => offset >= insert ? offset + GroupSize : offset;

        int end = Align(data.Length + GroupSize);
        int listHeader = end + ArrayHeaderSize;
        byte[] result = new byte[listHeader + ArrayHeaderSize + list.Length * 2];
        data.AsSpan(0, insert).CopyTo(result);
        data.AsSpan(insert).CopyTo(result.AsSpan(insert + GroupSize));
        foreach (var (field, target) in fields)
            WriteI64(result, Shift(field), Shift(target) - Shift(field));

        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(insert), packageId);
        WriteI64(result, insert + ListCountField, list.Length);
        WriteI64(result, insert + ListField, listHeader - (insert + ListField));
        // The type marker is four bytes before the entry-list header.
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(listHeader - 4), ArrayMarker);
        WriteI64(result, listHeader, list.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(listHeader + 8), EntryListClass);
        for (int i = 0; i < list.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(listHeader + ArrayHeaderSize + 2 * i), list[i]);

        WriteI64(result, GroupCountField, count + 1);
        WriteI64(result, array, count + 1);
        WriteI64(result, 0, result.Length);
        return result;
    }

    /** Merged groups keep every old entry while their bitmap and list become one typed list. */
    public static byte[] AddEntries(byte[] data, ushort packageId, IEnumerable<int> entries)
    {
        var manifest = Read(data);
        int index = manifest.Groups.ToList().FindIndex(group => group.PackageId == packageId);
        if (index < 0) return AddGroup(data, packageId, entries);
        int[] requested = entries.Distinct().ToArray();
        if (requested.Any(entry => entry is < 0 or > PackageDependencyTable.MaximumEntry))
            throw new ArgumentOutOfRangeException(nameof(entries), "Manifest entries must fit thirteen bits.");
        var group = manifest.Groups[index];
        int[] combined = group.Entries.Concat(requested).Distinct().Order().ToArray();
        if (combined.Length == group.Entries.Count) return data.ToArray();
        int row = Target(data, GroupArrayField, ArrayHeaderSize) + ArrayHeaderSize + index * GroupSize;
        int header = Align(checked(data.Length + 4));
        byte[] result = new byte[checked(header + ArrayHeaderSize + combined.Length * 2)];
        data.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(row + BitmapCountField), 0);
        WriteI64(result, row + BitmapField, 0);
        WriteI64(result, row + ListCountField, combined.Length);
        WriteI64(result, row + ListField, header - row - ListField);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(header - 4), ArrayMarker);
        WriteI64(result, header, combined.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(header + 8), EntryListClass);
        for (int i = 0; i < combined.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(header + ArrayHeaderSize + i * 2), checked((ushort)combined[i]));
        WriteI64(result, 0, result.Length);
        return result;
    }

    private static (ushort Package, int Entry) Split(uint tag) =>
        ((ushort)((tag - 0x80800000U) >> 13), (int)((tag - 0x80800000U) & PackageDependencyTable.MaximumEntry));

    /** Native arrays require their marker, repeated count and element class to agree. */
    private static void RequireArrayHeader(byte[] data, int header, long count, uint type)
    {
        if (header < 4 || header > data.Length - ArrayHeaderSize
            || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(header - 4)) != ArrayMarker
            || I64(data, header) != count
            || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(header + 8)) != type)
            throw new InvalidDataException("A manifest typed array header is malformed.");
    }

    /** @return The offset a relative field points at, after checking `size` bytes fit there. */
    private static int Target(byte[] data, int field, long size)
    {
        long target = field + I64(data, field);
        if (size < 0 || target < 0 || target > data.Length - size)
            throw new InvalidDataException("A manifest offset leaves the data.");
        return (int)target;
    }

    private static int Align(int value) => (value + 7) & ~7;
    private static long I64(byte[] data, int offset) => BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset));
    private static void WriteI64(byte[] data, int offset, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset), value);
}
