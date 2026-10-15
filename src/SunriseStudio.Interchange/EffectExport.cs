using System.Buffers.Binary;
using System.Diagnostics;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;
using Vortice.Direct3D11;

namespace SunriseStudio.Interchange;

/** Bakes the viewer's particle simulation into looped sprite samples that Blender plays back. */
public static class EffectExport
{
    public const string Format = "sunrise-effects";
    public const int Version = 1, SampleStride = 20;
    // Sprites use the largest mip within this edge, as the viewer does.
    private const int SpriteEdge = 256;
    // About 160 MB of samples; past it, later emitters stay markers.
    private const long MaxSamples = 8_000_000;

    /**
     * Writes the effects document, its sample file and its sprites.
     * @param document Path of the document, relative to root.
     * @param folder Folder for the sample file and sprites, relative to root.
     * @return Null when the scene places no effect markers.
     */
    public static EffectExportSet? Write(PackageCatalog catalog, SceneDocument scene, string root, string document,
        string folder, EffectBakeOptions? options = null, CancellationToken token = default, Action<string>? progress = null)
    {
        var markers = scene.Markers.Where(marker => marker.Effect is not null).ToArray();
        if (markers.Length == 0) return null;
        var clock = Stopwatch.StartNew();
        progress?.Invoke($"Baking {markers.Length} effects");
        var baked = EffectBake.Run(scene, options, token);
        var skipped = new List<EffectExportSkip>();
        foreach (var marker in markers.Where(marker => marker.Effect!.Kind == EffectKind.LensFlare))
            skipped.Add(Skip(marker, "LensFlare", -1, "Lens flares are screen-space; Blender draws none."));
        var kept = new List<EffectBakeEmitter>();
        long total = 0;
        foreach (var emitter in baked)
        {
            if (emitter.Failure is { } failure) { skipped.Add(Skip(emitter.Marker, "ParticleSystem", emitter.System, failure)); continue; }
            if (total + emitter.Samples.Count > MaxSamples)
            { skipped.Add(Skip(emitter.Marker, "ParticleSystem", emitter.System, "The export's sample budget is spent.")); continue; }
            total += emitter.Samples.Count;
            kept.Add(emitter);
        }
        token.ThrowIfCancellationRequested();
        var sprites = new List<EffectExportSprite>();
        var materials = new List<EffectExportMaterial>();
        var materialIds = new Dictionary<uint, int>();
        var reader = new GameMaterialReader(catalog);
        var states = new NativeRenderStateCatalog();
        foreach (uint tag in kept.Select(emitter => emitter.Material).Distinct())
        {
            materialIds.Add(tag, materials.Count);
            materials.Add(Material(catalog, reader, states, tag, materials.Count, root, folder, sprites));
        }
        string dataPath = folder + "/particles.bin";
        string data = BundlePaths.Resolve(root, dataPath);
        Directory.CreateDirectory(Path.GetDirectoryName(data)!);
        var emitters = new List<EffectExportEmitter>();
        long first = 0;
        using (var stream = new FileStream(data, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var row = new byte[SampleStride];
            foreach (var emitter in kept)
            {
                token.ThrowIfCancellationRequested();
                foreach (var sample in emitter.Samples)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(row, sample.Position.X);
                    BinaryPrimitives.WriteSingleLittleEndian(row.AsSpan(4), sample.Position.Y);
                    BinaryPrimitives.WriteSingleLittleEndian(row.AsSpan(8), sample.Position.Z);
                    BinaryPrimitives.WriteHalfLittleEndian(row.AsSpan(12), (Half)sample.Size);
                    BinaryPrimitives.WriteHalfLittleEndian(row.AsSpan(14), (Half)sample.Alpha);
                    // Half precision needs the angle kept small.
                    BinaryPrimitives.WriteHalfLittleEndian(row.AsSpan(16), (Half)(sample.Spin % MathF.Tau));
                    BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(18), checked((ushort)sample.Frame));
                    stream.Write(row);
                }
                var marker = emitter.Marker;
                var effect = marker.Effect!;
                var system = effect.Systems[emitter.System];
                string name = $"{marker.Name} {system.Tag:X8}" + (emitter.Instance == 0 ? "" : $" #{emitter.Instance}");
                emitters.Add(new(emitters.Count, name, marker.TagHash, marker.SourceTag,
                    marker.SourceOffset, marker.PlacementIdentity, emitter.System, emitter.Instance, system.Tag, system.EmitterTag,
                    materialIds[emitter.Material], emitter.Default, emitter.Trigger, emitter.Mode, emitter.Frames, system.GpuSimulated,
                    [emitter.Origin.X, emitter.Origin.Y, emitter.Origin.Z], first, emitter.Samples.Count));
                first += emitter.Samples.Count;
            }
        }
        long length = new FileInfo(data).Length;
        var notices = Notices(markers.Length, emitters, skipped);
        var set = new EffectExportSet(Format, Version, scene.Name, "NativeZUp", EffectBake.Rate, dataPath, BundlePaths.FileHash(data),
            length, SampleStride, sprites, materials, emitters, skipped, notices,
            new(emitters.Count, skipped.Count, first, length, sprites.Count, clock.Elapsed.TotalSeconds));
        BundlePaths.Write(BundlePaths.Resolve(root, document), set);
        return set;
    }

    private static EffectExportSkip Skip(SceneMarker marker, string kind, int system, string reason) =>
        new(marker.Name, marker.TagHash, marker.SourceTag, marker.SourceOffset, kind, system, reason);

    private static List<BundleNotice> Notices(int markers, List<EffectExportEmitter> emitters, List<EffectExportSkip> skipped)
    {
        var notices = new List<BundleNotice>();
        if (emitters.Count != 0)
            notices.Add(new("Effects", "Baked", $"{emitters.Count} particle emitters play as looped sprites, as Studio draws them;"
                + " sprite size and fade are Studio's choice."));
        if (emitters.Count(emitter => !emitter.Default) is > 0 and var hidden)
            notices.Add(new("Effects", "Hidden", $"{hidden} emitters that gameplay starts are baked as Show all runs them, and hidden."));
        if (emitters.Count(emitter => emitter.Gpu) is > 0 and var gpu)
            notices.Add(new("Effects", "Approximate", $"{gpu} emitters the game simulates on the GPU use Studio's CPU steps."));
        foreach (var group in skipped.GroupBy(skip => skip.Reason))
            notices.Add(new("Effects", "Markers only", $"{group.Count()} {(group.First().Kind == "LensFlare" ? "lens flares" : "particle systems")}"
                + $" stay hidden markers: {group.Key}"));
        if (markers != 0 && emitters.Count == 0 && skipped.Count == 0)
            notices.Add(new("Effects", "Markers only", $"{markers} effects draw nothing."));
        return notices;
    }

    /** The viewer's sprite and blend for one material; without a usable sprite it draws an additive soft dot. */
    private static EffectExportMaterial Material(PackageCatalog catalog, GameMaterialReader reader, NativeRenderStateCatalog states,
        uint tag, int id, string root, string folder, List<EffectExportSprite> sprites)
    {
        var additive = new EffectExportBlend(true, nameof(Blend.One), nameof(Blend.One), nameof(BlendOperation.Add), true);
        if (tag == 0) return new(id, tag, -1, additive);
        try
        {
            var info = reader.Read(tag);
            var binding = info.Textures.Where(t => t.Stage == GameShaderStage.Pixel && t.DeclaredByShader
                    && t.ShaderDimension == "texture2d" && t.Depth <= 1
                    && t.Format is not (Vortice.DXGI.Format.BC5_UNorm or Vortice.DXGI.Format.BC5_SNorm
                        or Vortice.DXGI.Format.BC6H_Uf16 or Vortice.DXGI.Format.BC6H_Sf16))
                .OrderByDescending(t => t.IsSrgb).ThenBy(t => t.Slot).FirstOrDefault();
            if (binding is null) return new(id, tag, -1, additive);
            var data = new TextureDataReader(catalog).Read(binding.TextureTag);
            if (data.IsCube || data.IsVolume) return new(id, tag, -1, additive);
            var mip = data.Subresources.Where(s => s.ArraySlice == 0)
                .OrderBy(s => Math.Max(s.Width, s.Height) <= SpriteEdge ? 0 : 1).ThenByDescending(s => s.Width).First();
            var format = (Vortice.DXGI.Format)data.Format;
            bool single = format is Vortice.DXGI.Format.BC4_UNorm or Vortice.DXGI.Format.BC4_SNorm
                or Vortice.DXGI.Format.R8_UNorm or Vortice.DXGI.Format.A8_UNorm;
            var image = BundleTextureCodec.Decode(data, mip.MipLevel);
            string path = $"{folder}/sprites/{binding.TextureTag:X8}.png";
            string file = BundlePaths.Resolve(root, path);
            var sprite = sprites.FirstOrDefault(value => value.Path == path);
            if (sprite is null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                PngInterchange.Write(file, image);
                sprite = new(sprites.Count, binding.TextureTag, path, BundlePaths.FileHash(file), image.Width, image.Height, single,
                    binding.IsSrgb);
                sprites.Add(sprite);
            }
            // Bit 7 marks an authored blend state; the low bits index the native blend table.
            byte state = (byte)info.StateOverrides;
            var blend = additive;
            if ((state & 0x80) != 0 && (state & 0x7F) < states.BlendCount)
            {
                var target = states.GetBlend(state & 0x7F).RenderTarget[0];
                blend = new(target.BlendEnable, target.SourceBlend.ToString(), target.DestinationBlend.ToString(),
                    target.BlendOperation.ToString(), target.SourceBlend == Blend.One);
            }
            return new(id, tag, sprite.Id, blend);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException
            or KeyNotFoundException or ArgumentException or InvalidOperationException) { return new(id, tag, -1, additive); }
    }
}
