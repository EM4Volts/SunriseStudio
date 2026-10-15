using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SunriseStudio.Packages;

/** Coarse browser categories and filter tags for package entries, from package type and tag class. */
public static partial class AssetCategories
{
    public static IReadOnlyList<string> All { get; } =
    [
        "Map", "Bubble", "Activity", "Activity / Config link", "Activity slot", "Static mesh",
        "Entity / Definition", "Entity component", "Entity model", "Terrain", "Texture", "Material", "Shader",
        "Buffer", "Animation", "Skeleton", "Audio", "Havok", "Strings", "Light", "Effect", "Script",
        "Renderer", "Video", "Font", "Perk", "Icon", "Network", "Investment", "CUI", "Other"
    ];

    // Structured tags carry their class in Reference; other package types carry a paired entry there.
    private static readonly Dictionary<uint, string> ClassCategories = new()
    {
        [0x808091DE] = "Map", [0x80809962] = "Map", [0x808091E0] = "Map", [0x80808A54] = "Map",
        [0x808099D6] = "Map", [0x8080966D] = "Map", [0x80806EF4] = "Map", [0x80807268] = "Map",
        [0x80806F95] = "Map", [0x80809162] = "Map", [0x80806E68] = "Map",
        [0x80807DAE] = "Bubble",
        [0x80809994] = "Activity", [0x8080925B] = "Activity", [0x8080925E] = "Activity",
        [0x80809462] = "Activity", [0x80809468] = "Activity",
        [0x808071A7] = "Static mesh", [0x80807194] = "Static mesh",
        [0x80809C0F] = "Entity / Definition", [0x80809C36] = "Entity component",
        [0x808073A5] = "Entity model", [0x80809B14] = "Activity / Config link",
        [0x8080714F] = "Terrain",
        [0x808071E8] = "Material",
        [0x80808F49] = "Animation", [0x808036F6] = "Animation",
        [0x8080727A] = "Skeleton",
        [0x80809802] = "Audio",
        [0x80809A88] = "Strings", [0x80809A8A] = "Strings",
        [0x8080713A] = "Light", [0x80807140] = "Light",
        [0x80806E28] = "Effect", [0x80806E2C] = "Effect", [0x80806F68] = "Effect", [0x80806CC6] = "Effect",
        [0x80800000] = "Script", [0x8080941E] = "Script",
        [0x80806CB1] = "Renderer", [0x808071F3] = "Renderer", [0x8080858D] = "Renderer",
        [0x808040B5] = "Perk",
        [0x80804A53] = "Icon", [0x80804A69] = "Icon", [0x80804A55] = "Icon",
        [0x80809BB6] = "Network", [0x80809BBB] = "Network",
        [0x80808E68] = "Audio", [0x80808E69] = "Audio",
        [0x80803465] = "Animation",
        [0x80809EBB] = "Texture", [0x808072D2] = "Texture",
        [0x8080744A] = "Entity model",
        [0x80809C54] = "Script",
        [0x80804825] = "CUI", [0x8080496A] = "CUI",
    };

