using SunriseStudio.Packages;

namespace SunriseStudio.Animation;

/** Finds the neutral face pose a guardian head entity carries in its face component. */
public static class FaceRestReader
{
    private const uint EntityClass = 0x80809C0F, ComponentRowClass = 0x80809C04, ConfigClass = 0x80809C36;
    private const uint ClipClass = 0x80808F49;
    /** Resource class of the face component. */
    public const uint FaceResourceClass = 0x80803637;
    // Resource +0xE0 holds the expression library clip, +0xE4 the override clip with the neutral face.
    private const int RestClipOffset = 0xE4;

    /**
     * The override clip of the entity's face component.
     * @return 0 when the entity has no face component.
     */
    public static uint Read(PackageCatalog catalog, uint entityTag)
    {
        if (Class(catalog, entityTag) != EntityClass)
            throw new InvalidDataException($"0x{entityTag:X8} is not an entity.");
        var entity = new TagData(catalog.Read(entityTag));
        uint found = 0;
        foreach (int row in entity.Rows(0x10, ComponentRowClass, 12))
        {
            uint configTag = entity.U32(row);
            if (Class(catalog, configTag) != ConfigClass) continue;
            var config = new TagData(catalog.Read(configTag));
            int resource = config.Pointer(0x18);
            if (config.U32(resource - 4) != FaceResourceClass) continue;
            if (found != 0) throw new InvalidDataException($"Entity 0x{entityTag:X8} has more than one face component.");
            found = config.U32(resource + RestClipOffset);
            if (Class(catalog, found) != ClipClass)
                throw new InvalidDataException($"Face component 0x{configTag:X8} names 0x{found:X8}, which is not a clip.");
        }
        return found;
    }

    private static uint Class(PackageCatalog catalog, uint tag) => catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0;
}
