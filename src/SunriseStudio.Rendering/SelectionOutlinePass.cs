using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SunriseStudio.Rendering;

/** The selection mask uses the same uploaded vertices as the geometry preview. */
public sealed class SelectionOutlinePass : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Settings
    {
        public Matrix4x4 Transform;
        public Vector4 Color;
        public Vector4 Flags;
    }

    private readonly Dx11Renderer renderer;
    private readonly ID3D11VertexShader meshShader, fullscreenShader;
    private readonly ID3D11PixelShader maskShader, compositeShader;
    private readonly ID3D11InputLayout layout;
    private readonly ID3D11Buffer constants;
    private readonly ID3D11RasterizerState rasterizer;
    private readonly ID3D11DepthStencilState depth, copyDepth;
    private readonly ID3D11BlendState opaque;
    private NativeRenderTargets? mask, output, scene;
    private ID3D11ShaderResourceView? sceneDepth;
    private bool disposed;

    public Vector4 Color { get; set; } = new(1, .53f, .08f, 1);
    public int Radius { get; set; } = 2;

    /** Plain fallback draws write the same depth later read by the selection mask. */
    public (GpuTexture Image, ID3D11ShaderResourceView Depth) CopyScene(GpuTexture source,
        ID3D11ShaderResourceView sourceDepth)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (scene is null || scene.Width != source.Width || scene.Height != source.Height)
        {
            var next = renderer.CreateNativeTargets(source.Width, source.Height,
                [Format.R8G8B8A8_UNorm], Format.D32_Float, true);
            scene?.Dispose();
            scene = next;
        }
        DrawComposite(source, sourceDepth, scene, null);
        return (scene.Textures[0], scene.DepthShaderView!);
    }

    /** Owned graphics resources must be released before the host renderer. */
    public SelectionOutlinePass(Dx11Renderer renderer)
    {
        this.renderer = renderer;
        var device = renderer.Device;
        var vertexCode = Dx11Renderer.Compile(Shader, "VSMask", "vs_5_0");
        meshShader = device.CreateVertexShader(vertexCode.Span);
        fullscreenShader = device.CreateVertexShader(Dx11Renderer.Compile(Shader, "VSFull", "vs_5_0").Span);
        maskShader = device.CreatePixelShader(Dx11Renderer.Compile(Shader, "PSMask", "ps_5_0").Span);
        compositeShader = device.CreatePixelShader(Dx11Renderer.Compile(Shader, "PSComposite", "ps_5_0").Span);
        layout = device.CreateInputLayout([new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0)], vertexCode.Span);
        constants = device.CreateBuffer((uint)Marshal.SizeOf<Settings>(), BindFlags.ConstantBuffer);
        rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        depth = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.LessEqual));
        copyDepth = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.Always));
        opaque = device.CreateBlendState(new BlendDescription(Blend.One, Blend.Zero));
    }

    /** Begin once per image; the returned image remains valid until the next resize or disposal. */
    public void Begin(int width, int height, ID3D11ShaderResourceView? sourceDepth)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (mask is null || mask.Width != width || mask.Height != height)
        {
            var nextMask = renderer.CreateNativeTargets(width, height, Format.R8_UNorm);
            NativeRenderTargets nextOutput;
            try { nextOutput = renderer.CreateNativeTargets(width, height, Format.R8G8B8A8_UNorm); }
            catch { nextMask.Dispose(); throw; }
            mask?.Dispose(); output?.Dispose();
            mask = nextMask; output = nextOutput;
        }
        sceneDepth = sourceDepth;
        renderer.BeginOffscreen(mask, Vector4.Zero);
        var context = renderer.Context;
        context.HSSetShader(null!); context.DSSetShader(null!); context.GSSetShader(null!);
        context.IASetInputLayout(layout);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(meshShader);
        context.VSSetConstantBuffer(0, constants);
        context.PSSetShader(maskShader);
        context.PSSetConstantBuffer(0, constants);
        context.PSSetShaderResource(2, sourceDepth!);
        context.RSSetState(rasterizer);
        context.OMSetDepthStencilState(depth);
        context.OMSetBlendState(opaque);
    }

    /** Only selected geometry enters the mask; depth can be ignored for an x-ray outline. */
    public void Draw(GpuMesh mesh, Matrix4x4 worldViewProjection, bool ignoreSceneDepth = false)
    {
        if (mask is null) throw new InvalidOperationException("Begin the selection pass before drawing.");
        var context = renderer.Context;
        var settings = new Settings { Transform = worldViewProjection, Flags = new(sceneDepth is null || ignoreSceneDepth ? 0 : 1, 0, 0, 0) };
        context.UpdateSubresource(in settings, constants);
        context.IASetVertexBuffer(0, mesh.Vertices, (uint)Marshal.SizeOf<RenderVertex>());
        context.IASetIndexBuffer(mesh.Indices, Format.R32_UInt, 0);
        context.DrawIndexed(mesh.IndexCount, 0, 0);
        renderer.CountDraws(1);
    }

    /** Composite keeps the scene color and depth; only pixels outside the mask receive outline color. */
    public GpuTexture Composite(GpuTexture source)
    {
        if (mask is null || output is null) throw new InvalidOperationException("Begin the selection pass before compositing.");
        DrawComposite(source, sceneDepth, output, mask.Textures[0]);
        return output.Textures[0];
    }

    /** A null mask copies color and depth before fallback geometry is drawn. */
    private void DrawComposite(GpuTexture source, ID3D11ShaderResourceView? sourceDepth,
        NativeRenderTargets destination, GpuTexture? sourceMask)
    {
        var context = renderer.Context;
        context.OMSetRenderTargets(destination.Views, destination.DepthView);
        context.RSSetViewport(new Viewport(destination.Width, destination.Height));
        context.RSSetScissorRect(destination.Width, destination.Height);
        context.IASetInputLayout(null!);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.HSSetShader(null!); context.DSSetShader(null!); context.GSSetShader(null!);
        context.VSSetShader(fullscreenShader);
        context.PSSetShader(compositeShader);
        context.PSSetConstantBuffer(0, constants);
        context.RSSetState(rasterizer);
        context.OMSetDepthStencilState(copyDepth);
        context.OMSetBlendState(opaque);
        var settings = new Settings { Color = Color, Flags = new(sourceDepth is null ? 0 : 1,
            sourceMask is null ? 0 : Math.Clamp(Radius, 1, 4), destination.Width, destination.Height) };
        context.UpdateSubresource(in settings, constants);
        context.PSSetShaderResources(0, [source.View, sourceMask?.View!, sourceDepth!]);
        context.Draw(3, 0);
        renderer.CountDraws(1);
        context.PSSetShaderResources(0, new ID3D11ShaderResourceView[3]);
    }

    /** ImGui must finish sampling the output image before its targets are released. */
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        mask?.Dispose(); output?.Dispose(); scene?.Dispose();
        opaque.Dispose(); copyDepth.Dispose(); depth.Dispose(); rasterizer.Dispose();
        constants.Dispose(); layout.Dispose(); compositeShader.Dispose(); maskShader.Dispose();
        fullscreenShader.Dispose(); meshShader.Dispose();
    }

    private const string Shader = """
        cbuffer Settings : register(b0) {
            row_major float4x4 Transform;
            float4 OutlineColor;
            float4 Flags;
        };
        Texture2D<float4> Source : register(t0);
        Texture2D<float> Mask : register(t1);
        Texture2D<float> SceneDepth : register(t2);
        float4 VSMask(float3 position : POSITION) : SV_POSITION {
            return mul(float4(position, 1), Transform);
        }
        float PSMask(float4 position : SV_POSITION) : SV_TARGET {
            if (Flags.x != 0 && position.z > SceneDepth.Load(int3(position.xy, 0)) + 0.00002) discard;
            return 1;
        }
        float4 VSFull(uint id : SV_VertexID) : SV_POSITION {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }
        struct Output { float4 Color : SV_TARGET; float Depth : SV_DEPTH; };
        Output PSComposite(float4 position : SV_POSITION) {
            int2 pixel = int2(position.xy);
            float center = Mask.Load(int3(pixel, 0));
            float expanded = 0;
            int radius = int(Flags.y);
            for (int y = -radius; y <= radius; y++)
                for (int x = -radius; x <= radius; x++)
                    if (x*x + y*y <= radius*radius)
                        expanded = max(expanded, Mask.Load(int3(clamp(pixel + int2(x,y), int2(0,0), int2(Flags.zw)-1), 0)));
            Output output;
            output.Color = lerp(Source.Load(int3(pixel, 0)), OutlineColor, saturate(expanded-center));
            output.Depth = Flags.x != 0 ? SceneDepth.Load(int3(pixel, 0)) : 1;
            return output;
        }
        """;
}
