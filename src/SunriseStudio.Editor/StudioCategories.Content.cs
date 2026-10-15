using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Interface and Audio categories; rows split by type from the entry fields alone. */
public static partial class StudioCategories
{
    /** Category keys; creation types and shortcuts look them up. */
    internal const string CuiName = "CUI", AudioName = "Audio";

    static partial void AddContentCategories(List<StudioCategory> categories)
    {
        categories.Add(new(CuiName, "Interface", ContentGroup, StudioIcon.Frame,
            "Game screens and their widget resources", ["sunrise.cui", "sunrise.related"]));
        categories.Add(new(AudioName, AudioName, ContentGroup, StudioIcon.Play,
            "Sound events, banks and streams", ["sunrise.audio", "sunrise.related"]));
    }

    // Records the Audio view cannot open; their rows sort after the ones it can.
    private static readonly HashSet<string> ListOnlyKinds = new(StringComparer.Ordinal)
        { "Audio / Event timing", "Audio / Event shared" };

    // The asset kind decides the category; runs on every row, so cached lookups only.
    static partial void ClassifyContent(PackageEntry entry, StudioNameIndex names, ref StudioCategoryMatch? match)
    {
        string category = AssetCategories.SplitKind(StudioWorkspace.AssetKind(entry)).Category;
        if (category is not (CuiName or AudioName)) return;
        match = new(ItemKinds.GetOrAdd((category, ContentType(entry)), key =>
            key.Type.Length == 0 ? key.Category : $"{key.Category} / {key.Type}"));
    }

    private static string ContentType(PackageEntry entry) => AssetCategories.IsStructured(entry) ? entry.Reference switch
    {
        0x8080496A => "Screen",
        0x80804825 => "Resource",
        0x80809802 => "Event",
        0x80808E68 => "Event timing",
        0x80808E69 => "Event shared",
        _ => ""
    } : (entry.Type, entry.SubType) switch
    {
        (26, 4) => "Init bank",
        (26, 5) => "Bank",
        (26, 6) => "Stream",
        _ => ""
    };
}