    // The tables investment_globals and the investment root blob name; each class has one table.
    private static readonly HashSet<uint> InvestmentTables =
    [
        0x80802863, 0x8080287B, 0x808028A4, 0x808028BB, 0x80802943, 0x8080294D,
        0x80802951, 0x80802AE6, 0x80802AF1, 0x80802B60, 0x80802C2B, 0x80802C2E,
        0x80802C3C, 0x80802C3E, 0x80802C78, 0x80802C82, 0x80802C83, 0x80802CB6,
        0x80802E11, 0x80802E41, 0x80802E56, 0x80802E5B, 0x80802F0E, 0x80802F47,
        0x80803041, 0x8080311B, 0x80803145, 0x8080385E, 0x80803F49, 0x80804193,
        0x80804F13, 0x80805408, 0x808056CC, 0x8080573D, 0x80805751, 0x808057A0,
        0x808057D3, 0x80805854, 0x808058DE, 0x808058E8, 0x80805936, 0x80805945,
        0x80805953, 0x8080595D, 0x80805972, 0x8080599E, 0x808059A8, 0x808059B9,
        0x808059DA, 0x808059E2, 0x808059EC, 0x808059FC, 0x80805A08, 0x80805A0B,
        0x80805A1A, 0x80805A2A, 0x80805A72, 0x80805A95, 0x80805AB6, 0x80805AD9,
        0x80805AE7, 0x80805AF4, 0x80805AFF, 0x80805B0F, 0x80805B1B, 0x80805B27,
        0x80805B37, 0x80805B3F, 0x80805B52, 0x80805B5C, 0x80805B66, 0x80805B70,
        0x80805B78, 0x80805B86, 0x80805B8F, 0x80805BA2, 0x80805BDE, 0x80805BF1,
        0x80805C3C, 0x80805C8D, 0x80805C97, 0x80805CA4, 0x80805CB3, 0x80805CC5,
        0x80805CDB, 0x80805CFC, 0x80805D1D, 0x80805D3A, 0x80805DBC, 0x80805DE8,
        0x80805DF5, 0x80805E0A, 0x80805E73, 0x80805EA9, 0x80805F3C, 0x80805F59,
        0x80805F98, 0x80807533, 0x80807539, 0x8080753C, 0x80807542, 0x80807546,
        0x80807548, 0x80807553, 0x80807561, 0x80807567, 0x80807571, 0x80807574,
        0x80807576, 0x8080757C, 0x8080757E, 0x80807581, 0x80807583, 0x8080759B,
        0x808075A1, 0x808075A4, 0x808075A8, 0x808075B0, 0x808075B5, 0x808075BA,
        0x808075BE, 0x808075C0, 0x808075C6, 0x808075CA, 0x808075CE, 0x8080769F,
        0x808076D5, 0x808076D9, 0x808076F0, 0x808076FE, 0x8080775B, 0x80807768,
        0x80807773, 0x80807797, 0x808077D1, 0x808077EE, 0x808077F6, 0x8080784A,
        0x80807879, 0x8080789C, 0x808078A6, 0x8080792F, 0x80807981, 0x808079F8,
        0x80807A02, 0x80807A0E, 0x80807A18, 0x80807A2B, 0x80807A30, 0x80807A5F,
        0x80807A66, 0x80807A6B, 0x80807A78, 0x80807AAA, 0x80807AAF, 0x80807AB5,
        0x80807AC3, 0x80807ACE, 0x80807AE0, 0x80807B62, 0x80807BBB, 0x80807BC2,
        0x80807BD9, 0x80807BE4, 0x80807C0B, 0x80807C16, 0x80807C2A, 0x80807C32,
        0x80807C49, 0x80807C50, 0x80807C62, 0x80807C6C, 0x80807C80, 0x80807C92,
        0x80807C9B, 0x80807CA4, 0x80807CC1, 0x80807CCA, 0x80807CD9, 0x80807D05,
        0x80807D1E, 0x80807D36, 0x80807D49, 0x80807D6B,
    ];

    private static readonly ConcurrentDictionary<(int Type, int SubType, uint Class, ushort Package), (string, string[])> Cache = new();
    private static readonly ConcurrentDictionary<string, string[]> PackageTags = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, (string, string)> KindParts = new(StringComparer.Ordinal);
    // A kind is "Category / Type"; a kind with no separator has no type.
    private const string KindSeparator = " / ";

    /** Splits a kind such as "Entity / Prop" into its category and type; the type is empty when absent. */
    public static (string Category, string Type) SplitKind(string kind) => KindParts.GetOrAdd(kind, value =>
    {
        int separator = value.IndexOf(KindSeparator, StringComparison.Ordinal);
        return separator < 0 ? (value, "") : (value[..separator], value[(separator + KindSeparator.Length)..]);
    });

    [GeneratedRegex(@"^w64_|_[0-9a-f]{4}_\d+$|_unp\d+_\d+$")]
    private static partial Regex PackageAffixes();

    /** True for package types whose Reference field is a tag class. */
    public static bool IsStructured(PackageEntry entry) => entry.Type is 8 or 16;

    /**
     * Category and filter tags of one entry; results for the same type, class and package are shared.
     * @param entry Catalog entry.
     * @param names Optional names that add activity, destination and map tags.
     */
    public static (string Category, string[] Tags) Classify(PackageEntry entry, GameNames? names)
    {
        var classKey = IsStructured(entry) ? entry.Reference : 0;
        var (category, tags) = Cache.GetOrAdd((entry.Type, entry.SubType, classKey, entry.PackageId), _ =>
            (CategoryOf(entry), BuildTags(entry)));
        if (names?.Components is { } index && classKey is 0x80809C0F or 0x80809C36)
            return EntityClassify(entry, index, tags);
        if (names == null || entry.Reference is not (0x80809994 or 0x808091DE) || !IsStructured(entry))
            return (category, tags);
        var extra = new List<string>(tags);
        if (names.Scenario(entry.TagHash) is { } scenario)
        {
            if (scenario.DevName != null) extra.Add($"map {scenario.DevName}");
            if (scenario.Destination != null) extra.Add($"destination {scenario.Destination}");
            if (scenario.Activity != null) extra.Add($"activity {scenario.Activity}");
        }
        if (names.MapDevName(entry.TagHash) is { } dev) extra.Add($"map {dev}");
        return (category, extra.Distinct().ToArray());
    }

