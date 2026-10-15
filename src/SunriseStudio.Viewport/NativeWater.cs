using System.Numerics;
using SunriseStudio.Rendering;
using Vortice.Direct3D;

namespace SunriseStudio.Viewport;

/** Frame inputs of the water surface, render feature 22, which draws in the transparents stage. */
public static class NativeWater
{
    public const string Policy = "Water: placement class 0x80806DE0 places a model that draws in the transparents stage"
        + " with the frame inputs of 0x7FF742E1AA20 (extern 67), the atmosphere fill (extern 7) and its object's"
        + " position and scale (extern 67 vector 4, float 28). Refraction (extern 67 resource 0) is a copy of the lit"
        + " colour before transparents. The view environment (extern 3 resource 19) is the lighting mode's sky"
        + " reflection. Viewer stand-ins, because their producers are not reproduced: screen offsets (resource 1) hold"
        + " each pixel's own UV, shadow (resource 3) is 1, ripple normals (resource 5) are flat, the planar reflection"
        + " (resource 6) is black, extern 67 vector 5 is (0, 0.05, 0, 0.05) and extern 42 float 12 is 0. The water"
        + " stencil test on bit 2 is dropped.";

    public static readonly TfxResource Refraction = new("water refraction", ShaderResourceViewDimension.Texture2D);
    public static readonly TfxResource ScreenOffsets = new("water screen offsets", ShaderResourceViewDimension.Texture2D);
    public static readonly TfxResource Shadow = new("water shadow", ShaderResourceViewDimension.Texture2D);
    public static readonly TfxResource Ripples = new("water ripples", ShaderResourceViewDimension.Texture2D);
    public static readonly TfxResource Reflection = new("water planar reflection", ShaderResourceViewDimension.Texture2D);
    public static readonly TfxResource AtmosphereLookup = new("atmosphere 2D lookup", ShaderResourceViewDimension.Texture2D);
    public static readonly TfxResource ScreenLookup = new("screen atmosphere lookup", ShaderResourceViewDimension.Texture2D);
    public static readonly TfxResource Environment = new("view environment", ShaderResourceViewDimension.Texture2D);

    /**
     * Adds the extern 3, 7, 42 and 67 values the water programs read.
     * @param atmosphereSlots The 30 atmosphere parameter slots; null leaves extern 7 unset.
     * @param lookupSize Size of the screen atmosphere lookup, as extern 7 vector 5.
     */
    public static void Apply(TfxInputs inputs, IReadOnlyList<Vector4>? atmosphereSlots, float cameraHeight, Vector4 lookupSize)
    {
        if (atmosphereSlots is not null)
        {
            NativeAtmosphere.ApplyParameters(inputs, atmosphereSlots, cameraHeight);
            inputs.Vectors[new(7, 5)] = lookupSize;
            inputs.Resources[new(7, 7)] = AtmosphereLookup;
            inputs.Resources[new(7, 17)] = ScreenLookup;
        }
        // Extern 3 vector 1 is render-global keys 0x7C7F9077 (1, 1, 1) and 0x52717901 (0.1).
        inputs.Vectors[new(3, 1)] = new(1, 1, 1, 0.1f);
        inputs.Resources[new(3, 19)] = Environment;
        inputs.Floats[new(42, 12)] = 0;
        ApplyObject(inputs, Vector3.Zero, 1);
        inputs.Vectors[new(67, 5)] = new(0, 0.05f, 0, 0.05f);
        inputs.Resources[new(67, 0)] = Refraction;
        inputs.Resources[new(67, 1)] = ScreenOffsets;
        inputs.Resources[new(67, 3)] = Shadow;
        inputs.Resources[new(67, 5)] = Ripples;
        inputs.Resources[new(67, 6)] = Reflection;
    }

    /** Extern 67 vector 4 and float 28: the object's position with w 1, and its scale; 0x7FF742E74D00. */
    public static void ApplyObject(TfxInputs inputs, Vector3 position, float scale)
    {
        inputs.Vectors[new(67, 4)] = new(position, 1);
        inputs.Floats[new(67, 28)] = scale;
    }

    /**
     * The one water object all parts of a material belong to.
     * @return Position and uniform scale; throws when the parts sit at different objects.
     */
    public static (Vector3 Position, float Scale) SingleObject(IEnumerable<Matrix4x4> transforms)
    {
        var objects = transforms.Select(t => (t.Translation, new Vector3(t.M11, t.M12, t.M13).Length())).Distinct().ToArray();
        if (objects.Length != 1)
            throw new NotSupportedException($"Water material spans {objects.Length} objects; extern 67 is per object.");
        return objects[0];
    }

    /**
     * True for the water test: read-only Equal on stencil bit 2, in a material reading extern 67.
     * The water's own draw under forced state 0x8482 (depth-stencil pair 4) marks that bit; 0x7FF742E74D00.
     */
    public static bool IsWaterStencil(Vortice.Direct3D11.DepthStencilDescription state, MaterialExecutionData execution) =>
        state.StencilReadMask == WaterStencilBit && state.StencilWriteMask == 0
        && state.FrontFace.StencilFunc == Vortice.Direct3D11.ComparisonFunction.Equal
        && state.BackFace.StencilFunc == Vortice.Direct3D11.ComparisonFunction.Equal
        && ReadsObject(execution);

    /** True when a TFX program reads extern 67, the water object block. */
    public static bool ReadsObject(MaterialExecutionData execution) =>
        new[] { execution.Vertex, execution.Pixel }.Any(stage => TfxProgramInspection.Read(stage.TfxBytecode).Dependencies.Any(d => d.Extern == 67));

    // Stencil bit the water test reads.
    private const byte WaterStencilBit = 0x04;

    /** RG16 texture whose texel at (x, y) holds its own centre UV. */
    public static byte[] ScreenUv(int width, int height)
    {
        var bytes = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                ushort u = (ushort)MathF.Round((x + 0.5f) / width * 65535), v = (ushort)MathF.Round((y + 0.5f) / height * 65535);
                bytes[offset] = (byte)u; bytes[offset + 1] = (byte)(u >> 8);
                bytes[offset + 2] = (byte)v; bytes[offset + 3] = (byte)(v >> 8);
            }
        return bytes;
    }
}
