using System.Buffers.Binary;
using SunriseStudio.Packages;

namespace SunriseStudio.Authoring;

/** The weapon resource owns its first-person presentation entity. */
public static class WeaponPresentationReader
{
    private const uint EntityClass = 0x80809C0F, ConfigClass = 0x80809C36;
    // The weapon resource stores a default descriptor and a typed selector array.
    private const int DefaultDescriptor = 1184, VariantArray = 1416, PresentationField = 120;
    /** The base weapon's gear key; a weapon with its own records selects its row by its own key. */
    public const uint TemplateSelector = 0x44A0B334;

    /** The first row whose key matches the selector names the owner; otherwise the default row does. */
    public static uint Read(PackageCatalog catalog, uint gameplayEntity, uint selector = TemplateSelector)
    {
        if (!catalog.TryGetEntry(gameplayEntity, out var entry) || entry.Reference != EntityClass)
            throw new InvalidDataException("The weapon's gameplay root is not an entity.");
        byte[] root = catalog.Read(gameplayEntity);
        var owners = new HashSet<uint>();
        foreach (int row in NativeRows.Rows(root, 16, 0x80809C04, 12))
        {
            uint tag = U32(root, row);
            if (!catalog.TryGetEntry(tag, out var configEntry) || configEntry.Reference != ConfigClass) continue;
            byte[] config = catalog.Read(tag);
            if (config.Length < 32) throw new InvalidDataException("A weapon component is truncated.");
            int resource = Pointer(config, 24);
            if (resource < 4 || resource > config.Length) throw new InvalidDataException("A weapon resource leaves its component.");
            if (U32(config, resource - 4) != 0x80804221) continue;
            int selected = NativeRows.Rows(config, checked(resource + VariantArray), 0x80803E72, 232)
                .FirstOrDefault(at => U32(config, at) == selector, -1);
            uint owner = U32(config, selected >= 0 ? selected + PresentationField
                : checked(resource + DefaultDescriptor + PresentationField));
            if (owner == uint.MaxValue)
                throw new NotSupportedException("The weapon names no first-person presentation entity.");
            if (!catalog.TryGetEntry(owner, out var presentation) || presentation.Reference != EntityClass)
                throw new InvalidDataException("The weapon's presentation owner is not an entity.");
            owners.Add(owner);
        }
        if (owners.Count != 1)
            throw new NotSupportedException("The weapon needs one checked first-person presentation owner.");
        return owners.Single();
    }

    private static ReadOnlySpan<byte> Slice(byte[] bytes, int at, int count) =>
        at >= 0 && at <= bytes.Length - count ? bytes.AsSpan(at, count)
            : throw new InvalidDataException("A weapon presentation field leaves its component.");
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(bytes, at, 4));
    private static long I64(byte[] bytes, int at) => BinaryPrimitives.ReadInt64LittleEndian(Slice(bytes, at, 8));
    private static int Pointer(byte[] bytes, int at) => checked((int)(at + I64(bytes, at)));
}
