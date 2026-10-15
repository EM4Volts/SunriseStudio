using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** The three class bodies; the Guardian page assembles face, armor and weapon on them. */
public static partial class StudioCategories
{
    public const string GuardiansName = "Guardians";
    public const string GuardianPageId = "sunrise.guardian";

    static partial void AddGuardianCategories(List<StudioCategory> categories) =>
        categories.Add(new(GuardiansName, GuardiansName, WorldGroup, StudioIcon.Player,
            "Player bodies with face, armor and weapon",
            [GuardianPageId, "sunrise.scene", "sunrise.materials", "sunrise.related"], ModelPane: true,
            OpenContext: _ => new AssetOpenContext { Guardian = GuardianLoadout.Default }));

    static partial void ClassifyGuardian(PackageEntry entry, StudioNameIndex names, ref StudioCategoryMatch? match)
    {
        if (entry.Reference != GuardianClasses.BodyClass || GuardianClasses.ForBody(entry.TagHash) is not { } body) return;
        match = new($"{GuardiansName} / {body.Name}", "guardian player");
    }
}
