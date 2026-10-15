using System.Numerics;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** Isolated previews supply explicit inspection values instead of pretending to own map data. */
public static class NativeAssetBindings
{
    // Bit 25 selects one shared scalar instead of indexing it by vertex ID.
    private const uint SingleScalar = 0x02000000;
    /** Static shaders receive their model transform and one fully visible ambient-occlusion scalar. */
    public static NativeStaticBinding CreateStatic(MeshInstance instance)
    {
        var native = instance.Mesh.Native;
        if (native is not { Kind: NativeMeshKind.Static } || instance.InstanceSourceTag != 0
            || !instance.NativeInstanceData.IsEmpty)
            throw new NotSupportedException("Isolated bindings require a standalone static mesh.");
        var model = Matrix4x4.CreateScale(native.PositionScale.X)
            * Matrix4x4.CreateTranslation(native.PositionOffset.X, native.PositionOffset.Y, native.PositionOffset.Z)
            * instance.RenderTransform;
        var constants = new Vector4[]
        {
            new(model.M11, model.M21, model.M31, model.M41),
            new(model.M12, model.M22, model.M32, model.M42),
            new(model.M13, model.M23, model.M33, model.M43),
            new(native.UvTransform.X, native.UvTransform.Z, native.UvTransform.W,
                BitConverter.UInt32BitsToSingle(SingleScalar))
        };
        if (constants.Take(3).Any(x => !float.IsFinite(x.X) || !float.IsFinite(x.Y)
            || !float.IsFinite(x.Z) || !float.IsFinite(x.W))
            || !float.IsFinite(native.UvTransform.X) || !float.IsFinite(native.UvTransform.Z)
            || !float.IsFinite(native.UvTransform.W))
            throw new InvalidDataException("Isolated mesh transform is not finite.");
        return new NativeStaticBinding
        {
            Map = new(0, 0, 0, 0, 0), ScalarBuffer = new(0, 0, 1, 0, [255, 255, 255, 255]),
            InstanceConstants = constants, PackedWord = SingleScalar, UseSingleScalar = true
        };
    }
}
