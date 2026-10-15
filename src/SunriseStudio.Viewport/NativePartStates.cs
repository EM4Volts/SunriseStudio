using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using Vortice.Direct3D11;

namespace SunriseStudio.Viewport;

/** The blend and depth a part draws with: its stage's pass states with the material overrides on top. */
public static class NativePartStates
{
    private static readonly Lazy<NativeRenderStateCatalog> Catalog = new(() => new());

    /**
     * Resolves the part's states the way the native renderer does, with the alternate depth table.
     * @return The blend class of target 0 and the depth class.
     */
    public static (string Blend, string Depth) Describe(uint materialOverrides, int renderStage)
    {
        var selection = NativeStagePassStates.Resolve(materialOverrides, renderStage);
        var blend = Catalog.Value.GetBlend(selection.Blend).RenderTarget[0];
        var depth = Catalog.Value.GetDepthStencil(selection.DepthStencil, NativeDepthTable.Alternate);
        return (NativeStagePassStates.ClassifyBlend(blend), DepthClass(depth));
    }

    /** @return Off, "Test, <comparison>" or "Test and write, <comparison>". */
    public static string DepthClass(DepthStencilDescription depth) => !depth.DepthEnable ? "Off"
        : $"{(depth.DepthWriteMask == DepthWriteMask.All ? "Test and write" : "Test")}, {depth.DepthFunc}";

    /** One inspector line, such as "Opaque; depth Test and write, GreaterEqual". */
    public static string Label(NativeMeshData native)
    {
        try
        {
            var (blend, depth) = Describe(native.MaterialStateOverrides, native.RenderStage);
            return $"{blend}; depth {depth}";
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or ArgumentOutOfRangeException
            or InvalidOperationException)
        {
            return "Unavailable: " + error.Message;
        }
    }
}
