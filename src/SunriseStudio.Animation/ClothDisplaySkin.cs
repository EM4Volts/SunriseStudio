using System.Numerics;
using System.Runtime.InteropServices;

namespace SunriseStudio.Animation;

/** Poses a cloth display stream on the CPU; cloth simulation is not evaluated. */
public static class ClothDisplaySkin
{
    // Declaration 18 slot 0: float4 POSITION, NORMAL and TANGENT at offsets 0, 16 and 32.
    public const int Stride = 48;

    /**
     * Writes the weighted skin of every position, normal and tangent; every w lane is copied.
     * @param bind Object-space bind stream, one 48-byte vertex per weight.
     * @param palette Matrices by skeleton bone; zero-weight lanes never read it.
     * @param output Receives the posed stream; it must be as long as bind.
     */
    public static void Skin(ReadOnlySpan<byte> bind, IReadOnlyList<VertexSkinWeights> weights,
        IReadOnlyList<Matrix4x4> palette, Span<byte> output)
    {
        if (bind.Length != weights.Count * Stride || output.Length != bind.Length)
            throw new ArgumentException("Cloth stream length differs from its weights.");
        var source = MemoryMarshal.Cast<byte, Vector4>(bind);
        var target = MemoryMarshal.Cast<byte, Vector4>(output);
        for (int vertex = 0; vertex < weights.Count; vertex++)
        {
            var influence = weights[vertex];
            int at = vertex * 3;
            Vector4 position = source[at], normal = source[at + 1], tangent = source[at + 2];
            Vector3 p = Vector3.Zero, n = Vector3.Zero, t = Vector3.Zero;
            for (int lane = 0; lane < 4; lane++)
            {
                float weight = influence.Weights[lane];
                if (weight <= 0) continue;
                var matrix = palette[lane switch
                    { 0 => influence.Bone0, 1 => influence.Bone1, 2 => influence.Bone2, _ => influence.Bone3 }];
                p += Vector3.Transform(Xyz(position), matrix) * weight;
                n += Vector3.TransformNormal(Xyz(normal), matrix) * weight;
                t += Vector3.TransformNormal(Xyz(tangent), matrix) * weight;
            }
            target[at] = new Vector4(p, position.W);
            target[at + 1] = new Vector4(Direction(n, Xyz(normal)), normal.W);
            target[at + 2] = new Vector4(Direction(t, Xyz(tangent)), tangent.W);
        }
    }

    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);

    /** A blended direction is unit length; a degenerate blend keeps the bind direction. */
    private static Vector3 Direction(Vector3 blended, Vector3 bind) =>
        blended.LengthSquared() > 1e-12f ? Vector3.Normalize(blended) : bind;
}
