using SunriseStudio.Interchange;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** The draw-time shader inputs of a guardian's gear parts, as the viewer binds them, for the Blender export. */
public static class GuardianShaderInputs
{
    /**
     * Reads each gear part's dye bit, dye-scope constant buffers, plate and dye textures, and forced decal blend.
     * @param plan A plan the renderer has not uploaded; it still holds the composed plate atlases.
     * @return Inputs by part; parts that read no gear inputs are left out.
     */
    public static Dictionary<MeshInstance, BundleShaderInputs> Read(NativeScenePlan plan)
    {
        var result = new Dictionary<MeshInstance, BundleShaderInputs>(ReferenceEqualityComparer.Instance);
        foreach (var group in plan.Groups)
        {
            var material = group.Material;
            if (material.Gear is null && !material.ReadsGearDye) continue;
            var textures = new Dictionary<uint, uint>();
            var composed = new Dictionary<uint, SunriseStudio.Packages.TextureData>();
            var buffers = new Dictionary<uint, System.Numerics.Vector4[]>();
            if (material.Gear is { } gear)
            {
                foreach (var scope in gear.Scopes)
                {
                    buffers[scope.ConstantBufferSlot] = scope.Constants;
                    foreach (var (slot, tag) in scope.Textures)
                        if (tag != NativeGearPlating.FrameDyeTexture) textures[slot] = tag;
                }
                // A plate atlas exists only in the plan; one that failed to compose is left to the material.
                foreach (var (slot, tag) in gear.PlateTextures)
                    if (plan.Textures.TryGetValue(tag, out var atlas)) { textures[slot] = tag; composed[tag] = atlas; }
            }
            string key = $"{material.Gear?.Key}|dye {material.GearDyeBit}";
            foreach (var part in group.Parts)
                result[part.Instance] = new(key, part.Instance.Name.Split(':')[0], material.GearDyeBit, buffers, textures, composed)
                { ForcedBlend = material.Gear?.ForcedBlend };
        }
        return result;
    }
}
