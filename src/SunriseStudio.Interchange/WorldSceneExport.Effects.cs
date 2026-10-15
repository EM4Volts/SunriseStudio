using System.Diagnostics;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Interchange;

/** Particle effects of a world scene, baked beside the manifest; the manifest does not list them. */
public static partial class WorldSceneExport
{
    public const string EffectsName = "world.effects.json", EffectsFolder = "effects";

    /** Writes the effects file and adds its notices; a scene without effect markers writes none. */
    private static void WriteEffects(PackageCatalog catalog, SceneDocument scene, string root, List<BundleNotice> notices,
        Dictionary<string, double> phases, CancellationToken token, Action<string>? progress)
    {
        var clock = Stopwatch.StartNew();
        var set = EffectExport.Write(catalog, scene, root, EffectsName, EffectsFolder, token: token, progress: progress);
        if (set is not null) notices.AddRange(set.Notices);
        phases["effects"] = clock.Elapsed.TotalSeconds;
    }
}
