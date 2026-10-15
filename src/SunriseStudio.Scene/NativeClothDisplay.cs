using System.Numerics;
using System.Runtime.InteropServices;

namespace SunriseStudio.Scene;

/** Cloth parts whose vertex shader reads object-space float elements from stream 0. */
public static class NativeClothDisplay
{
    // Cloth display stream: three float4 elements in a 48-byte vertex.
    private const int Stride = 48;

    /** True for a cloth part drawn through declaration 18 with float4 elements at 0, 16 and 32. */
    public static bool IsDisplayMesh(MeshInstance instance) =>
        instance.RequiresClothSimulation && instance.Mesh.Native is { Kind: NativeMeshKind.Entity, NativeDeclarationId: 18 } native
        && native.VertexStreams.Length != 0 && native.VertexStreams[0].Stride == Stride
        && Float4(native, "NORMAL", 16) && Float4(native, "TANGENT", 32) && Float4(native, "POSITION", 0);

    private static bool Float4(NativeMeshData native, string semantic, int offset) =>
        native.InputElements.Any(e => e.Semantic == semantic && e.SemanticIndex == 0 && e.Slot == 0
            && e.Offset == offset && e.Format == NativeVertexFormat.R32G32B32A32Float);

    /** Stream 0 with positions scaled and offset into object space; every w lane is kept. */
    public static byte[] BindStream(NativeMeshData native)
    {
        var bytes = (byte[])native.VertexStreams[0].Data.Clone();
        var lanes = MemoryMarshal.Cast<byte, Vector4>(bytes.AsSpan());
        var scale = new Vector3(native.PositionScale.X, native.PositionScale.Y, native.PositionScale.Z);
        var offset = new Vector3(native.PositionOffset.X, native.PositionOffset.Y, native.PositionOffset.Z);
        bool uniform = scale.X == scale.Y && scale.Y == scale.Z;
        for (int at = 0; at + 2 < lanes.Length; at += 3)
        {
            lanes[at] = new Vector4(Xyz(lanes[at]) * scale + offset, lanes[at].W);
            if (uniform) continue;
            lanes[at + 1] = new Vector4(Rescaled(Xyz(lanes[at + 1]), Vector3.One / scale), lanes[at + 1].W);
            lanes[at + 2] = new Vector4(Rescaled(Xyz(lanes[at + 2]), scale), lanes[at + 2].W);
        }
        return bytes;
    }

    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);

    /** A direction under a non-uniform scale keeps its authored length. */
    private static Vector3 Rescaled(Vector3 direction, Vector3 scale)
    {
        var scaled = direction * scale;
        return scaled.LengthSquared() > 1e-12f ? Vector3.Normalize(scaled) * direction.Length() : direction;
    }
}
