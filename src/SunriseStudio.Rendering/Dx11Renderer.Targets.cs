using System.Numerics;
using Vortice.Mathematics;

namespace SunriseStudio.Rendering;

public sealed unsafe partial class Dx11Renderer
{
    /** RestoreMainTarget must follow offscreen draws before rendering the host's ImGui frame. */
    public void BeginOffscreen(NativeRenderTargets targets, Vector4 clear)
    {
        Context.PSSetShaderResource(0, null!);
        Context.OMSetRenderTargets(targets.Views, targets.DepthView);
        Context.RSSetViewport(new Viewport(targets.Width, targets.Height));
        Context.RSSetScissorRect(targets.Width, targets.Height);
        ClearNativeTargets(targets, clear, 1);
    }
}
