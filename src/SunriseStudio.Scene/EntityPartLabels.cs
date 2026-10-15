using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

// TODO: EntityInitialStateReader repeats Read; make it call this class once Read reports the tags it reads.
/** Label pairs of a model config's logical parts; part filters match against them. */
public static class EntityPartLabels
{
    // FNV-1 of "gender", "male" and "female".
    public const uint GenderKey = 0x69EA00AA, Male = 0xB976E27E, Female = 0x83651CD5;
    /** Gear component filter key; 0x7FF742B13A40 shows parts labelled true. Its string is unknown. */
    public const uint GearAppliedKey = 0xF4CFC148;
    // FNV-1 of "true" and "false".
    public const uint True = 0xB602B441, False = 0x9229B22A;
    /** Armor variant key; 0x7FF742BFC6C0 passes true when the spec keeps the helmet. Its string is unknown. */
    public const uint HelmetShownKey = 0x090E39A4;
    /** Variant key whose value comes from player state. */
    public const uint RuntimeKey = 0x6ED9E9EF;
    private const uint ConfigClass = 0x80809C36, PartClass = 0x80809C2C, LabelClass = 0x80809C31;
    private const int PartStride = 24, LabelStride = 8;

    /**
     * Config resource +56 holds the logical parts; part +8 holds its {key, value} label rows.
     * @return One label set per logical part index.
     * @exception InvalidDataException The tag is not a model config or an array does not match.
     */
    public static IReadOnlyList<IReadOnlySet<(uint Key, uint Value)>> Read(PackageCatalog catalog, uint configTag)
    {
        if (!catalog.TryGetEntry(configTag, out var entry) || entry.Reference != ConfigClass)
            throw new InvalidDataException($"Tag 0x{configTag:X8} is not a model config.");
        var config = new TagData(catalog.Read(configTag));
        if (config.I64(0x18) == 0) throw new InvalidDataException($"Model config 0x{configTag:X8} has no resource.");
        int resource = config.Relative(0x18);
        return config.Rows(resource + 56, PartClass, PartStride)
            .Select(part => (IReadOnlySet<(uint Key, uint Value)>)config.Rows(part + 8, LabelClass, LabelStride)
                .Select(row => (config.U32(row), config.U32(row + 4))).ToHashSet())
            .ToArray();
    }
}
