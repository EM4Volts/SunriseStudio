using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Interchange;

/** World scenes are read-only shared resources and placements, separate from editable asset bundles. */
public static partial class WorldSceneExport
{
    public const string Format = "sunrise-world-scene", ManifestName = "world.sunrise-scene.json";
    public const int Version = 1, InstanceStride = 128;
    private static readonly JsonSerializerOptions Json = new(BundlePaths.Json) { WriteIndented = false };
    /** @param Record Offset of a ground decorator record from the part's first record. */
    private sealed record Placement(MeshInstance Source, Matrix4x4 Transform, int Prototype, int Record = 0)
    {
        /** The part's decorator inputs by record; Record picks this placement's. */
        public IReadOnlyList<WorldDecoratorInputs>? Inputs { get; init; }
    }
    /** @param Inputs Key of the part's draw-time shader inputs; empty for none. */
    private readonly record struct MaterialKey(uint Tag, int Stage, uint TerrainDye, string Inputs);

    /** The caller supplies the exact authored map scope; live preview transforms are never baked. */
    public static WorldSceneExportResult Export(PackageCatalog catalog, SceneDocument scene, string outputFolder,
        WorldSceneExportOptions? options = null, CancellationToken token = default, Action<string>? progress = null)
    {
        options ??= new();
        string root = Path.GetFullPath(outputFolder);
        BundlePaths.RejectLinks(root);
        for (string? parent = root; parent is not null; parent = Path.GetDirectoryName(parent))
            if (File.Exists(Path.Combine(parent, StudioProject.ProjectFileName))) throw new IOException("Export world scenes outside projects.");
        if (BundlePaths.Within(root, catalog.GameRoot) || options.Project is not null && BundlePaths.Within(root, options.Project.DirectoryPath))
            throw new IOException("Export world scenes outside the installation and project.");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("World export needs an empty folder.");
        string parentFolder = Path.GetDirectoryName(root) ?? throw new IOException("World export needs a named folder.");
        Directory.CreateDirectory(parentFolder);
        string staging = Path.Combine(parentFolder, ".sunrise-world-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var manifest = Write(catalog, scene, staging, options, token, progress);
            token.ThrowIfCancellationRequested();
            BundlePaths.RejectLinks(root);
            if (Directory.Exists(root)) Directory.Delete(root);
            Directory.Move(staging, root);
            return new(Path.Combine(root, ManifestName), manifest);
        }
        finally
        {
            if (Directory.Exists(staging)) { BundlePaths.RejectLinks(staging); Directory.Delete(staging, true); }
        }
    }

