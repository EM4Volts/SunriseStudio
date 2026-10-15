using Vortice.Direct3D11;

namespace SunriseStudio.Rendering;

/** Pass and forced state words the client writes before it submits each render stage. */
public static class NativeStagePassStates
{
    // Context +0x1660 holds this between submitters; stages whose submitter was not read use it.
    public const uint ResetPassStates = 0x81828281;

    // First-view words; byte 0 blend, 1 depth/stencil, 2 rasterizer, 3 bias.
    // Stage 0 draws blend 1, or blend 0 after a depth prepass; both are opaque.
    private static readonly Dictionary<int, (uint Pass, uint Forced)> Stages = new()
    {
        [0] = (0x81828481, 0), [1] = (0x8182C08C, 0), [2] = (0x8182C08C, 0),
        // Render_ShadowGenerate_SetPassStates 0x7FF742F0D980: no colour, depth 13, no culling, bias 3.
        // Only the blend byte is left to the material. Shadow views use the primary depth table.
        [3] = (0x83818DCE, 0x83818D00),
        [6] = (0x8182C082, 0), [7] = (0x8182C082, 0), [8] = (0x81828F81, 0x82),
        [10] = (0x8182A481, 0), [12] = (0x818284CE, 0xCE),
    };

    /** @return The stage's pass and forced words; an unread stage gets the reset word. */
    public static (uint Pass, uint Forced) Get(int renderStage) =>
        Stages.GetValueOrDefault(renderStage, (ResetPassStates, 0u));

    /** A forced stage byte beats the material override, which beats the stage pass byte. */
    public static NativeStateSelection Resolve(uint materialOverrides, int renderStage)
    {
        var (pass, forced) = Get(renderStage);
        return NativeRenderStateCatalog.ResolveSelection(pass, materialOverrides, forced);
    }

    /** @return NoColor, Opaque, InverseAlpha, PremultipliedAlpha, Additive or Alpha for target 0. */
    public static string ClassifyBlend(RenderTargetBlendDescription blend)
    {
        if (blend.RenderTargetWriteMask == ColorWriteEnable.None) return "NoColor";
        if (!blend.BlendEnable) return "Opaque";
        if (blend.SourceBlend == Blend.One && blend.DestinationBlend == Blend.SourceAlpha) return "InverseAlpha";
        if (blend.SourceBlend == Blend.One && blend.DestinationBlend == Blend.InverseSourceAlpha) return "PremultipliedAlpha";
        return blend.DestinationBlend == Blend.One ? "Additive" : "Alpha";
    }
}
