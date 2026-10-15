using System.Numerics;

namespace SunriseStudio.Rendering;

/**
 * One sun shadow cascade: the main-view slice it covers and the light view that renders it.
 * Distances are along the main view. The cascade fades out from Split to BlendFar.
 */
public sealed record NativeShadowCascade(int Index, float Near, float Split, float BlendFar,
    Matrix4x4 LightView, Matrix4x4 Projection, Vector3 Extents, float Backward, int Resolution)
{
    public Matrix4x4 ViewProjection => LightView * Projection;
}

/** The sun cascade setup of 0x7FF742EE40A0: splits from 0x7FF742F00020, fits from 0x7FF742EEA500. */
public static class NativeShadowCascades
{
    // dword_7FF743C96CA0 and the four cascade_shadow_view names.
    public const int Count = 4;
    // dword_7FF743C976A8: the shadow distance, the far end of the last cascade.
    public const float Distance = 300;
    // dword_7FF743C97564: the weight of the logarithmic split against the uniform one.
    public const float Lambda = 0.6f;
    // dword_7FF743C97560: each cascade but the last extends past its split by this share of its depth.
    public const float BlendFraction = 0.05f;
    // The last cascade fades out over its last tenth; 0x7FF742F0AC20 uses the distance times 0.9.
    public const float LastFadeStart = Distance * 0.9f;
    // Cascade fitter fields +0x3C10 and +0x3C1C, set by 0x7FF742EE4410: the caster height above the camera.
    public const float CasterHeight = 240, FallbackBackward = 64;
    // 0x7FF742EEA500 drops a backward distance above this.
    public const float MaximumBackward = 10000;
    // The shadow_quality setting defaults to 2 (0x7FF7420CAA70); it selects row 2 of the profile table 0x7FF7439038B0.
    public const int ShadowQuality = 2;
    // 0x7FF742F06490: row 2 keeps 1536 for cascades 0 and 1; cascades 2 and 3 take word_7FF74391DAF0 entries 0 and 1.
    public static IReadOnlyList<int> Resolutions { get; } = [1536, 1536, 1280, 1024];
    // 0x7FF742EE43A0 sets the stable-fit and texel-snap masks (frame +0x37F30, +0x37F34) and the F0CB10 mask to 8.
    // SDSM is on for all four cascades, so only cascade 3 gets the square fit and the snap.
    public const int StableMask = 8;

    /**
     * Split distances of 0x7FF742F00020 with the frame +0x37F39 flag that 0x7FF742EE43A0 sets: the first
     * three blend logarithmic and uniform splits over half the shadow distance; the last ends at the distance.
     * @param cameraNear The main view's near plane; dword_7FF743C97598 is -1, which selects it.
     * @return Near, split and blend-far distance per cascade.
     */
    public static (float Near, float Split, float BlendFar)[] Splits(float cameraNear)
    {
        if (!(cameraNear > 0)) throw new ArgumentOutOfRangeException(nameof(cameraNear));
        if (cameraNear >= Distance) cameraNear = Distance - 0.001f;
        var result = new (float, float, float)[Count];
        // The first near plane steps 4 units in front of the camera near, clamped to it, times 1.002.
        float near = MathF.Max(cameraNear - 4, cameraNear) * 1.002f;
        const float spread = Distance * 0.5f;
        for (int i = 0; i < Count; i++)
        {
            float t = (i + 1) / (float)(Count - 1);
            float uniform = (spread - cameraNear) * t + cameraNear;
            float split = i == Count - 1 ? Distance
                : (MathF.Pow(spread / cameraNear, t) * cameraNear - uniform) * Lambda + uniform;
            float blend = i == Count - 1 ? split : (split - near) * BlendFraction + split;
            result[i] = (near, split, blend);
            near = split;
        }
        return result;
    }