    private static WorldSceneManifest Write(PackageCatalog catalog, SceneDocument scene, string root,
        WorldSceneExportOptions options, CancellationToken token, Action<string>? progress)
    {
        var clock = Stopwatch.StartNew(); var phases = new Dictionary<string, double>();
        long generation = catalog.ReadGeneration, overlay = catalog.OverlayRevision;
        string source = catalog.GetPackageFileIdentity(token);
        List<BundleNotice> notices = scene.Diagnostics.Select(message => new BundleNotice("Scene", "Source diagnostic", message)).ToList();
        if (scene.UnsupportedCount != 0 || scene.DecodeErrorCount != 0)
            notices.Add(new("Scene", "Partial source decode", $"Unsupported source entries: {scene.UnsupportedCount}; decode errors: {scene.DecodeErrorCount}."));
        notices.AddRange(scene.MaterialWarnings.Select(warning => new BundleNotice("Material", "Initial state fallback", warning.Message, warning.ConfigTag)));
        var instances = scene.Instances.Where(instance => instance.Mesh.Vertices.Length != 0 && instance.Mesh.Indices.Length != 0).ToArray();
        if (instances.Length != scene.Instances.Count)
            notices.Add(new("Geometry", "Empty source parts", $"{scene.Instances.Count - instances.Length} decoded parts contain no triangle geometry."));
        var meshes = instances.Select(instance => instance.Mesh).Distinct(ReferenceEqualityComparer.Instance).Cast<MeshData>().ToArray();
        var inputs = new Dictionary<string, BundleShaderInputs>(StringComparer.Ordinal);
        foreach (var value in options.ShaderInputs?.Values ?? []) inputs.TryAdd(value.Key, value);
        MaterialKey MaterialOf(MeshInstance instance) => WorldSceneExport.MaterialOf(instance.Mesh,
            options.ShaderInputs?.GetValueOrDefault(instance)?.Key ?? "");
        var materialKeys = instances.Select(MaterialOf).Distinct().ToArray();
        var (materials, textures) = Materials(catalog, materialKeys, inputs, root, options.IncludeMaterials, notices, token, progress);
        phases["materials_textures"] = clock.Elapsed.TotalSeconds; clock.Restart();
        var materialIds = materialKeys.Select((key, index) => (key, index)).ToDictionary(pair => pair.key, pair => pair.index);
        using var writer = new WorldBinaryWriter(root, token);
        List<WorldGeometry> geometry = []; Dictionary<string, WorldGeometry> shared = [];
        var geometryIds = new Dictionary<MeshData, int>(ReferenceEqualityComparer.Instance);
        var materialReader = new GameMaterialReader(catalog);
        foreach (var mesh in meshes)
        {
            token.ThrowIfCancellationRequested(); progress?.Invoke($"Writing shared geometry {geometryIds.Count + 1}/{meshes.Length}");
            var value = Geometry(catalog, mesh, geometry.Count, writer, materialReader, shared, notices);
            if (value.Id == geometry.Count) geometry.Add(value);
            geometryIds.Add(mesh, value.Id);
        }
        phases["geometry"] = clock.Elapsed.TotalSeconds; clock.Restart();
        var groups = new List<List<MeshInstance>>();
        var owners = new Dictionary<SceneObjectPlacement, List<MeshInstance>>(ReferenceEqualityComparer.Instance);
        // A ground cell's LOD meshes are one placement; the native draw picks one per record by distance.
        var cells = new Dictionary<(uint Payload, int Record, uint Model), List<MeshInstance>>();
        foreach (var instance in instances)
        {
            List<MeshInstance>? group;
            if (instance.ObjectPlacement is { } owner)
            {
                if (!owners.TryGetValue(owner, out group)) { group = []; owners.Add(owner, group); groups.Add(group); }
                group.Add(instance);
            }
            else if (instance.CellBounds is not null && instance.InstanceSourceTag != 0)
            {
                var cell = (instance.InstanceSourceTag, instance.InstanceSourceIndex, instance.Mesh.TagHash);
                if (!cells.TryGetValue(cell, out group)) { group = []; cells.Add(cell, group); groups.Add(group); }
                group.Add(instance);
            }
            else groups.Add([instance]);
        }
        // A model's meshes run from the most detailed and each has its own position stream; the rank is the LOD.
        var cellLods = new Dictionary<MeshInstance, int>(ReferenceEqualityComparer.Instance);
        foreach (var group in cells.Values)
        {
            var ranks = group.GroupBy(instance => instance.Mesh.Native?.VertexStreams.FirstOrDefault()?.HeaderTag ?? 0)
                .OrderBy(mesh => mesh.Min(instance => instance.Mesh.PartIndex)).ToArray();
            for (int rank = 0; rank < ranks.Length; rank++)
                foreach (var instance in ranks[rank]) cellLods.Add(instance, rank);
        }
        List<WorldPrototype> prototypes = []; Dictionary<string, int> prototypeIds = [];
        List<Placement> placements = [];
        var quantizations = new Dictionary<uint, byte[]?>();
        var oceans = new OceanHeights(catalog, scene.TagHash);
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            var first = group[0];
            var transform = first.ObjectPlacement?.WorldTransform ?? first.Transform;
            if (!Matrix4x4.Invert(transform, out var inverse)) { transform = Matrix4x4.Identity; inverse = Matrix4x4.Identity; }
            var parts = group.Select(instance =>
            {
                var mesh = instance.Mesh; int stage = mesh.Native?.RenderStage ?? -1;
                var placed = oceans.Place(instance);
                return new WorldPart(geometryIds[mesh], materialIds[MaterialOf(instance)],
                    mesh.TagHash, mesh.PartIndex, mesh.LogicalPartIndex, instance.ModelSourceConfigTag,
                    instance.ModelSourceResourceClass, instance.Name == first.Name ? "" : instance.Name,
                    BundleSceneWriter.MatrixValues(placed == transform ? Matrix4x4.Identity : placed * inverse),
                    cellLods.TryGetValue(instance, out int rank) ? rank : options.PartLods?.TryGetValue(instance, out int level) == true
                        ? level : mesh.LodLevel, stage, instance.InitiallyVisible, instance.InitialVisibilityReason,
                    instance.Layer.ToString());
            }).ToArray();
            string key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(parts)));
            if (!prototypeIds.TryGetValue(key, out int prototype))
            {
                prototype = prototypes.Count; prototypeIds.Add(key, prototype);
                prototypes.Add(new(prototype, parts, parts.All(part => !materials[part.Material].NeedsNativeBasis)));
            }
            var decorator = options.DecoratorInputs is { } known
                ? group.Select(instance => known.GetValueOrDefault(instance)).FirstOrDefault(value => value is not null) : null;
            placements.Add(new(first, transform, prototype) { Inputs = decorator });
            // A ground decorator cell draws one plant per record; its parts hold the first.
            if (cellLods.ContainsKey(first) && GroundRecords(catalog, first, quantizations) is { } records)
                placements.AddRange(records.Skip(1).Select((value, index) => new Placement(first, value, prototype, index + 1) { Inputs = decorator }));
        }
        oceans.Report(notices);
        List<string> names = []; Dictionary<string, int> nameIds = new(StringComparer.Ordinal);
        int Name(string value)
        {
            if (!nameIds.TryGetValue(value, out int index)) { index = names.Count; nameIds.Add(value, index); names.Add(value); }
            return index;
        }
        var table = writer.Records(placements.Count, InstanceStride, (index, row) =>
        {
            var placement = placements[index]; var item = placement.Source;
            row.I32(0, placement.Prototype); row.I32(4, Name(item.Name));
            var matrix = BundleSceneWriter.MatrixValues(placement.Transform);
            for (int lane = 0; lane < 16; lane++) row.F32(8 + lane * 4, matrix[lane]);
            row.U32(72, item.BubbleHash); row.U32(76, item.InstanceSourceTag); row.I32(80, item.InstanceSourceIndex + placement.Record);
            row.U32(84, item.PlacementSourceTag); row.I32(88, item.PlacementSourceOffset); row.U32(92, item.ObjectClassTag);
            row.U64(96, item.PlacementIdentity); row.U32(104, item.ModelSourceConfigTag); row.U32(108, item.ActivityTag);
            row.I32(112, item.StateOrdinal); row.I32(116, (int)item.Layer); row.I32(120, Name(item.StateName));
        });
        WorldBufferView? inputRows = null;
        if (placements.Any(placement => placement.Inputs is not null))
            inputRows = writer.Floats(placements.Count, 8, (index, lane) =>
            {
                var placement = placements[index];
                if (placement.Inputs is not { } values || placement.Record >= values.Count) return 0;
                var value = values[placement.Record];
                return lane < 4 ? value.Record[lane] : value.Occlusion[lane - 4];
            });
        writer.Finish(); phases["instances"] = clock.Elapsed.TotalSeconds;
        if (catalog.ReadGeneration != generation || catalog.OverlayRevision != overlay || catalog.GetPackageFileIdentity(token) != source)
            throw new InvalidDataException("Package sources changed during world export.");
        notices.Add(new("World scene", "Read-only", "Cannot be imported back."));
        WriteEffects(catalog, scene, root, notices, phases, token, progress);
        notices.Add(new("Materials", "Preview", "Lighting and some shader inputs are approximate."));
        WriteLights(catalog, scene, root, notices, phases, token, progress);
        var statistics = new WorldSceneStatistics(placements.Count, instances.Length, geometry.Count, prototypes.Count,
            materials.Count, textures.Count, geometry.Sum(value => (long)value.Positions.Count),
            geometry.Sum(value => (long)value.Indices.Count), writer.Buffers.Sum(buffer => buffer.Length), phases);
        var markers = scene.Markers.Select(marker => new WorldMarker(marker.Name, marker.TagHash, marker.SourceTag,
            marker.SourceOffset, marker.PlacementIdentity, marker.Layer.ToString(), marker.BubbleHash, marker.ActivityTag,
            marker.StateOrdinal, marker.StateName, [marker.Position.X, marker.Position.Y, marker.Position.Z],
            [marker.Rotation.X, marker.Rotation.Y, marker.Rotation.Z, marker.Rotation.W],
            [marker.Extents.X, marker.Extents.Y, marker.Extents.Z])).ToArray();
        var manifest = new WorldSceneManifest(Format, Version, scene.Name, scene.TagHash, "NativeZUp", "RowMajorRowVector",
            writer.Buffers.ToArray(), geometry, materials, textures, prototypes, names, new(table, InstanceStride) { Inputs = inputRows }, markers, notices, statistics);
        WriteJson(Path.Combine(root, ManifestName), manifest);
        return manifest;
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
        JsonSerializer.Serialize(stream, value, Json);
    }

    private static MaterialKey MaterialOf(MeshData mesh, string inputs) => new(mesh.MaterialTag, mesh.Native?.RenderStage ?? -1,
        mesh.Native is { Kind: NativeMeshKind.Terrain } native && native.TerrainDyeTexture != 0
            ? native.TerrainDyeTexture : uint.MaxValue, inputs);

    /**
     * Each record transform of a ground decorator cell, first record first.
     * @param quantizations Quantization bytes by payload; null for a payload that does not read.
     * @return Null when the part is not a ground cell.
     */
    private static Matrix4x4[]? GroundRecords(PackageCatalog catalog, MeshInstance instance, Dictionary<uint, byte[]?> quantizations)
    {
        var records = instance.NativeInstanceData;
        if (instance.CellBounds is null || instance.InstanceSourceTag == 0 || records.Length < 2 * DecoratorRecords.Size
            || records.Length % DecoratorRecords.Size != 0) return null;
        if (!quantizations.TryGetValue(instance.InstanceSourceTag, out byte[]? quantization))
        {
            try { quantization = catalog.Read(DecoratorTags.Read(catalog, instance.InstanceSourceTag).Quantization); }
            catch (Exception error) when (Recoverable(error)) { quantization = null; }
            quantizations.Add(instance.InstanceSourceTag, quantization);
        }
        if (quantization is null) return null;
        return Enumerable.Range(0, records.Length / DecoratorRecords.Size).Select(index => AssetSceneLoader.GroundTransform(
            records.Span.Slice(index * DecoratorRecords.Size, DecoratorRecords.Size), quantization) * instance.InstanceParent).ToArray();
    }

    private static bool Recoverable(Exception error) => error is IOException or InvalidDataException or NotSupportedException
        or ArgumentException or OverflowException or IndexOutOfRangeException or KeyNotFoundException;
}