    /** Browser category of a package type, subtype and class. */
    public static string CategoryOf(PackageEntry entry) => (entry.Type, entry.SubType) switch
    {
        (8 or 16, _) when InvestmentTables.Contains(entry.Reference) => "Investment / Table",
        (8 or 16, _) => ClassCategories.GetValueOrDefault(entry.Reference, "Other"),
        (32 or 40, 1 or 2 or 3) or (48, _) => "Texture",
        (32 or 40, 4 or 6) => "Buffer",
        (32 or 40, 7) or (34 or 42, 1) => "Material",
        (33 or 41, _) => "Shader",
        (26, 4 or 5 or 6) => "Audio",
        (26, 7) => "Havok",
        (27, _) => "Video",
        (24, _) => "Font",
        _ => "Other"
    };

    /** A short kind label: the class label of a structured tag, else the package type's meaning. */
    public static string KindLabel(PackageEntry entry)
    {
        if (IsStructured(entry)) return ClassNames.Describe(entry.Reference);
        var part = entry.Type is >= 32 and <= 34 ? " header" : entry.Type is >= 40 and <= 42 ? " data" : "";
        return (entry.Type, entry.SubType) switch
        {
            (32 or 40, 1) => "texture 2D" + part,
            (32 or 40, 2) => "texture cube" + part,
            (32 or 40, 3) => "texture 3D" + part,
            (32 or 40, 4) => "vertex buffer" + part,
            (32 or 40, 6) => "index buffer" + part,
            (32 or 40, 7) => "constant buffer" + part,
            (48, _) => "texture large buffer",
            (33 or 41, 0) => "pixel shader" + part,
            (33 or 41, 1) => "vertex shader" + part,
            (33 or 41, 2) => "geometry shader" + part,
            (33 or 41, 6) => "compute shader" + part,
            (34 or 42, 1) => "sampler" + part,
            (26, 4) => "wwise init bank",
            (26, 5) => "wwise bank",
            (26, 6) => "wwise stream",
            (26, 7) => "havok",
            (27, _) => "video",
            (24, _) => "font",
            _ => $"type {entry.Type}/{entry.SubType}"
        };
    }

    /** "w64_edz_activities_0197_5" gives "edz_activities" and "edz". */
    public static IReadOnlyList<string> PackageStems(string packageName) =>
        PackageTags.GetOrAdd(packageName, name =>
        {
            var stem = PackageAffixes().Replace(Path.GetFileNameWithoutExtension(name), "");
            foreach (var suffix in new[] { "_activities", "_destination" })
                if (stem.EndsWith(suffix, StringComparison.Ordinal) && stem.Length > suffix.Length)
                    return [stem, stem[..^suffix.Length]];
            return [stem];
        });

    /** Entities split by kind, configs by their primary component class; activity slots get their own branch. */
    private static (string, string[]) EntityClassify(PackageEntry entry, ComponentIndex index, string[] tags)
    {
        var extra = new List<string>(tags);
        string category;
        if (entry.Reference == 0x80809C0F)
        {
            var kind = index.EntityKind(entry.TagHash) ?? "Other";
            category = $"Entity / {kind}";
            extra.Add($"kind {kind}");
            if (index.CombatantName(entry.TagHash) is { } combatant) extra.Add($"combatant {combatant}");
            if (index.ObjectType(entry.TagHash) is { } objectType) extra.Add($"object type {objectType}");
        }
        else
        {
            var primary = index.PrimaryClass(entry.TagHash);
            var label = index.ComponentLabel(primary);
            category = index.IsSlotComponent(primary) ? $"Activity slot / {label}"
                : label != null ? $"Entity component / {label}" : "Entity component / Other";
            extra.Add($"component 0x{primary:X8}");
            if (label != null) extra.Add($"component {label}");
        }
        if (index.Prompt(entry.TagHash) is { } prompt) extra.Add($"prompt {prompt}");
        return (category, extra.ToArray());
    }

    private static string[] BuildTags(PackageEntry entry)
    {
        var tags = new List<string> { KindLabel(entry), $"type {entry.Type}/{entry.SubType}" };
        foreach (var stem in PackageStems(entry.PackageName)) tags.Add($"package {stem}");
        return tags.Distinct().ToArray();
    }
}
