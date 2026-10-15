using System.Numerics;

namespace SunriseStudio.Rendering;

/** Export previews and Studio use the same authored defaults and object-channel policy. */
public static class NativeInspectionMaterialInputs
{
    public const uint RenderGlobalsTag = 0x80B9E57C;
    public static readonly Vector4 ObjectChannelDefault = Vector4.One;

    /** Camera-dependent programs receive an identity camera when no view is supplied. */
    public static TfxInputs Create(double simulationSeconds, TfxParameterLookup? parameters,
        TfxInputs? camera = null, float timeOfDay = NativeFrameInputs.ViewerTimeOfDay)
    {
        var inputs = NativeFrameInputs.Create(simulationSeconds, timeOfDay: timeOfDay);
        inputs.Parameters = parameters;
        inputs.ObjectChannelDefault = ObjectChannelDefault;
        camera ??= NativeViewInputs.CreateCameraInputs(1, 1, Matrix4x4.Identity, Matrix4x4.Identity);
        foreach (var matrix in camera.Matrices) inputs.Matrices[matrix.Key] = matrix.Value;
        // Extern 2 floats 0 and 1 are the target width and height in pixels.
        foreach (var value in camera.Floats) inputs.Floats[value.Key] = value.Value;
        return inputs;
    }
}
