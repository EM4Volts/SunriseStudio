using System.Numerics;
using Vortice.Direct3D;

namespace SunriseStudio.Rendering;

/** Scope 13 (transparent) pixel CB2 and the extern 39 vectors 5 to 9 it copies; 0x7FF742E86E90. */
public static class NativeTransparentScope
{
    public const uint ScopeTag = 0x80B356E8;
    // The authored pixel template of scope 13 holds six vectors.
    public const int Rows = 6;

    // 0x7FF742E85910 reads these keys through the frame parameter block at frame +0x36F20.
    public const uint UpColorKey = 0x7C7F9077, UpScaleKey = 0x52717901, UpSpreadKey = 0xF20B0FF3;
    public const uint DownColorKey = 0x603EF86C, DownScaleKey = 0xCD02F312, DownSpreadKey = 0x74FC62C4;
    public const uint UniformColorKey = 0xE43477BE, UniformScaleKey = 0x98552BE8;
    public const uint UpVectorKey = 0x6E22B25E;

    /**
     * Extern 39 vectors 6 to 8: per colour channel, L1 irradiance as (xyz, w) with E(n) = dot(xyz, n) + w.
     * @return Three vectors for red, green and blue.
     */
    public static Vector4[] ReadLighting(TfxParameterDefaults defaults, TfxParameterLookup parameters)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(parameters);
        Vector4 Read(uint key)
        {
            var entry = defaults.Entries.FirstOrDefault(e => e.LookupKey == key)
                ?? throw new InvalidDataException($"Transparent lighting key 0x{key:X8} is missing from the authored table.");
            return parameters.Read(checked((byte)entry.Index));
        }
        var up = Read(UpVectorKey);
        var direction = new Vector3(up.X, up.Y, up.Z);
        var rows = new Vector4[3];
        // Each light adds colour * (2/3 * spread * direction, 1/3); the uniform term adds only W.
        void Add(Vector4 color, float scale, float spread, Vector3 toward)
        {
            float t = Math.Clamp(spread, 0, 1);
            for (int channel = 0; channel < 3; channel++)
            {
                float c = color[channel] * scale * 0.25f;
                rows[channel] += new Vector4(toward * (c * 2 / 3 * t), c / 3);
            }
        }
        Add(Read(UpColorKey), Read(UpScaleKey).X, Read(UpSpreadKey).X, direction);
        Add(Read(DownColorKey), Read(DownScaleKey).X, Read(DownSpreadKey).X, -direction);
        Add(Read(UniformColorKey), Read(UniformScaleKey).X, 0, Vector3.Zero);
        return rows;
    }

    /**
     * Extern 39 vector 5: the two height-fog densities at the camera height, then their falloffs.
     * @param atmosphereSlots The 30 atmosphere parameter slots.
     */
    public static Vector4 HeightFog(IReadOnlyList<Vector4> atmosphereSlots, float cameraHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(atmosphereSlots.Count, 30);
        // 0x7FF742EF2330 caps each exponent at 16 and floors each falloff at 1e-4 after use.
        static (float Density, float Falloff) Fall(float density, float falloff, float height, float cameraZ) =>
            (density * MathF.Exp(MathF.Min((cameraZ - height) * falloff * -0.001f, 16)), MathF.Max(falloff, 1e-4f));
        var a = Fall(atmosphereSlots[6].X, atmosphereSlots[9].X, atmosphereSlots[10].X, cameraHeight);
        var b = Fall(atmosphereSlots[13].X, atmosphereSlots[14].X, atmosphereSlots[15].X, cameraHeight);
        return new(a.Density, b.Density, a.Falloff, b.Falloff);
    }

    /**
     * Runs the authored scope 13 pixel program, which copies extern 3 vector 0 and extern 39 vectors 5 to 9.
     * @param lighting Extern 39 vectors 6 to 8 from ReadLighting.
     * @return The six CB2 vectors.
     */
    public static Vector4[] Evaluate(GameScopeExecution scope, Vector4 extern3Vector0, Vector4 heightFog,
        IReadOnlyList<Vector4> lighting)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Name != "transparent" || scope.ScopeIndex != 13 || scope.Pixel.ConstantBufferSlot != 2
            || scope.Pixel.Data.ConstantTemplate.Length != Rows)
            throw new NotSupportedException("Transparent scope identity or constant slot changed.");
        ArgumentOutOfRangeException.ThrowIfNotEqual(lighting.Count, 3);
        var inputs = new TfxInputs();
        inputs.Vectors[new(3, 0)] = extern3Vector0;
        inputs.Vectors[new(39, 5)] = heightFog;
        for (int i = 0; i < 3; i++) inputs.Vectors[new(39, (byte)(6 + i))] = lighting[i];
        // Vector 9 is zero; 0x7FF742E86FEC stores it.
        inputs.Vectors[new(39, 9)] = Vector4.Zero;
        // The program also binds textures; the renderer supplies those views itself.
        inputs.Resources[new(3, 7)] = ScopeResource;
        for (byte i = 0; i <= 8; i++) inputs.Resources[new(39, i)] = ScopeResource;
        var result = TfxEvaluator.Evaluate(scope.Pixel.Data, inputs);
        result.RequireComplete();
        return result.Constants;
    }

    private static readonly TfxResource ScopeResource = new("transparent scope texture", ShaderResourceViewDimension.Texture2D);
}