    /**
     * Fits every cascade to its slice of the main view, as 0x7FF742EEA500 does.
     * @param view Main world-to-view matrix, row-vector form.
     * @param projection Main projection with forward depth.
     * @param toLight Unit direction toward the light, frame +0x37020.
     */
    public static NativeShadowCascade[] Fit(Matrix4x4 view, Matrix4x4 projection, float cameraNear, Vector3 cameraPosition,
        Vector3 toLight)
    {
        float length = toLight.Length();
        if (!(length > 1e-6f)) throw new ArgumentException("The light direction is zero.", nameof(toLight));
        var light = toLight / length;
        // 0x7FF742EE6830: world X is the up vector unless the light is almost along it, then world Y.
        var up = MathF.Abs(light.X) >= 0.999f ? Vector3.UnitY : Vector3.UnitX;
        // The light basis of 0x7FF7427F9970: Z toward the light, X = up x Z, Y = Z x X.
        var rotation = Matrix4x4.CreateLookAt(Vector3.Zero, -light, up);
        if (!Matrix4x4.Invert(view * projection, out var inverseViewProjection))
            throw new ArgumentException("The main view is singular.");
        if (!Matrix4x4.Invert(view, out var inverseView)) throw new ArgumentException("The main view is singular.");
        var cameraZ = new Vector3(inverseView.M31, inverseView.M32, inverseView.M33);
        var cascades = new NativeShadowCascade[Count];
        var splits = Splits(cameraNear);
        for (int i = 0; i < Count; i++)
        {
            var (near, split, blendFar) = splits[i];
            // The eight slice corners from near to the blend far plane, in light space and in view space.
            var lightMin = new Vector3(float.PositiveInfinity); var lightMax = new Vector3(float.NegativeInfinity);
            var viewMin = new Vector3(float.PositiveInfinity); var viewMax = new Vector3(float.NegativeInfinity);
            foreach (float distance in new[] { near, blendFar })
            {
                float depth = DepthAt(projection, distance);
                for (int corner = 0; corner < 4; corner++)
                {
                    var clip = new Vector4((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, depth, 1);
                    var world4 = Vector4.Transform(clip, inverseViewProjection);
                    var world = new Vector3(world4.X, world4.Y, world4.Z) / world4.W;
                    var inLight = Vector3.Transform(world, rotation);
                    lightMin = Vector3.Min(lightMin, inLight); lightMax = Vector3.Max(lightMax, inLight);
                    var inView = Vector3.Transform(world, view);
                    viewMin = Vector3.Min(viewMin, inView); viewMax = Vector3.Max(viewMax, inView);
                }
            }
            int resolution = Resolutions[i];
            bool stable = (StableMask & (1 << i)) != 0;
            if (stable)
            {
                // A stable cascade is square: X and Y grow to the slice's view-space diagonal.
                float diagonal = (viewMax - viewMin).Length();
                var grow = new Vector3((diagonal - (lightMax.X - lightMin.X)) * 0.5f, (diagonal - (lightMax.Y - lightMin.Y)) * 0.5f, 0);
                lightMin -= grow; lightMax += grow;
            }
            var center = (lightMin + lightMax) * 0.5f;
            var extents = lightMax - lightMin;
            if (stable)
            {
                // Texel snapping keeps the cascade on whole texels while the camera moves.
                center.X = MathF.Round(center.X * resolution / extents.X) * extents.X / resolution;
                center.Y = MathF.Round(center.Y * resolution / extents.Y) * extents.Y / resolution;
            }
            float backward = Backward(rotation, center, light, cameraPosition, cameraZ);
            backward = MathF.Max(extents.Z * 0.5f, backward);
            var origin = Vector3.Transform(new Vector3(center.X, center.Y, center.Z + backward), Transpose3(rotation));
            var lightView = Matrix4x4.CreateLookAt(origin, origin - light, up);
            float range = extents.Z * 0.5f + backward;
            // The projection of 0x7FF742E8DB20: orthographic, depth 0 at the origin and 1 at the far face.
            var orthographic = Matrix4x4.CreateOrthographicOffCenter(-extents.X * 0.5f, extents.X * 0.5f,
                -extents.Y * 0.5f, extents.Y * 0.5f, 0, range);
            cascades[i] = new(i, near, split, blendFar, lightView, orthographic, extents with { Z = range }, backward, resolution);
        }
        return cascades;
    }

    /**
     * The backward distance: from the slice centre along the light to the plane CasterHeight above the
     * camera, or above zero when the camera is lower. Casters up to that height stay inside the cascade.
     */
    private static float Backward(Matrix4x4 rotation, Vector3 center, Vector3 light, Vector3 camera, Vector3 cameraZ)
    {
        float height = CasterHeight + MathF.Max(camera.Z, 0);
        var world = Vector3.Transform(center, Transpose3(rotation));
        float distance = MathF.Abs(light.Z) <= 1e-4f ? float.PositiveInfinity : (height - world.Z) / light.Z;
        if (float.IsFinite(distance) && distance <= MaximumBackward) return distance;
        // The fallback compares the light with the camera's absolute Z axis.
        return Vector3.Dot(light, Vector3.Abs(cameraZ)) < 0 ? 0 : FallbackBackward;
    }

    /** Forward depth of a point at this view distance in front of the camera. */
    private static float DepthAt(Matrix4x4 projection, float distance)
    {
        var clip = Vector4.Transform(new Vector4(0, 0, -distance, 1), projection);
        return clip.Z / clip.W;
    }

    private static Matrix4x4 Transpose3(Matrix4x4 rotation)
    {
        var t = Matrix4x4.Transpose(rotation);
        t.M14 = t.M24 = t.M34 = 0; t.M41 = t.M42 = t.M43 = 0; t.M44 = 1;
        return t;
    }
}
