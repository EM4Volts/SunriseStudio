using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Interchange;

/** Particle effects of a bundle, baked as a preview beside the editable emitter constants. */
public static partial class AssetBundle
{
    public const string EffectsPath = "particles/effects.json", EffectsFolder = "particles";

    /** Writes the effects file and adds its notices; a scene without effect markers writes none. */
    private static void WriteEffects(PackageCatalog catalog, SceneDocument scene, string root, List<BundleNotice> notices,
        CancellationToken token, Action<string>? progress)
    {
        var set = EffectExport.Write(catalog, scene, root, EffectsPath, EffectsFolder, token: token, progress: progress);
        if (set is not null) notices.AddRange(set.Notices);
    }
}
